namespace Kkindle.Core;

/// <summary>
/// One optional reader body font. The catalog is code, not a remote manifest,
/// so the local/remote switch is one constant and the app can render the list
/// before any network request.
/// </summary>
public sealed record ReaderFontCatalogEntry(
    string Id,
    string DisplayName,
    string Description,
    string FileName,
    long Bytes,
    string Sha256,
    string License,
    string LicenseFile,
    string PostScriptName);

public static class ReaderFontCatalog
{
    /// <summary>
    /// Local development source. Debug iOS builds bundle this directory from
    /// <c>src/Kkindle.App/Assets/Fonts/catalog</c> when the fetch script has
    /// populated it. Keep it in sync with <see cref="RemoteBaseUrl"/>.
    /// </summary>
    public const string LocalBaseUrl = "local://Assets/Fonts/catalog/";

    /// <summary>
    /// Production font host. Set this once the files are uploaded, for example
    /// <c>https://cdn.example.com/kkindle/reader-fonts/</c>. The remote
    /// directory must contain the exact file names below plus the license
    /// files under <c>licenses/</c>.
    /// </summary>
    public const string RemoteBaseUrl = "";

    public static string BaseUrl => string.IsNullOrWhiteSpace(RemoteBaseUrl)
        ? LocalBaseUrl
        : RemoteBaseUrl;

    public static IReadOnlyList<ReaderFontCatalogEntry> Entries { get; } =
    [
        new(
            "source-han-serif-sc",
            "思源宋体",
            "宋体 · 长篇正文",
            "NotoSerifCJKsc-Regular.otf",
            24543080,
            "2a2eae2628df83556c54018c41e20fa532c1b862c5256ae8b3f23feb918d12ca",
            "OFL-1.1",
            "licenses/OFL-Noto.txt",
            "NotoSerifCJKsc-Regular"),
        new(
            "lxgw-wenkai-lite",
            "霞鹜文楷",
            "楷体 · 小说散文",
            "LXGWWenKaiLite-Regular.ttf",
            13949472,
            "f799a538a3355877e597a0ae04050e897932c9ae7689eb9227c2953c5dd549ea",
            "OFL-1.1",
            "licenses/OFL-LXGWWenKai.txt",
            "LXGWWenKaiLite-Regular"),
        new(
            "zhuque-fangsong",
            "朱雀仿宋",
            "仿宋 · 古典阅读",
            "ZhuqueFangsong-Regular.ttf",
            8824084,
            "558c62730844fe54ba220146ed62f859d4e2880188d92d985f8921c6e3743bc4",
            "OFL-1.1",
            "licenses/OFL-Zhuque.txt",
            "ZhuqueFangsong-Regular"),
        new(
            "source-han-sans-sc",
            "思源黑体",
            "黑体 · 现代屏幕",
            "NotoSansCJKsc-Regular.otf",
            16437364,
            "2c76254f6fc379fddfce0a7e84fb5385bb135d3e399294f6eeb6680d0365b74b",
            "OFL-1.1",
            "licenses/OFL-Noto.txt",
            "NotoSansCJKsc-Regular"),
    ];

    public static ReaderFontCatalogEntry? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Entries.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string ResolveUrl(ReaderFontCatalogEntry entry) => BaseUrl + entry.FileName;

    /// <summary>
    /// Resolves <c>local://</c> URLs to the app bundle. The path is constrained
    /// to the bundle root so a catalog entry can never escape it.
    /// </summary>
    public static string? ResolveLocalPath(string url)
    {
        const string prefix = "local://";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = url[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relative)
            || Path.IsPathRooted(relative)
            || relative.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ? path : null;
    }
}
