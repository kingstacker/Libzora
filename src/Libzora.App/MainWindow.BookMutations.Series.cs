using Libzora.Core;

namespace Libzora;

/// <summary>
/// Series-level batch mutations triggered from the stacked series context menu
/// (renaming, format conversion, whole-series and batch-format deletion).
/// </summary>
public partial class MainWindow
{
    private async Task RenameSeriesFromContextAsync(BookCardViewModel stack)
    {
        if (!stack.IsSeriesStack)
        {
            await RenameBookFromContextAsync(stack);
            return;
        }

        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync(T("无法重命名系列"), T("当前正在阅读书籍，请先关闭阅读器。"));
            return;
        }

        if (_conversionInProgress || _automaticReaderFormatGenerationInProgress)
        {
            await ShowMessageAsync(T("无法重命名系列"), T("当前有书籍任务正在进行，请完成后再重命名。"));
            return;
        }

        var previousSeries = stack.SeriesTitle ?? stack.DisplayTitle;
        var name = await PromptCollectionNameAsync(
            previousSeries,
            T("重命名系列"),
            T("请输入新的系列名称。"));
        if (string.IsNullOrWhiteSpace(name)) return;

        var normalizedName = name.Trim();
        var members = OrderLocalSeriesMembers(stack);
        var inferredIndices = ResolveInferredSeriesIndices(members);

        var enteredGate = false;
        try
        {
            await _bookMetadataSaveGate.WaitAsync(_lifetimeCancellation.Token);
            enteredGate = true;
            var updatedCount = 0;
            foreach (var member in members)
            {
                var book = await _library.GetBookAsync(member.Book.Id, _lifetimeCancellation.Token);
                if (book is null) continue;

                var nextIndex = book.SeriesIndex
                    ?? (inferredIndices.TryGetValue(book.Id, out var inferredIndex) ? inferredIndex : null);
                if (string.Equals(book.Series, normalizedName, StringComparison.Ordinal)
                    && book.SeriesIndex == nextIndex)
                {
                    continue;
                }

                book.Series = normalizedName;
                if (nextIndex is not null)
                    book.SeriesIndex = nextIndex;
                await _library.UpdateMetadataAsync(book, _lifetimeCancellation.Token);
                updatedCount++;
            }

            ViewModel.PreserveSeriesFront(
                stack.SeriesKey,
                BookSeriesGrouping.ExplicitSeriesKey(normalizedName),
                stack.Book.Id);
            await RefreshLibraryAsync();
            SetTaskStatus(T(
                "已将系列“{0}”重命名为“{1}”（共 {2} 本）。",
                previousSeries,
                normalizedName,
                updatedCount > 0 ? updatedCount : members.Count));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("重命名系列失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法重命名系列"), UiText.Localize(exception.Message));
        }
        finally
        {
            if (enteredGate) _bookMetadataSaveGate.Release();
        }
    }

    private static Dictionary<Guid, double> ResolveInferredSeriesIndices(
        IReadOnlyList<BookCardViewModel> members)
    {
        var result = new Dictionary<Guid, double>();
        var stacks = BookSeriesGrouping.Stacks(members.Select(card => new BookSeriesCandidate(
            card.Book.Id.ToString("N"),
            card.Book.Title,
            card.Book.Authors,
            card.Book.Series,
            card.Book.SeriesIndex,
            card.Book.Files
                .Select(file => Path.GetFileNameWithoutExtension(file.RelativePath))
                .ToArray())));
        if (stacks.Count != 1) return result;

        foreach (var member in stacks[0].Members)
        {
            if (member.Index is { } index
                && double.IsFinite(index)
                && Guid.TryParseExact(member.Id, "N", out var bookId))
            {
                result[bookId] = index;
            }
        }
        return result;
    }

    private async Task ConvertSeriesBooksAsync(
        IReadOnlyList<BookCardViewModel> volumes,
        string targetFormat)
    {
        if (_conversionInProgress || _automaticReaderFormatGenerationInProgress)
        {
            await ShowMessageAsync(T("格式转换"), T("已有一本书正在转换，请稍候。"));
            return;
        }

        var target = BookFormatConversionPolicy.Normalize(targetFormat);
        if (!BookFormatConversionPolicy.IsConvertibleFormat(target)) return;

        var queue = volumes
            .Select(volume => (
                Volume: volume,
                Source: !HasBookFormat(volume.Book.Files, target)
                    ? BookFormatConversionPolicy.SelectSource(volume.Book.Files, target)
                    : null))
            .Where(item => item.Source is not null)
            .Select(item => (item.Volume, Source: item.Source!))
            .ToArray();
        if (queue.Length == 0) return;

        _conversionInProgress = true;
        _conversionCancellation?.Dispose();
        _conversionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var targetLabel = target.ToUpperInvariant();
        var convertedCount = 0;
        var failures = new List<string>();
        try
        {
            await RunTrackedTaskAsync(T("格式转换"), BackgroundTaskKind.FormatConversion, async (context, token) =>
            {
                context.ReportBooks(queue.Select(item => item.Volume.Title).ToArray());
                for (var index = 0; index < queue.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var (volume, sourceFile) = queue[index];
                    var bookIndex = index;
                    var progress = new Progress<FormatConversionProgress>(value =>
                    {
                        if (token.IsCancellationRequested) return;
                        var overall = (bookIndex * 100 + value.RoundedPercentage) / queue.Length;
                        var message = T("正在转换《{0}》（{1}/{2}）…", volume.Title, bookIndex + 1, queue.Length);
                        context.ReportProgress(message, overall, 100);
                        SetTaskStatus($"{message} {value.RoundedPercentage}%");
                    });
                    try
                    {
                        context.ReportProgress(
                            T("正在转换《{0}》（{1}/{2}）…", volume.Title, index + 1, queue.Length),
                            index * 100 / queue.Length,
                            100);
                        await ConvertSingleBookFileAsync(volume, sourceFile, target, progress, token);
                        convertedCount++;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        failures.Add(T("《{0}》：{1}", volume.Title, UiText.Localize(exception.Message)));
                    }
                }

                if (convertedCount > 0)
                    await RefreshLibraryAsync();

                var summary = failures.Count == 0
                    ? T("已为系列中 {0} 本书添加 {1} 格式。", convertedCount, targetLabel)
                    : T("系列格式转换完成：成功 {0} 本，失败 {1} 本。", convertedCount, failures.Count);
                context.ReportProgress(summary, 100, 100);
                return failures.Count == 0;
            }, _conversionCancellation.Token, allowRetry: false);

            var finalMessage = failures.Count == 0
                ? T("已为系列中 {0} 本书添加 {1} 格式。", convertedCount, targetLabel)
                : T("系列格式转换完成：成功 {0} 本，失败 {1} 本。", convertedCount, failures.Count);
            SetTaskStatus(finalMessage);
            if (failures.Count > 0 && convertedCount == 0)
                await ShowMessageAsync(T("格式转换"), string.Join(Environment.NewLine, failures.Take(5)));
        }
        catch (OperationCanceledException)
        {
            if (convertedCount > 0)
                await RefreshLibraryAsync();
            SetTaskStatus(T("格式转换已取消。"));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("系列格式转换失败：{0}", UiText.Localize(exception.Message)));
        }
        finally
        {
            _conversionInProgress = false;
            _conversionCancellation?.Dispose();
            _conversionCancellation = null;
        }
    }

    private Task DeleteSeriesFromContextAsync(BookCardViewModel stack)
    {
        if (!stack.IsSeriesStack)
            return DeleteBookFromContextAsync(stack);

        var members = OrderLocalSeriesMembers(stack);
        var seriesTitle = stack.SeriesTitle ?? stack.DisplayTitle;
        return DeleteBooksBatchCoreAsync(
            members,
            T("《{0}》系列的 {1} 本书将从书库移除并移入回收站，可恢复。", seriesTitle, members.Count),
            T("已将《{0}》系列的 {1} 本书移入回收站。", seriesTitle, members.Count));
    }

    private async Task DeleteSeriesFormatFilesAsync(BookCardViewModel stack, string format)
    {
        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync(T("无法删除格式"), T("当前正在阅读其中一本书，请先关闭阅读器。"));
            return;
        }

        var members = OrderLocalSeriesMembers(stack);
        var targets = new List<(BookCardViewModel Volume, BookFile File)>();
        var matchedVolumeCount = 0;
        var wholeBookRemovalCount = 0;
        foreach (var member in members)
        {
            var matchingFiles = member.Book.Files
                .Where(file => string.Equals(file.Format, format, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matchingFiles.Length == 0) continue;

            matchedVolumeCount++;
            if (matchingFiles.Length >= member.Book.Files.Count)
                wholeBookRemovalCount++;
            foreach (var file in matchingFiles)
                targets.Add((member, file));
        }

        if (targets.Count == 0) return;

        var formatLabel = format.ToUpperInvariant();
        var prompt = wholeBookRemovalCount == 0
            ? T("确定将系列中 {0} 个 {1} 文件移入回收站吗？可在回收站恢复，其他格式不受影响。", targets.Count, formatLabel)
            : wholeBookRemovalCount == matchedVolumeCount
                ? T("确定将系列中 {0} 个 {1} 文件移入回收站吗？这 {2} 本书仅包含该格式，将会整本移入回收站（均可恢复）。", targets.Count, formatLabel, wholeBookRemovalCount)
                : T("确定将系列中 {0} 个 {1} 文件移入回收站吗？其中 {2} 本书仅包含该格式，将会整本移入回收站（均可恢复），其余书籍的其他格式不受影响。", targets.Count, formatLabel, wholeBookRemovalCount);
        if (!await ConfirmAsync(T("删除文件"), prompt, T("删除文件")))
            return;

        var deletedCount = 0;
        try
        {
            foreach (var (volume, file) in targets)
            {
                await _library.DeleteFileAsync(volume.Book.Id, file.Id, _lifetimeCancellation.Token);
                deletedCount++;
            }

            if (SeriesShelfOverlay.IsVisible && wholeBookRemovalCount > 0)
                CloseSeriesShelfOverlay(immediate: true);
            await RefreshLibraryAsync();
            var completion = T("已将系列中 {0} 个 {1} 文件移入回收站。", deletedCount, formatLabel);
            SetTaskStatus(completion);
            ShowSettingsCapsule(completion, 2400, success: true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (deletedCount > 0)
                await RefreshLibraryAsync();
            SetTaskStatus(T("删除文件失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法删除格式"), UiText.Localize(exception.Message));
        }
    }

    private static bool HasBookFormat(IEnumerable<BookFile> files, string format) =>
        files.Any(file => string.Equals(
            BookFormatConversionPolicy.Normalize(file.Format),
            BookFormatConversionPolicy.Normalize(format),
            StringComparison.OrdinalIgnoreCase));
}
