using Avalonia.Controls;
using Avalonia.Interactivity;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Close, checkpoint, cleanup and progress saving.
/// </summary>
public partial class MainWindow
{
    private void ReaderHost_NavigationStarting(
        object? sender,
        ReaderNavigationStartingEventArgs e)
    {
        if (e.Request is null) return;
        if (string.Equals(e.Request.Scheme, "about", StringComparison.OrdinalIgnoreCase)) return;
        if (!e.Request.IsFile)
        {
            e.Cancel = true;
            return;
        }

        try
        {
            var target = Path.GetFullPath(e.Request.LocalPath);
            var allowed = _readerIsPdf && !string.IsNullOrWhiteSpace(_readerPdfSourcePath)
                ? target.Equals(Path.GetFullPath(_readerPdfSourcePath), StringComparison.OrdinalIgnoreCase)
                : _readerDocument is not null && IsPathInside(_readerDocument.RootPath, target);
            e.Cancel = !allowed;
        }
        catch (Exception) when (e.Request.IsFile)
        {
            // Malformed file URIs fail closed instead of escaping the active
            // EPUB cache or the single PDF file selected for this session.
            e.Cancel = true;
        }
    }

    private void ReaderHost_NavigationCompleted(
        object? sender,
        ReaderNavigationCompletedEventArgs e)
    {
        if (sender is not IReaderHost host || !ReferenceEquals(host, CurrentReaderHost)) return;
        if (!e.IsSuccess)
        {
            ReaderStatusText.Text = T("当前章节加载失败。");
            return;
        }
        ResetReaderStatusText();
    }

    private void ReaderHost_WebMessageReceived(
        object? sender,
        ReaderWebMessageReceivedEventArgs e)
    {
        if (Volatile.Read(ref _readerCloseInProgress) != 0) return;
        if (sender is not IReaderHost host || !ReferenceEquals(host, CurrentReaderHost)) return;
        HandleReaderBridgeMessage(e.Body);
    }

    private Task CloseReaderAsync() => CloseReaderCoreAsync(animate: true);

    private async Task CloseReaderCoreAsync(bool animate)
    {
        if (Interlocked.Exchange(ref _readerCloseInProgress, 1) != 0) return;
        // Preserve the reading viewport until its progress has been saved.
        _readerToolbarAutoHideEnabled = false;
        _readerToolbarHideTimer?.Stop();
        _readerToolbarLayoutTimer?.Stop();
        // Cancel any in-flight open transition, then snapshot the live reading
        // page before cleanup hides ReaderRoot so the 3D close animation lands
        // smoothly back on the shelf cover.
        ResetReaderOpenTransition();
        var shouldAnimateClose = animate
            && ReaderRoot.IsVisible
            && !_lifetimeCancellation.IsCancellationRequested
            && !_applicationShuttingDown;
        var closingCard = _readerBookCard;
        // Cancel the session before waiting on persistence so an active page
        // turn releases its gate immediately.
        _readerSession.Cancel();
        _readerNavigationCancellation?.Cancel();
        CancelReaderChapterPreload();
        Interlocked.Exchange(ref _readerPendingKeyboardNavigation, 0);
        StopReaderTransitionOverlays();
        var closingTransition = shouldAnimateClose ? PrepareReaderCloseTransition(closingCard) : null;
        // Invalidate debounced bridge saves before waiting for the authoritative
        // close checkpoint. The close path below is the only save that should
        // survive a return to the bookshelf.
        Interlocked.Increment(ref _readerProgressSaveSequence);
        try
        {
            try
            {
                CancelReaderTtsContinuation();
                await _readerTts.StopAsync(ReaderCloseHostStopTimeout)
                    .WaitAsync(ReaderCloseHostStopTimeout + TimeSpan.FromMilliseconds(500));
            }
            catch
            {
                // A provider that ignores cancellation must not keep the
                // reader surface alive after the user returns to the library.
            }
            ReaderTtsPopup.IsOpen = false;

            await SaveReaderCheckpointBeforeCloseAsync();
            await SaveReaderPreferencesBeforeCloseAsync();

            StopReaderStatsTimer();
            // Reading time is accounted by the active-seconds flush (the stats
            // timer), matching the WinUI reference: time only accrues while the
            // window is active and the reader is visible.
            try
            {
                var flush = FlushReaderActiveSecondsAsync();
                try { await flush.WaitAsync(ReaderCloseCheckpointTimeout); }
                catch (TimeoutException) { _ = ObserveLateReadingTimeFlushAsync(flush); }
            }
            catch (Exception exception)
            {
                await ShowMessageAsync(T("阅读时长保存失败"), T("阅读时长保存失败，请检查存储空间或文件权限：{0}", UiText.Localize(exception.Message)));
            }
        }
        finally
        {
            try
            {
                await CleanupReaderAfterCloseAsync();
                if (closingTransition is not null)
                    await PlayReaderCloseTransitionAsync(closingTransition);
            }
            finally
            {
                Interlocked.Exchange(ref _readerCloseInProgress, 0);
            }
        }
    }

    private async Task ObserveLateReadingTimeFlushAsync(Task flush)
    {
        try { await flush; }
        catch (Exception exception)
        {
            if (!_lifetimeCancellation.IsCancellationRequested)
                await ShowMessageAsync(T("阅读时长保存失败"), T("阅读时长保存失败，请检查存储空间或文件权限：{0}", UiText.Localize(exception.Message)));
        }
    }

    private async Task SaveReaderCheckpointBeforeCloseAsync()
    {
        var pageTurnGateEntered = false;
        var layoutGateEntered = false;
        try
        {
            // Return-to-bookshelf is the authoritative checkpoint. Wait for a
            // page turn and side-panel reflow to settle, then read the native
            // position directly instead of trusting a stale bridge event.
            pageTurnGateEntered = await _readerPageTurnGate
                .WaitAsync(ReaderCloseCheckpointTimeout);
            if (!pageTurnGateEntered) return;

            layoutGateEntered = await _readerLayoutGate
                .WaitAsync(ReaderCloseCheckpointTimeout);
            if (!layoutGateEntered) return;

            if (!_readerIsPdf && CurrentReaderHost is { } currentHost)
            {
                await UpdateReaderScrollStateAsync(currentHost)
                    .WaitAsync(ReaderCloseCheckpointTimeout);
            }

            await SaveReaderProgressAsync(CancellationToken.None)
                .WaitAsync(ReaderCloseCheckpointTimeout);
        }
        catch
        {
            // Progress capture is best effort. A broken host or a locked
            // database must not skip UI restoration and resource disposal.
        }
        finally
        {
            if (layoutGateEntered) _readerLayoutGate.Release();
            if (pageTurnGateEntered) _readerPageTurnGate.Release();
        }
    }

    private async Task SaveReaderPreferencesBeforeCloseAsync()
    {
        try
        {
            await _readerAppearanceSaveTask.WaitAsync(ReaderCloseCheckpointTimeout);
            await SaveCurrentReaderPreferencesAsync(CancellationToken.None)
                .WaitAsync(ReaderCloseCheckpointTimeout);
        }
        catch
        {
            // Returning to the bookshelf must still complete if the settings
            // file is temporarily unavailable.
        }
    }

    private async Task CleanupReaderAfterCloseAsync()
    {
        // Capture the mode before ExitReaderZenMode resets it. Closing the
        // reader straight from zen asks the next open to restore zen; exiting
        // zen first leaves the normal layout for the next book.
        _openReaderInZenMode = _readerZenMode;
        ExitReaderZenMode();
        Interlocked.Exchange(ref _readerPendingKeyboardNavigation, 0);
        CancelReaderChapterPreload();
        _readerNavigationCancellation?.Cancel();
        _readerNavigationCancellation?.Dispose();
        _readerNavigationCancellation = null;
        CancelReaderPdfNavigation();
        _readerSession.End();
        _readerLayoutApplyCancellation?.Cancel();
        _readerLayoutApplyCancellation?.Dispose();
        _readerLayoutApplyCancellation = null;
        _readerRelayoutCancellation?.Cancel();
        _readerRelayoutCancellation?.Dispose();
        _readerRelayoutCancellation = null;
        _readerPendingRelayoutHost = null;
        _readerPendingRelayoutState = null;
        _readerAiCancellation?.Cancel();
        _readerAiCancellation?.Dispose();
        _readerAiCancellation = null;
        ReleaseReaderHosts();
        // Restore a clean shelf before revealing it. Selection may remain, but
        // details and their cover reading scrim require a fresh single click.
        ClearBookDetailsSelection();
        CompleteClearSelectedBook();
        ReaderRoot.IsVisible = false;
        LibraryRoot.IsVisible = true;
        WindowBrandText.IsVisible = false;
        ReaderLayoutSettingsPopup.IsOpen = false;
        CloseReaderImageZoom();
        ReaderInPageSearchBar.IsVisible = false;
        ReaderLinuxTextFallbackOverlay.IsVisible = false;
        ReaderLinuxTextFallbackText.Text = string.Empty;
        StopReaderFootnoteHoverPoll();
        _readerFootnoteHoverSequence++;
        HideReaderFootnotePopup();
        HideReaderAnnotationHoverPopup();
        _readerBookmarkIndicatorSequence++;
        ReaderBookmarkCornerMarker.IsVisible = false;
        _readerActiveSyncedMarker = null;
        _readerSyncedMarkerScrollOrigin = 0;
        _readerSyncedMarkerScrollArmed = false;
        ReaderTocCompactPanel.IsVisible = false;
        _readerSliderDragging = false;
        _readerSliderPreviewVisible = false;
        ReaderChapterPreviewPopup.IsOpen = false;
        ClearReaderCompactNavigationItems();
        ReaderAssistantPanel.IsVisible = false;
        ReaderPdfThumbnailsView.IsVisible = false;
        ReaderPdfThumbnailsList.ItemsSource = null;
        _readerThumbnailHost = null;
        ReaderRoot.ColumnDefinitions[2].Width = new GridLength(0);
        ReaderContentPanel.RowDefinitions[0].Height = new GridLength(52);
        ReaderHeaderBar.IsVisible = true;
        ReaderContentPanel.RowDefinitions[2].Height = new GridLength(42);
        ReaderFooterBar.IsVisible = true;
        _readerBookReadingStats = null;
        ReaderTransitionCover.Opacity = 0;
        _readerAssistantVisibleBeforeZen = false;
        _readerIsPdf = false;
        ResetReaderMangaSession();
        _readerPdfPages = [];
        _readerPdfEmbeddedOutline = [];
        _readerPendingPdfPoint = null;
        _readerPdfSourcePath = null;
        ReaderBookmarks.Clear();
        ReaderAnnotations.Clear();
        ReaderSearchResults.Clear();
        _readerPendingChunkOffset = null;
        _readerPendingSearchQuery = null;
        _readerPendingSearchContext = null;
        ClearReaderBookmarkPendingLocation();
        _readerPendingAnnotation = null;
        _readerCurrentFragment = null;
        _readerSearchSequence++;
        _readerWholeSearchSequence++;
        ClearReaderAiCollections();
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        try
        {
            await RefreshReadingMaterialsIfDirtyAsync()
                .WaitAsync(ReaderCloseCheckpointTimeout);
        }
        catch
        {
            // A refresh failure must not leave the cleared reader session tied
            // to the library or prevent the window from becoming usable.
        }
        _readerDocument = null;
        _readerBookCard = null;
        _readerBookFile = null;
    }

    private void ReleaseReaderHosts()
    {
        var activeHost = _readerActiveHost;
        var preloadHost = _readerPreloadHost;
        _readerActiveHost = null;
        _readerPreloadHost = null;
        _readerShowingPreload = false;
        _readerLoadedHostSources.Clear();


        // Clear both visual slots before disposing. The active and preload
        // references can point at the same host in tests or diagnostic
        // configurations; releasing the active slot alone would leave the
        // second ContentControl holding a disposed view.
        ReaderActiveHostSlot.Content = null;
        ReaderPreloadHostSlot.Content = null;
        ReleaseReaderHost(activeHost, ReaderActiveHostSlot);
        if (!ReferenceEquals(activeHost, preloadHost))
            ReleaseReaderHost(preloadHost, ReaderPreloadHostSlot);
    }

    private void ReleaseReaderHost(IReaderHost? host, ContentControl slot)
    {
        if (host is null)
        {
            slot.Content = null;
            return;
        }

        try { host.Stop(); }
        catch { }
        try
        {
            host.NavigationStarting -= ReaderHost_NavigationStarting;
            host.NavigationCompleted -= ReaderHost_NavigationCompleted;
            host.WebMessageReceived -= ReaderHost_WebMessageReceived;
        }
        catch { }
        slot.Content = null;
        try { host.Dispose(); }
        catch { }
    }

    private async Task SaveReaderProgressAsync(CancellationToken cancellationToken)
    {
        if (_readerBookCard is null || _readerBookFile is null) return;

        if (_readerIsPdf)
        {
            if (_readerPdfPages.Count == 0) return;
            var pdfProgress = new ReaderProgressRow(
                _readerBookCard.Book.Id,
                _readerBookFile.Id,
                $"pdf:page:{_readerPdfPage}",
                (CurrentReaderHost as IPdfReadingHost)?.CaptureViewState(),
                _readerPdfPage - 1,
                (int)Math.Round(_readerScrollPosition),
                CalculateReaderProgressPercent(),
                0,
                DateTimeOffset.UtcNow);
            await _readerProgressRecovery.SaveAsync(pdfProgress, cancellationToken);
            return;
        }

        if (_readerDocument is null
            || _readerChapterIndex < 0
            || _readerChapterIndex >= _readerDocument.Chapters.Count) return;
        if (_readerRestoredProgress is not null) return;

        TryApplyLinuxReaderTextFallbackState();

        var nativeReader = CurrentReaderHost as IReaderPositionHost;
        var contentPosition = nativeReader?.CaptureContentPosition();
        if (nativeReader is not null && (contentPosition is null
            || nativeReader.Source is not { IsFile: true } source
            || !string.Equals(source.LocalPath, _readerDocument.Chapters[_readerChapterIndex], StringComparison.OrdinalIgnoreCase)))
            return;
        var nativeState = nativeReader?.GetScrollState();
        var chapterPath = Path.GetRelativePath(
                _readerDocument.RootPath,
                _readerDocument.Chapters[_readerChapterIndex])
            .Replace('\\', '/');
        var progress = new ReaderProgressRow(
            _readerBookCard.Book.Id,
            _readerBookFile.Id,
            chapterPath,
            _readerCurrentFragment,
            _readerChapterIndex,
            (int)Math.Round(nativeState?.Position ?? _readerScrollPosition),
            nativeState is { } state
                ? Math.Clamp((_readerChapterIndex + state.Ratio) * 100d / _readerDocument.Chapters.Count, 0, 100)
                : CalculateReaderProgressPercent(),
            nativeReader is not null ? nativeReader.IsPaginated ? 1 : 0 : _readerLayout.FlowMode,
            DateTimeOffset.UtcNow)
        {
            ContentPosition = contentPosition
        };

        await _readerProgressRecovery.SaveAsync(progress, cancellationToken);
    }

    private static bool UriEquals(Uri? left, Uri right) =>
        left is not null
        && string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

    private static bool IsPathInside(string root, string path)
        => ReaderResourcePolicy.IsPathInside(root, path);

    private async void CloseReaderButton_Click(object? sender, RoutedEventArgs e)
        => await CloseReaderAsync();

    private async void ReaderPreviousButton_Click(object? sender, RoutedEventArgs e)
        => await MoveReaderFooterTocAsync(-1);

    private async void ReaderNextButton_Click(object? sender, RoutedEventArgs e)
        => await MoveReaderFooterTocAsync(1);
}
