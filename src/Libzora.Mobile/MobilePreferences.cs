using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Localization;

namespace Libzora.Mobile;

public enum MobileThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}

public enum MobileBookSort
{
    Recent = 0,
    Title = 1,
    Author = 2,
    Added = 3
}

public enum MobilePageAnimation
{
    None = 0,
    Fade = 1,
    Slide = 2,
    Wave = 3
}

public enum MobileReaderProgressDisplay
{
    Pages = 0,
    Chapters = 1,
    BookTimeRemaining = 3,
    Off = 4
}

/// <summary>Battery-saving mode for mobile reading.</summary>
public enum MobileReaderPowerSavingMode
{
    Off = 0,
    System = 1,
    On = 2
}

/// <summary>Phone-only interface choices; shared library data stays in the database.</summary>
public sealed record MobilePreferences
{
    /// <summary>auto follows OS preferences; manual choices are zh-CN or en-US.</summary>
    public string UiLanguage { get; init; } = MobileUiLanguageService.System;
    public MobileThemeMode ThemeMode { get; init; }
    public bool ListView { get; init; }
    /// <summary>Grid gallery: show covers only, hiding title, author and progress.</summary>
    public bool GalleryMode { get; init; }
    /// <summary>Use colours sampled from book covers for the native app background.</summary>
    public bool ColorfulMode { get; init; }
    public bool ShowBookSyncStatus { get; init; }
    /// <summary>Overlay each book's rating on its cover in the library.</summary>
    public bool ShowBookRating { get; init; }
    public bool StackBookSeries { get; init; }
    public bool CollectionsView { get; init; }
    public double PdfZoom { get; init; } = 1;
    /// <summary>PDF display mode: 0 = SinglePage, 1 = Continuous, 2 = TwoPage.</summary>
    public int PdfDisplayMode { get; init; }
    /// <summary>PDF fit mode: 0 = Page, 1 = Width.</summary>
    public int PdfFitMode { get; init; }
    /// <summary>Trim outer white margins on PDF pages automatically.</summary>
    public bool PdfAutoCrop { get; init; }
    /// <summary>Per-book remembered PDF zoom factors keyed by bookFileId.</summary>
    public Dictionary<string, double>? PdfBookZooms { get; init; }
    public MobileBookSort Sort { get; init; }
    public ReaderTheme ReaderTheme { get; init; }
    /// <summary>Follow the app's effective light or dark appearance for reader paper.</summary>
    public bool ReaderThemeFollowsAppearance { get; init; } = true;
    public double ReaderFontScale { get; init; } = 1.35;
    /// <summary>
    /// Bundled family name, <c>system:Family</c> for a platform face, or
    /// <c>catalog:id</c> for a downloaded reader font.
    /// </summary>
    public string ReaderFontFamily { get; init; } = ReaderFontDefaults.DefaultFamily;
    /// <summary>
    /// Optional paired Latin reading font (<c>auto</c> follows <see cref="ReaderFontFamily"/>,
    /// or <c>system:Family</c> for a platform Latin face such as Georgia or Charter).
    /// </summary>
    public string ReaderLatinFontFamily { get; init; } = "auto";
    public double ReaderLineHeight { get; init; } = 1.75;
    public double ReaderParagraphSpacing { get; init; } = ReaderLayoutDefaults.DefaultParagraphSpacing;
    /// <summary>Side margin of the reading column, in device-independent pixels.</summary>
    public double ReaderBodyPadding { get; init; } = ReaderLayoutDefaults.DefaultBodyPadding;
    /// <summary>Indent the first line of each paragraph by two characters.</summary>
    public bool ReaderParagraphIndent { get; init; }
    /// <summary>Break long English words at syllable boundaries with a display-only hyphen.</summary>
    public bool ReaderHyphenation { get; init; } = true;
    /// <summary>Left-align horizontal body prose instead of justifying both edges.</summary>
    public bool ReaderAlignLeft { get; init; }
    /// <summary>Insert a subtle visual gap between adjacent CJK and Latin/numeric runs.</summary>
    public bool ReaderCjkLatinSpacing { get; init; } = true;
    /// <summary>Keep at least two lines together at paragraph page boundaries.</summary>
    public bool ReaderAvoidWidowsAndOrphans { get; init; } = true;
    /// <summary>Vertical right-to-left typesetting, the traditional CJK layout.</summary>
    public bool ReaderVerticalWriting { get; init; }
    public MobilePageAnimation ReaderPageAnimation { get; init; } = MobilePageAnimation.Fade;
    public MobileReaderProgressDisplay ReaderProgressDisplay { get; init; } = MobileReaderProgressDisplay.Pages;
    /// <summary>Power-saving mode during reading: Off, follow OS Low Power Mode (default), or always On.</summary>
    public MobileReaderPowerSavingMode ReaderPowerSavingMode { get; init; } = MobileReaderPowerSavingMode.System;

    public bool ResolvePowerSaving(bool systemLowPowerMode) => ReaderPowerSavingMode switch
    {
        MobileReaderPowerSavingMode.On => true,
        MobileReaderPowerSavingMode.System => systemLowPowerMode,
        _ => false
    };

    /// <summary>
    /// Stored values can outlive a retired option. Value 2 used to mean the
    /// current chapter's remaining time; it now maps to the whole-book pace
    /// estimate so an upgrade never falls back to a chapter-only guess.
    /// </summary>
    public static MobileReaderProgressDisplay NormalizeProgressDisplay(
        MobileReaderProgressDisplay value) => value switch
    {
        (MobileReaderProgressDisplay)2 => MobileReaderProgressDisplay.BookTimeRemaining,
        _ when Enum.IsDefined(value) => value,
        _ => MobileReaderProgressDisplay.Pages
    };

    public ReaderTheme ResolveReaderTheme(bool darkAppearance) => ReaderThemeFollowsAppearance
        ? (darkAppearance ? Libzora.Core.ReaderTheme.Ink : Libzora.Core.ReaderTheme.Classic)
        : ReaderTheme;

    public double GetEffectivePdfZoom(Guid? bookFileId)
    {
        if (bookFileId is { } id && PdfBookZooms is { Count: > 0 } map
            && map.TryGetValue(id.ToString("N"), out var saved) && double.IsFinite(saved))
        {
            return Math.Clamp(saved, 1, 4);
        }
        return double.IsFinite(PdfZoom) ? Math.Clamp(PdfZoom, 1, 4) : 1;
    }

    public MobilePreferences WithPdfZoom(Guid? bookFileId, double zoom)
    {
        var clamped = double.IsFinite(zoom) ? Math.Clamp(zoom, 1, 4) : 1;
        if (bookFileId is not { } id)
        {
            return this with { PdfZoom = clamped };
        }
        var updated = PdfBookZooms is not null
            ? new Dictionary<string, double>(PdfBookZooms, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var key = id.ToString("N");
        if (Math.Abs(clamped - 1) < 0.01) updated.Remove(key);
        else updated[key] = clamped;
        while (updated.Count > 64)
        {
            var oldest = updated.Keys.First();
            updated.Remove(oldest);
        }
        return this with { PdfZoom = clamped, PdfBookZooms = updated };
    }
}

/// <summary>
/// Reads on the UI thread at startup, so it stays a small synchronous JSON file.
/// A damaged file only loses interface choices and must never block launch.
/// </summary>
public sealed class MobilePreferencesStore(AppPaths paths)
{
    private readonly string _path = Path.Combine(paths.Root, "mobile-preferences.json");

    public MobilePreferences Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_path));
                var root = document.RootElement;
                var current = root.Deserialize<MobilePreferences>() ?? new();
                // Older settings cannot tell whether the paper was chosen manually.
                // Preserve their existing appearance instead of changing it on upgrade.
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty(nameof(MobilePreferences.ReaderTheme), out _)
                    && !root.TryGetProperty(nameof(MobilePreferences.ReaderThemeFollowsAppearance), out _))
                    current = current with { ReaderThemeFollowsAppearance = false };
                Current = current with
                {
                    UiLanguage = MobileUiLanguageService.NormalizePreference(current.UiLanguage),
                    ReaderProgressDisplay = MobilePreferences.NormalizeProgressDisplay(
                        current.ReaderProgressDisplay)
                };
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException
            or UnauthorizedAccessException)
        {
            Current = new();
        }
    }

    public void Update(Func<MobilePreferences, MobilePreferences> change)
    {
        var changed = change(Current);
        Current = changed with { UiLanguage = MobileUiLanguageService.NormalizePreference(changed.UiLanguage) };
        try
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Current));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The choice still applies for this session.
        }
    }
}
