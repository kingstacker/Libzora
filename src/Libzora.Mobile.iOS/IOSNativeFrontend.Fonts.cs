#if LIBZORA_SWIFT_UI
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Layout;
using Libzora.Mobile;
using SkiaSharp;

namespace Libzora.iOSHead;

internal sealed partial class IOSNativeFrontend
{
    private async Task<object> GetReaderFontsAsync(CancellationToken token)
    {
        var library = _shell.Modules.Library.Fonts;
        var installed = await library.ListAsync(token);
        var installedIds = installed
            .Select(font => font.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var catalogPostScriptNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ReaderFontCatalog.Entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.PostScriptName))
            {
                catalogPostScriptNames.Add(entry.PostScriptName);
            }
        }

        var options = new List<ReaderFontOption>
        {
            new(
                ReaderFontDefaults.DefaultFamily,
                "京华老宋体",
                "内置 · 默认",
                "builtin",
                true,
                0,
                null,
                ReaderFontDefaults.BundledFamily,
                null),
        };

        var systemFonts = await Task.Run(() => SystemFontCatalog.CjkFonts, token);
        foreach (var face in systemFonts)
        {
            if (catalogPostScriptNames.Contains(face.PostScriptName))
            {
                continue;
            }

            options.Add(new ReaderFontOption(
                ReaderFontDefaults.SystemFamilyPrefix + face.Family,
                face.DisplayName,
                "系统 · " + face.Family,
                "system",
                true,
                0,
                null,
                face.PostScriptName,
                null));
        }

        foreach (var entry in ReaderFontCatalog.Entries)
        {
            var installedFont = installed.FirstOrDefault(font =>
                string.Equals(font.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            var isInstalled = installedFont is not null
                && ReaderFontRegistry.IsInstalled(ReaderFontDefaults.CatalogFamilyPrefix + entry.Id);
            var installedPath = isInstalled && installedFont is not null
                ? library.GetAbsolutePath(installedFont)
                : null;
            var previewName = installedPath is not null
                ? ReadPostScriptName(installedPath) ?? entry.PostScriptName
                : entry.PostScriptName;
            var size = FormatFontSize(entry.Bytes);
            var detail = isInstalled
                ? $"{entry.Description} · 已下载 · {size} · {entry.License}"
                : $"{entry.Description} · {size} · {entry.License}";
            options.Add(new ReaderFontOption(
                ReaderFontDefaults.CatalogFamilyPrefix + entry.Id,
                entry.DisplayName,
                detail,
                "download",
                isInstalled,
                entry.Bytes,
                entry.License,
                previewName,
                installedPath));
        }

        return new ReaderFontList(_shell.Preferences.Current.ReaderFontFamily, options.ToArray());
    }

    private async Task<object> InstallReaderFontAsync(string id, CancellationToken token)
    {
        var entry = ReaderFontCatalog.Find(id)
            ?? throw new InvalidOperationException("字体不存在或已下架。");
        using var operation = BeginOperation();
        var font = await _shell.Modules.Library.Fonts.InstallCatalogAsync(entry, token);
        var path = _shell.Modules.Library.Fonts.GetAbsolutePath(font);
        var key = ReaderFontDefaults.CatalogFamilyPrefix + entry.Id;
        return new ReaderFontInstall(
            key,
            path,
            ReadPostScriptName(path) ?? entry.PostScriptName);
    }

    private static string? ReadPostScriptName(string path)
    {
        try
        {
            using var typeface = SKTypeface.FromFile(path, 0);
            return string.IsNullOrWhiteSpace(typeface?.PostScriptName) ? null : typeface.PostScriptName;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatFontSize(long bytes) => bytes <= 0
        ? "未知大小"
        : bytes >= 1024 * 1024
            ? $"{bytes / 1048576.0:0.#} MB"
            : $"{Math.Max(1, bytes / 1024)} KB";
}
#endif
