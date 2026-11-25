#if LIBZORA_SWIFT_UI
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile;
using static Libzora.iOSHead.IOSNativeJson;

namespace Libzora.iOSHead;

internal sealed partial class IOSNativeFrontend
{
    private readonly SemaphoreSlim _importGate = new(1, 1);

    private async Task ImportBooksAsync(CancellationToken token)
    {
        var files = await _shell.Services.ImportSource.PickFilesAsync(token);
        try { await ImportFileBatchAsync(files, fromExternalOpen: false, token); }
        finally { _shell.Services.ImportSource.ReleaseFiles(files); }
    }

    private async Task ImportExternalFilesAsync(IReadOnlyList<string> files)
    {
        try { await ImportFileBatchAsync(files, fromExternalOpen: true, CancellationToken.None); }
        catch (Exception exception) { _shell.ShowToast(UiText.Localize(exception.Message)); }
        finally { IOSFileImportSource.DeleteFiles(files); }
    }

    private async Task ImportFileBatchAsync(IReadOnlyList<string> files, bool fromExternalOpen, CancellationToken token)
    {
        if (files.Count == 0) return;
        await _importGate.WaitAsync(token);
        try { await ImportFileBatchCoreAsync(files, fromExternalOpen, token); }
        finally { _importGate.Release(); }
    }

    private async Task ImportFileBatchCoreAsync(IReadOnlyList<string> files, bool fromExternalOpen, CancellationToken token)
    {
        using var operation = BeginOperation();
        if (fromExternalOpen && files.All(path => Path.GetExtension(path).Equals(".mdx", StringComparison.OrdinalIgnoreCase)))
        {
            await DictionaryGate.WaitAsync(token);
            try
            {
                var imported = await ImportDictionaryFilesAsync(_shell.Modules.Library.Dictionary, files, token);
                if (imported.Count == 1) _shell.ShowToast(UiText.Get("已导入词典：{0}", imported[0]));
                else if (imported.Count > 1) _shell.ShowToast(UiText.Get("已导入 {0} 本词典", imported.Count));
            }
            finally { DictionaryGate.Release(); }
            return;
        }
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".epub", ".pdf", ".mobi", ".azw3", ".txt" };
        if (files.Any(path => !supported.Contains(Path.GetExtension(path))))
            throw new InvalidOperationException(UiText.Get("请选择 EPUB、PDF、MOBI、AZW3 或 TXT 文件。"));
        var chosenFiles = files.ToList();
        var rules = new Dictionary<string, TxtImportRules>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files.Where(path => Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var selected = _shell.Preferences.Current.PreviewTxtImport
                ? await ChooseTxtRulesAsync(path, token) : TxtImportRules.Default;
            if (selected is null) chosenFiles.RemoveAll(file => string.Equals(file, path, StringComparison.OrdinalIgnoreCase));
            else rules[path] = selected;
        }
        if (chosenFiles.Count == 0) { _shell.ShowToast(UiText.Get("已取消导入。")); return; }
        await _shell.Modules.Library.Books.InitializeAsync(token);
        var result = await Task.Run(() => _shell.Modules.Library.Books.ImportAsync(chosenFiles,
            cancellationToken: token, txtImportRules: rules,
            conflictResolver: conflict => ChooseImportConflictAsync(conflict, token)), token);
        _shell.ShowToast(result.FailureCount > 0
            ? UiText.Get("已导入 {0} 本，{1} 个文件失败", result.AddedCount, result.FailureCount)
            : result.AddedCount == 0 ? UiText.Get("所选书籍已在书库中") : UiText.Get("已导入 {0} 本", result.AddedCount));
        if (fromExternalOpen)
        {
            Publish(Data(("type", "libraryChanged")));
            if (result.Items.FirstOrDefault(item => item.Succeeded && item.BookId is not null)?.BookId is { } bookId && files.Count == 1)
                Publish(Data(("type", "openBook"), ("bookId", bookId.ToString())));
        }
        if (IOSNativeBookLibraryPolicy.ImportFailedCompletely(result)
            && result.Items.FirstOrDefault(item => !item.Succeeded) is { } failed)
            throw new InvalidOperationException(failed.Message);
    }

    private async Task<TxtImportRules?> ChooseTxtRulesAsync(string path, CancellationToken token)
    {
        var id = ++_promptId;
        var decision = new TaskCompletionSource<TxtImportRules?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _txtImports.Add(id, (path, decision));
        Publish(Data(("type", "txtImport"), ("txtImport", Data(("id", id), ("name", Path.GetFileName(path))))));
        try { return await decision.Task.WaitAsync(TimeSpan.FromMinutes(30), token); }
        finally
        {
            _txtImports.Remove(id);
            Publish(Data(("type", "txtImportDismissed"), ("id", id)));
        }
    }
}
#endif
