using System.Runtime.InteropServices;
using HarfBuzzSharp;
using Libzora.Core;
using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Owns the bundled font faces. HarfBuzz faces are created per file and reused;
/// glyph indices HarfBuzz reports are valid for Skia because both libraries
/// read the same font file.
/// </summary>
public sealed partial class TypesetFontLibrary : IDisposable
{
    private sealed class FontFaceEntry : IDisposable
    {
        public required string Path { get; init; }
        public required Blob Blob { get; init; }
        public required Face Face { get; init; }
        public required Font Font { get; init; }
        public required SKTypeface Typeface { get; init; }
        public required int Upem { get; init; }

        public void Dispose()
        {
            Font.Dispose();
            Face.Dispose();
            Blob.Dispose();
            Typeface.Dispose();
        }
    }

    private readonly Dictionary<string, FontFaceEntry> _faces = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();
    private readonly List<string> _fallbackCandidates = new();
    private int _activeCompositions;
    private HashSet<string> _retainedFontPaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _deferredReleaseKeys;
    private bool _disposed;

    public string MainFontPath { get; private set; }
    /// <summary>Optional paired Latin face for horizontal Latin/numeric runs.</summary>
    public string? LatinFontPath { get; private set; }
    /// <summary>Lazily resolved platform monospace face for code/preformatted runs.</summary>
    public string? MonospaceFontPath { get; private set; }
    private bool _monospaceProbed;
    private readonly Dictionary<(string BaseKey, bool Bold, bool Italic), string?> _styledFontCache = new();

    /// <summary>
    /// System families currently held in memory. Exposed for diagnostics and
    /// tests so a font switch can prove the previous face was released.
    /// </summary>
    public IReadOnlyList<string> LoadedSystemFontKeys
    {
        get
        {
            lock (_faces)
            {
                return _order.Where(IsSystemFontKey).ToArray();
            }
        }
    }

    /// <summary>
    /// Marks a composition in progress. Releasing platform faces is deferred
    /// while a composer still holds them, so a font switch cannot dispose
    /// bytes needed by a background layout or table preview. The counter is
    /// guarded by the face registry lock so a new composition can never start
    /// between the final decrement and a deferred release.
    /// </summary>
    public IDisposable BeginComposition()
    {
        lock (_faces)
        {
            _activeCompositions++;
        }
        return new CompositionScope(this);
    }

    private sealed class CompositionScope(TypesetFontLibrary library) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                library.EndComposition();
            }
        }
    }

    private void EndComposition()
    {
        List<FontFaceEntry> released = [];
        lock (_faces)
        {
            if (--_activeCompositions > 0)
            {
                return;
            }

            if (_deferredReleaseKeys is { Count: > 0 } keys)
            {
                CollectReleasedLocked(keys, MainFontPath, released);
            }

            _deferredReleaseKeys = null;
        }

        foreach (var entry in released)
        {
            entry.Dispose();
        }
    }

    /// <summary>
    /// Replaces the faces borrowed by the reader's installed layout. A former
    /// main face can still supply missing glyphs in the new layout, so it must
    /// survive both immediate and deferred platform-font cleanup. Call while
    /// holding a composition scope when replacing a layout.
    /// </summary>
    public void SetRetainedFontPaths(IEnumerable<string> fontPaths)
    {
        var retained = fontPaths.Where(IsSystemFontKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_faces)
        {
            _retainedFontPaths = retained;
        }
    }

    /// <summary>
    /// Releases platform faces other than the reader's main font and faces
    /// referenced by its installed layout. A system face can occupy tens of
    /// megabytes (PingFang ships as a ~59 MB
    /// TTC), so switching families must not keep every earlier face alive for
    /// the rest of the session. The preferred face is re-opened on demand if
    /// the user switches back. The current main face is always kept, even
    /// when <paramref name="keepKey"/> is null.
    /// </summary>
    public void ReleaseSystemFontsExcept(string? keepKey)
    {
        List<FontFaceEntry> released = [];
        lock (_faces)
        {
            if (_disposed)
            {
                return;
            }

            var keep = string.IsNullOrEmpty(keepKey) ? MainFontPath : keepKey;
            if (_activeCompositions > 0)
            {
                // A composer may still look these faces up by key; keep them
                // registered and only drop the bytes when the last one ends.
                _deferredReleaseKeys ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in _order)
                {
                    if (IsSystemFontKey(key)
                        && !string.Equals(key, keep, StringComparison.OrdinalIgnoreCase))
                    {
                        _deferredReleaseKeys.Add(key);
                    }
                }
                return;
            }

            CollectReleasedLocked(null, keep, released);
        }

        foreach (var entry in released)
        {
            entry.Dispose();
        }
    }

    /// <summary>Removes matching faces from the registry; caller disposes.</summary>
    private void CollectReleasedLocked(IReadOnlyCollection<string>? onlyKeys, string keep,
        List<FontFaceEntry> released)
    {
        for (var index = _order.Count - 1; index >= 0; index--)
        {
            var key = _order[index];
            if (!IsSystemFontKey(key)
                || IsProtectedSystemKey(key, keep)
                || _retainedFontPaths.Contains(key)
                || (onlyKeys is not null && !onlyKeys.Contains(key)))
            {
                continue;
            }

            if (_faces.Remove(key, out var entry))
            {
                _order.RemoveAt(index);
                released.Add(entry);
            }
        }
        PurgeMissingStyledCacheLocked();
    }

    /// <summary>
    /// A stable key for a face exposed by the platform font manager. The
    /// key is stored in reader preferences; the face itself is loaded on
    /// demand and never written to disk.
    /// </summary>
    public static string SystemFontKey(string family) => ReaderFontDefaults.SystemFamilyPrefix + family.Trim();

    public static bool IsSystemFontKey(string? fontPath) =>
        fontPath is not null
        && fontPath.StartsWith(ReaderFontDefaults.SystemFamilyPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Paths that may be tried when the main face does not cover a glyph.
    /// They are registered on first use: symbol and emoji faces can be large
    /// (iOS also exposes a 190 MB color emoji face on macOS), and most pages
    /// never need them.
    /// </summary>
    public IReadOnlyList<string> FallbackCandidates => _fallbackCandidates;

    public TypesetFontLibrary(string mainFontPath, IEnumerable<string>? fallbackFontPaths = null)
    {
        MainFontPath = mainFontPath;
        Register(mainFontPath);
        if (fallbackFontPaths is not null)
        {
            foreach (var path in fallbackFontPaths)
            {
                AddFallbackCandidate(path);
            }
        }
    }

    private void AddFallbackCandidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || string.Equals(path, MainFontPath, StringComparison.OrdinalIgnoreCase)
            || _fallbackCandidates.Contains(path, StringComparer.OrdinalIgnoreCase)
            || !File.Exists(path))
        {
            return;
        }

        _fallbackCandidates.Add(path);
    }

    /// <summary>
    /// Registers one fallback face on demand. A malformed or unreadable font
    /// is ignored rather than failing the whole chapter.
    /// </summary>
    public bool TryEnsureFont(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (ContainsFace(path))
        {
            return true;
        }

        if (!File.Exists(path))
        {
            return false;
        }

        FontFaceEntry entry;
        try
        {
            entry = CreateFileEntry(path);
        }
        catch (Exception)
        {
            return false;
        }

        return TryAddFace(path, entry);
    }

    /// <summary>
    /// Registers a face exposed by the platform font manager, without a font
    /// file on disk. iOS exposes its built-in CJK faces only through CoreText;
    /// the sandbox cannot read the system font directory, so the face data is
    /// obtained through <see cref="SKTypeface.OpenStream"/>.
    /// </summary>
    public bool TryEnsureSystemFont(string family, out string key)
    {
        key = SystemFontKey(family);
        if (_disposed || string.IsNullOrWhiteSpace(family) || ContainsFace(key))
        {
            return !_disposed && !string.IsNullOrWhiteSpace(family);
        }

        FontFaceEntry? entry;
        try
        {
            entry = CreateSystemEntry(family);
        }
        catch (Exception)
        {
            entry = null;
        }

        if (entry is null)
        {
            key = string.Empty;
            return false;
        }

        if (!TryAddFace(key, entry))
        {
            key = string.Empty;
            return false;
        }

        return true;
    }

    private bool ContainsFace(string path)
    {
        lock (_faces)
        {
            return _faces.ContainsKey(path);
        }
    }

    private bool TryAddFace(string path, FontFaceEntry entry)
    {
        lock (_faces)
        {
            if (_disposed)
            {
                entry.Dispose();
                return false;
            }

            if (_faces.ContainsKey(path))
            {
                entry.Dispose();
                return true;
            }

            _faces[path] = entry;
            _order.Add(path);
            return true;
        }
    }

    private static FontFaceEntry CreateFileEntry(string path)
    {
        var blob = HarfBuzzSharp.Blob.FromFile(path);
        var face = new Face(blob, 0);
        var font = new Font(face);
        var typeface = SKTypeface.FromFile(path, 0);
        // Scale stays at units-per-em; the shaper converts to pixels so the
        // conversion is one explicit float multiply and identical on all
        // platforms. Upem comes from the same file Skia reads.
        var upem = typeface.UnitsPerEm;
        font.SetScale(upem, upem);
        return new FontFaceEntry
        {
            Path = path,
            Blob = blob,
            Face = face,
            Font = font,
            Typeface = typeface,
            Upem = upem,
        };
    }

    private static FontFaceEntry? CreateSystemEntry(string family, SKFontStyle? style = null, string? explicitKey = null)
    {
        var trimmed = family.Trim();
        var typeface = style is null
            ? SKFontManager.Default.MatchFamily(trimmed)
            : SKFontManager.Default.MatchFamily(trimmed, style);
        if (typeface is null)
        {
            return null;
        }

        try
        {
            var upem = typeface.UnitsPerEm;
            using var stream = typeface.OpenStream(out var ttcIndex);
            if (upem <= 0 || stream is null || stream.Length is <= 0 or > int.MaxValue)
            {
                typeface.Dispose();
                return null;
            }

            var length = (int)stream.Length;
            var bytes = new byte[length];
            stream.Rewind();
            if (stream.Read(bytes, length) != length)
            {
                typeface.Dispose();
                return null;
            }

            // HarfBuzzSharp's Blob.FromStream pins the managed array only for
            // the duration of that call, then hands HarfBuzz a pointer the GC
            // may move. Copy the face into unmanaged memory that the blob owns
            // and frees when the face is disposed.
            var data = Marshal.AllocHGlobal(length);
            HarfBuzzSharp.Blob? blob = null;
            try
            {
                Marshal.Copy(bytes, 0, data, length);
                blob = new HarfBuzzSharp.Blob(
                    data, length, MemoryMode.ReadOnly, () => Marshal.FreeHGlobal(data));
                var face = new Face(blob, ttcIndex);
                var font = new Font(face);
                font.SetScale(upem, upem);
                return new FontFaceEntry
                {
                    Path = explicitKey ?? SystemFontKey(trimmed),
                    Blob = blob,
                    Face = face,
                    Font = font,
                    Typeface = typeface,
                    Upem = upem,
                };
            }
            catch
            {
                if (blob is null)
                {
                    Marshal.FreeHGlobal(data);
                }
                else
                {
                    blob.Dispose();
                }

                throw;
            }
        }
        catch
        {
            typeface.Dispose();
            throw;
        }
    }

    private void Register(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || ContainsFace(path))
        {
            return;
        }

        TryAddFace(path, CreateFileEntry(path));
    }

    public SKTypeface GetTypeface(string fontPath) => Entry(fontPath).Typeface;

    public int GetUpem(string fontPath) => Entry(fontPath).Upem;

    /// <summary>
    /// Selects the primary face used by the layout engine. File-backed faces
    /// stay available as glyph fallbacks; platform faces other than the main
    /// one can be released to reclaim their memory (see
    /// <see cref="ReleaseSystemFontsExcept"/>).
    /// </summary>
    public bool TrySetMainFont(string fontPath)
    {
        if (_disposed || string.IsNullOrWhiteSpace(fontPath))
        {
            return false;
        }

        lock (_faces)
        {
            if (!_faces.ContainsKey(fontPath))
            {
                return false;
            }

            MainFontPath = fontPath;
            return true;
        }
    }

    internal Font GetHarfBuzzFont(string fontPath) => Entry(fontPath).Font;

    internal IEnumerable<string> FontPaths
    {
        get
        {
            lock (_faces)
            {
                return _order.ToArray();
            }
        }
    }

    private FontFaceEntry Entry(string fontPath)
    {
        lock (_faces)
        {
            return _faces.TryGetValue(fontPath, out var entry)
                ? entry
                : throw new InvalidOperationException($"Font not registered: {fontPath}");
        }
    }

    public void Dispose()
    {
        List<FontFaceEntry> entries;
        lock (_faces)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = _faces.Values.ToList();
            _deferredReleaseKeys = null;
            _retainedFontPaths.Clear();
            _styledFontCache.Clear();
            LatinFontPath = null;
            MonospaceFontPath = null;
            _faces.Clear();
            _order.Clear();
        }

        foreach (var entry in entries)
        {
            entry.Dispose();
        }
    }
}

public sealed class ShapedText
{
    public required ushort[] GlyphIds { get; init; }
    /// <summary>Per-glyph index into the shaped substring.</summary>
    public required int[] Clusters { get; init; }
    /// <summary>Per-glyph advance in pixels along the shaping direction.</summary>
    public required float[] Advances { get; init; }
    public required float[] OffsetsX { get; init; }
    public required float[] OffsetsY { get; init; }
    public required float TotalAdvance { get; init; }

    public bool ContainsNotdef => Array.IndexOf(GlyphIds, (ushort)0) >= 0;
}

/// <summary>
/// Shapes text with HarfBuzz and converts advances to device-independent
/// pixels. The same input, font file and library versions always produce the
/// same output regardless of platform, which is the basis of cross-platform
/// pagination equality.
/// </summary>
public sealed class GlyphShaper
{
    private readonly TypesetFontLibrary _library;

    // HarfBuzz normally enables `vert` as a side effect of a vertical buffer
    // direction. Keep it explicit so vertical presentation forms are selected
    // consistently when the same EPUB is laid out on different hosts.
    private static readonly Feature VerticalFeature = Feature.Parse("vert");
    // Proportional alternate widths let CJK punctuation occupy its ink instead
    // of a full em box. Fonts without the feature simply ignore it; `vpal` is
    // the vertical counterpart.
    private static readonly Feature ProportionedFeature = Feature.Parse("palt");
    private static readonly Feature VerticalProportionedFeature = Feature.Parse("vpal");

    public GlyphShaper(TypesetFontLibrary library)
    {
        _library = library;
    }

    /// <summary>
    /// Shapes <paramref name="text"/> with the main face, then with any
    /// registered or lazily loadable fallback that covers the whole unit.
    /// Returns false when every face leaves at least one .notdef glyph; the
    /// caller may then split the unit into text elements and retry.
    /// </summary>
    public bool TryShapeWithFallback(
        string text,
        float fontSize,
        bool vertical,
        out string fontPath,
        out ShapedText shaped) =>
        TryShapeWithFallback(text, fontSize, vertical, preferredFontPath: null, out fontPath, out shaped);

    /// <summary>
    /// Shapes <paramref name="text"/> starting from <paramref name="preferredFontPath"/>
    /// (when supplied), then falling back through the main face and fallback faces.
    /// </summary>
    public bool TryShapeWithFallback(
        string text,
        float fontSize,
        bool vertical,
        string? preferredFontPath,
        out string fontPath,
        out ShapedText shaped)
    {
        var primary = !string.IsNullOrWhiteSpace(preferredFontPath) && _library.HasRegisteredFace(preferredFontPath)
            ? preferredFontPath
            : _library.MainFontPath;
        shaped = Shape(text, 0, text.Length, primary, fontSize, vertical);
        if (!shaped.ContainsNotdef)
        {
            fontPath = primary;
            return true;
        }

        if (!string.Equals(primary, _library.MainFontPath, StringComparison.OrdinalIgnoreCase))
        {
            var mainRetry = Shape(text, 0, text.Length, _library.MainFontPath, fontSize, vertical);
            if (!mainRetry.ContainsNotdef)
            {
                fontPath = _library.MainFontPath;
                shaped = mainRetry;
                return true;
            }
        }

        foreach (var fallback in _library.FontPaths)
        {
            if (string.Equals(fallback, primary, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fallback, _library.MainFontPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Styled variants (bold/italic) are chosen explicitly, not as generic
            // glyph fallbacks for unrelated regular characters.
            if (TypesetFontLibrary.IsStyledVariantKey(fallback))
            {
                continue;
            }

            var retry = Shape(text, 0, text.Length, fallback, fontSize, vertical);
            if (!retry.ContainsNotdef)
            {
                fontPath = fallback;
                shaped = retry;
                return true;
            }
        }

        foreach (var candidate in _library.FallbackCandidates)
        {
            if (!_library.TryEnsureFont(candidate))
            {
                continue;
            }

            var retry = Shape(text, 0, text.Length, candidate, fontSize, vertical);
            if (!retry.ContainsNotdef)
            {
                fontPath = candidate;
                shaped = retry;
                return true;
            }
        }

        fontPath = primary;
        return false;
    }

    public ShapedText Shape(
        string text,
        int start,
        int length,
        string fontPath,
        float fontSize,
        bool vertical = false)
    {
        if (length <= 0)
        {
            return new ShapedText
            {
                GlyphIds = Array.Empty<ushort>(),
                Clusters = Array.Empty<int>(),
                Advances = Array.Empty<float>(),
                OffsetsX = Array.Empty<float>(),
                OffsetsY = Array.Empty<float>(),
                TotalAdvance = 0f,
            };
        }

        var font = _library.GetHarfBuzzFont(fontPath);
        var upem = _library.GetUpem(fontPath);

        using var buffer = new HarfBuzzSharp.Buffer();
        buffer.AddUtf16(text.AsSpan(start, length));
        buffer.GuessSegmentProperties();
        buffer.Direction = vertical ? Direction.TopToBottom : Direction.LeftToRight;
        if (vertical)
        {
            font.Shape(buffer, new[] { VerticalFeature, VerticalProportionedFeature });
        }
        else
        {
            font.Shape(buffer, new[] { ProportionedFeature });
        }
        var infos = buffer.GlyphInfos.ToArray();
        var positions = buffer.GlyphPositions.ToArray();
        var count = infos.Length;
        var glyphIds = new ushort[count];
        var clusters = new int[count];
        var advances = new float[count];
        var offsetsX = new float[count];
        var offsetsY = new float[count];
        var scale = fontSize / upem;
        float total = 0f;

        for (var i = 0; i < count; i++)
        {
            var info = infos[i];
            var position = positions[i];
            glyphIds[i] = (ushort)info.Codepoint;
            clusters[i] = (int)info.Cluster;
            var advance = vertical ? position.YAdvance : position.XAdvance;
            advances[i] = advance * scale;
            offsetsX[i] = position.XOffset * scale;
            offsetsY[i] = position.YOffset * scale;
            total += advances[i];
        }

        return new ShapedText
        {
            GlyphIds = glyphIds,
            Clusters = clusters,
            Advances = advances,
            OffsetsX = offsetsX,
            OffsetsY = offsetsY,
            TotalAdvance = total,
        };
    }

    /// <summary>
    /// Resolves the glyph used for one upright vertical cell. Shaping a single
    /// character with a top-to-bottom direction selects the font's vertical
    /// presentation form (the OpenType vert feature) when it has one.
    /// </summary>
    public ushort GetVerticalGlyphId(char character, string fontPath, out bool isNotdef) =>
        GetVerticalGlyphId(character.ToString(), fontPath, out isNotdef);

    public ushort GetVerticalGlyphId(string text, string fontPath, out bool isNotdef)
    {
        var shaped = Shape(text, 0, text.Length, fontPath, 1000f, vertical: true);
        isNotdef = shaped.GlyphIds.Length == 0 || shaped.GlyphIds[0] == 0;
        return shaped.GlyphIds.Length == 0 ? (ushort)0 : shaped.GlyphIds[0];
    }

    /// <summary>
    /// True when a standalone punctuation mark has no usable vertical
    /// presentation glyph in the selected face and must be rotated.
    /// </summary>
    public bool NeedsVerticalRotation(char character, string fontPath) =>
        NeedsVerticalRotation(character.ToString(), fontPath);

    public bool NeedsVerticalRotation(string text, string fontPath)
    {
        if (!TypesetText.TryGetFirstScalar(text, out var scalar))
        {
            return false;
        }

        // This engine deliberately keeps the established reader behavior for
        // isolated ASCII digits: one digit is an upright cell and two digits
        // are tate-chu-yoko. Longer numeric/Latin runs are already marked as
        // sideways by VerticalTextUnits before this method is reached.
        if (scalar <= char.MaxValue && char.IsAsciiLetterOrDigit((char)scalar))
        {
            return false;
        }

        if (!TypesetText.IsVerticalTransformed(text))
        {
            // UAX #50 defaults unlisted non-CJK characters (including ASCII
            // semicolon, tilde, brackets and U+2014) to R: rotate them 90°
            // clockwise in mixed vertical text. CJK-native marks are upright
            // unless they are one of the explicit rotated fullwidth forms.
            return TypesetText.ShouldRotateInVertical(text);
        }

        // Tr characters are upright only after a typographic vertical
        // transformation. If the font has no alternate glyph, use the CSS
        // fallback of a clockwise rotation rather than leaving a horizontal
        // glyph in the vertical line.
        var horizontal = Shape(text, 0, text.Length, fontPath, 1000f, vertical: false);
        var vertical = Shape(text, 0, text.Length, fontPath, 1000f, vertical: true);
        if (vertical.GlyphIds.Length == 0 || vertical.GlyphIds[0] == 0)
        {
            return true;
        }

        return horizontal.GlyphIds.Length == 0
            || horizontal.GlyphIds[0] == 0
            || horizontal.GlyphIds.SequenceEqual(vertical.GlyphIds);
    }

    /// <summary>True when the font file covers the character (non-notdef glyph).</summary>
    public bool Covers(string fontPath, char character) =>
        Covers(fontPath, character.ToString());

    public bool Covers(string fontPath, string text)
    {
        var shaped = Shape(text, 0, text.Length, fontPath, 1000f, vertical: false);
        return shaped.GlyphIds.Length > 0 && shaped.GlyphIds[0] != 0;
    }

}
