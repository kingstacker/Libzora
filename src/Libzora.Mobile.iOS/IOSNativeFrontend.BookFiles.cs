#if LIBZORA_SWIFT_UI
using System.Text.Json;
using Libzora.Core;
using Libzora.Mobile;
using static Libzora.iOSHead.IOSNativeJson;

namespace Libzora.iOSHead;

internal sealed partial class IOSNativeFrontend
{
    private async Task<Guid?> ResolveReaderFileAsync(Book book, JsonElement args, ReaderProgressRow? location,
        CancellationToken token)
    {
        // Exact locations always retain their file identity, including invalid IDs (the reader rejects them).
        if (location is not null) return location.BookFileId;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("bookFileId", out var explicitId) && explicitId.ValueKind != JsonValueKind.Null)
        {
            var id = explicitId.GetGuid();
            if (!book.Files.Any(file => file.Id == id && file.BookId == book.Id && IOSNativeBookLibraryPolicy.IsReadable(file)))
                throw new InvalidOperationException(UiText.Get("所选书籍文件已不存在或不支持阅读。"));
            return id;
        }
        await _shell.EnsureReaderDataAsync();
        var positions = new List<ReaderProgressRow>();
        foreach (var file in book.Files.Where(IOSNativeBookLibraryPolicy.IsReadable))
            if (await _shell.Modules.Reader.Data.GetProgressAsync(file.Id, token) is { } progress)
                positions.Add(progress);
        return IOSNativeBookLibraryPolicy.MostRecentlyReadFile(book, positions)?.Id;
    }

    private async Task<object> GetBookFilesAsync(JsonElement args, CancellationToken token)
    {
        var book = await GetBookAsync(args, token);
        var preferred = await ResolveReaderFileAsync(book, default, null, token);
        return new NativeBookFilesSnapshot(book.Files.Select(file => new NativeBookFile(
            file.Id, Path.GetFileName(file.RelativePath), file.Format.ToUpperInvariant(), MobileFormat.Size(file.Size),
            IOSNativeBookLibraryPolicy.IsReadable(file), File.Exists(_shell.Modules.Library.Books.GetAbsoluteFilePath(file)))).ToArray(), preferred);
    }

    private async Task SetBookReadingStatusAsync(JsonElement args, CancellationToken token)
    {
        var book = await GetBookAsync(args, token);
        var status = (LibraryReadingStatus)Number(args, "status", -1);
        if (!Enum.IsDefined(status)) throw new InvalidOperationException(UiText.Get("无效的阅读状态。"));
        book.ReadingStatus = status;
        await _shell.Modules.Library.Books.UpdateMetadataAsync(book, token);
        _shell.Preferences.UpdateAndSave(value => value.WithManualReadingStatus(book.Id, status));
        Publish(Data(("type", "libraryChanged")));
    }

    private async Task<object> ExportBookFileAsync(JsonElement args, CancellationToken token)
    {
        var book = await GetBookAsync(args, token);
        var fileId = Id(args, "bookFileId");
        var file = book.Files.FirstOrDefault(item => item.Id == fileId && item.BookId == book.Id)
            ?? throw new InvalidOperationException(UiText.Get("所选书籍文件已不存在或不支持阅读。"));
        var path = _shell.Modules.Library.Books.GetAbsoluteFilePath(file);
        if (!File.Exists(path))
        {
            var stored = await _shell.Modules.Sync.Service.LoadSettingsAsync(token);
            if (!stored.Settings.Enabled || !stored.Settings.IsConfigured || !stored.Settings.HasChosenCustomProvider)
                throw new InvalidOperationException(UiText.Get("书籍文件不在本机，请重新导入，或在“我的”里开启同步后重试。"));
            path = await _shell.Modules.Sync.Service.EnsureBookFileDownloadedAsync(stored.DeviceId,
                stored.Settings, file, cancellationToken: token) ?? path;
        }
        var share = _shell.Services.FileShare ?? throw new InvalidOperationException(UiText.Get("此设备不支持分享文件。"));
        using var export = await MobileBookFileExport.CreateAsync(path,
            IOSNativeBookLibraryPolicy.ExportFileName(book.Title, file), token);
        return Data(("shared", await share.ShareFileAsync(export.Path)));
    }
}
#endif
