using Libzora.Core;
using Libzora.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

namespace Libzora;

/// <summary>
/// Host compatibility, warm-up, navigation and preloading.
/// </summary>
public partial class MainWindow
{
    private static ReaderProgressRow? ValidateReaderProgress(
        EpubReaderDocument document,
        ReaderProgressRow? progress)
        => ReaderResourcePolicy.RestoreProgress(document, progress);

    private IReaderHost CreateReaderHostForCurrentFormat()
    {
        var format = _readerIsPdf ? ReaderHostFormat.Pdf : ReaderHostFormat.Epub;
        var host = _readerHostFactory.Create(format)
            ?? throw new InvalidOperationException(T("阅读器宿主工厂必须返回有效实例。"));
        try
        {
            if (!_readerHostFactory.IsCompatible(host, format))
                throw new InvalidOperationException(T("阅读器宿主与当前书籍格式不兼容。"));
            (host as IReaderAppearanceHost)?.SetAppearance(CurrentReaderAppearance);
            (host as NativeReaderHost)?.SetPureBlackBackground(IsReaderMangaBlackCanvas);
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private bool IsReaderHostTypeCompatible(IReaderHost host) =>
        _readerHostFactory.IsCompatible(host,
            _readerIsPdf ? ReaderHostFormat.Pdf : ReaderHostFormat.Epub);

    private async Task EnsureReaderHostsAsync()
    {
        if (_readerActiveHost is not null && !IsReaderHostTypeCompatible(_readerActiveHost))
        {
            CancelReaderChapterPreload();
            // Each format owns its rendering and text geometry, so never reuse
            // an EPUB surface for PDF content or the reverse.
            ReleaseReaderHosts();
        }

        if (_readerActiveHost is null)
        {
            var activeHost = CreateReaderHostForCurrentFormat();
            IReaderHost? preloadHost = null;
            try
            {
                if (!_readerIsPdf)
                {
                    preloadHost = CreateReaderHostForCurrentFormat();
                    if (ReferenceEquals(activeHost, preloadHost))
                        throw new InvalidOperationException(T("阅读器宿主工厂必须返回两个不同实例。"));
                }
            }
            catch
            {
                if (!ReferenceEquals(activeHost, preloadHost)) preloadHost?.Dispose();
                activeHost.Dispose();
                throw;
            }

            // Publish only a complete pair; a failed adapter must not leave
            // a disposed or half-initialized surface attached to the shell.
            _readerActiveHost = activeHost;
            _readerPreloadHost = preloadHost;
            _readerActiveHost.NavigationStarting += ReaderHost_NavigationStarting;
            _readerActiveHost.NavigationCompleted += ReaderHost_NavigationCompleted;
            _readerActiveHost.WebMessageReceived += ReaderHost_WebMessageReceived;
            ReaderActiveHostSlot.Content = _readerActiveHost.View;

            if (_readerPreloadHost is not null)
            {
                _readerPreloadHost.NavigationStarting += ReaderHost_NavigationStarting;
                _readerPreloadHost.NavigationCompleted += ReaderHost_NavigationCompleted;
                _readerPreloadHost.WebMessageReceived += ReaderHost_WebMessageReceived;
                ReaderPreloadHostSlot.Content = _readerPreloadHost.View;
            }
            else
            {
                _readerPreloadHost = null;
                ReaderPreloadHostSlot.Content = null;
            }
        }

        ReaderActiveHostSlot.IsVisible = true;
        ReaderActiveHostSlot.Opacity = 1;
        ReaderActiveHostSlot.IsHitTestVisible = true;
        ReaderActiveHostSlot.ZIndex = 1;
        ReaderPreloadHostSlot.IsVisible = _readerPreloadHost is not null;
        ReaderPreloadHostSlot.Opacity = 0;
        ReaderPreloadHostSlot.IsHitTestVisible = false;
        ReaderPreloadHostSlot.ZIndex = 0;

        if (!await WaitForReaderHostReadyAsync(
                _readerActiveHost,
                TimeSpan.FromSeconds(10),
                _readerSession.GetTokenOr(_lifetimeCancellation.Token)))
        {
            throw new InvalidOperationException(
                T("阅读器 WebView 初始化超时。请确认 Linux 已安装 WPE WebKit 运行库后重试。"));
        }

        if (_readerPreloadHost is not null)
        {
            _ = WarmUpReaderPreloadHostAsync(
                _readerPreloadHost,
                _readerSession.GetTokenOr(_lifetimeCancellation.Token));
        }
    }

    private static async Task<bool> WaitForReaderHostReadyAsync(
        IReaderHost host,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (host.ReadyTask.IsCompletedSuccessfully) return true;

        try
        {
            await host.ReadyTask.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WarmUpReaderPreloadHostAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        try
        {
            await WaitForReaderHostReadyAsync(
                host,
                TimeSpan.FromSeconds(3),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private async Task<bool> NavigateReaderHostAndWaitAsync(
        IReaderHost host,
        Uri target,
        CancellationToken cancellationToken,
        bool isPreload = false)
    {
        var timing = Stopwatch.StartNew();
        if (!isPreload) CancelReaderChapterPreload(target);
        var gate = ReferenceEquals(host, _readerPreloadHost)
            ? _readerPreloadHostNavigationGate
            : _readerActiveHostNavigationGate;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (IsLinuxReaderTextFallbackActive())
            {
                if (!ReaderLinuxTextFallbackOverlay.IsVisible)
                    SetReaderHostLayer(revealActiveHost: true);
            }

            if (!await WaitForReaderHostReadyAsync(
                    host,
                    TimeSpan.FromSeconds(10),
                    cancellationToken))
            {
                return false;
            }
            LogReaderChapterTiming("nav.ready", timing);

            // A TOC click can request a fragment in the chapter currently
            // prepared by the hidden host. Same-document WebView navigation
            // is not guaranteed to raise NavigationCompleted, so reuse the
            // loaded document and let ApplyReaderLocationAsync perform the
            // exact anchor/offset jump after the host swap.
            if (ReaderHostHasLoadedDocument(host, target))
            {
                await ConfigureReaderHostAsync(host, cancellationToken);
                return true;
            }

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<ReaderNavigationCompletedEventArgs>? handler = null;
            handler = (_, args) =>
            {
                if (!ReaderNavigationLocationPolicy.TargetsSameDocument(args.Request, target)
                    && !UriEquals(args.Request, target)) return;
                completion.TrySetResult(args.IsSuccess);
            };
            host.NavigationCompleted += handler;
            try
            {
                _readerLoadedHostSources.Remove(host);
                if (!await NavigateReaderHostCoreAsync(host, target, cancellationToken))
                    return false;
                var loaded = await completion.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
                if (loaded)
                {
                    // The Linux WebView can finish navigation while Avalonia
                    // is still assigning the final reader width. Configuring
                    // and restoring against that transient narrow viewport
                    // maps a correct saved pixel to the wrong vertical page,
                    // then visibly jumps when the host expands. Keep the
                    // document hidden until the native and DOM viewports agree.
                    if (ReferenceEquals(host, CurrentReaderHost))
                        _ = await WaitForReaderViewportToMatchHostAsync(host, cancellationToken);
                    LogReaderChapterTiming("nav.viewportSettled", timing);
                    await ConfigureReaderHostAsync(host, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    _readerLoadedHostSources[host] = target;
                    LogReaderChapterTiming("nav.configured", timing);
                }
                return loaded;
            }
            finally
            {
                host.NavigationCompleted -= handler;
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            // Release the host for a foreground jump and stop the abandoned
            // load itself. Source is assigned before loading finishes, so it
            // must not make a cancelled document look ready for reuse.
            _readerLoadedHostSources.Remove(host);
            host.Stop();
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool ReaderHostHasLoadedDocument(IReaderHost host, Uri target) =>
        _readerLoadedHostSources.TryGetValue(host, out var loaded)
        && ReaderNavigationLocationPolicy.TargetsSameDocument(loaded, target)
        && ReaderNavigationLocationPolicy.TargetsSameDocument(host.Source, target);

    private async Task<bool> NavigateReaderHostCoreAsync(
        IReaderHost host,
        Uri target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (host is IReaderLayoutNavigationHost nativeReader)
        {
            // Each host can hold a different spine document. Only its own
            // TOC anchors participate in reading-position tracking.
            nativeReader.SetNavigationFragments(_readerTocItems
                .Select(item => Uri.TryCreate(item.Target, UriKind.Absolute, out var uri) ? uri : null)
                .Where(uri => uri is not null && ReaderNavigationLocationPolicy.TargetsSameDocument(uri, target))
                .Select(uri => uri!.Fragment));
            if (host is NativeReaderHost nativeHost)
                nativeHost.SetSpreadCompanionChapterPath(ResolveSpreadCompanionChapterPath(target));
            nativeReader.Navigate(target, _readerLayout, ShouldShowReaderVerticalDebugBoxes());
            return true;
        }

        if (OperatingSystem.IsLinux()
            && Environment.GetEnvironmentVariable("LIBZORA_LINUX_HTML_STRING") == "1"
            && !_readerIsPdf
            && target.IsFile
            && host is IReaderHtmlHost htmlHost)
        {
            var path = target.LocalPath;
            if (!File.Exists(path)) return false;
            var html = await File.ReadAllTextAsync(path, cancellationToken);
            var baseUri = new UriBuilder(target)
            {
                Fragment = string.Empty
            }.Uri;
            htmlHost.NavigateToString(html, baseUri);
            return true;
        }

        host.Navigate(target);
        return true;
    }

    private string? ResolveSpreadCompanionChapterPath(Uri target)
    {
        if (_readerDocument is null || !target.IsFile) return null;
        var targetPath = Path.GetFullPath(target.LocalPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var i = 0; i + 1 < _readerDocument.Chapters.Count; i++)
        {
            if (string.Equals(Path.GetFullPath(_readerDocument.Chapters[i]), targetPath, comparison))
                return _readerDocument.Chapters[i + 1];
        }
        return null;
    }

    private async Task PreloadNextReaderChapterAsync(CancellationToken cancellationToken)
    {
        var document = _readerDocument;
        var sourceChapterIndex = _readerChapterIndex;
        var step = CurrentReaderHost is NativeReaderHost { IsShowingCompanionSpread: true } ? 2 : 1;
        if (document is null
            || HiddenReaderHost is not { } host
            || !IsReaderHostReady(host)
            || sourceChapterIndex + step >= document.Chapters.Count) return;

        var targetChapterIndex = sourceChapterIndex + step;
        var target = new Uri(document.Chapters[targetChapterIndex]);
        CancelReaderChapterPreload();
        using var preloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readerChapterPreloadCancellation = preloadCancellation;
        _readerChapterPreloadTarget = target;
        try
        {
            var loaded = await NavigateReaderHostAndWaitAsync(
                host,
                target,
                preloadCancellation.Token,
                isPreload: true);
            if (loaded
                && (!ReferenceEquals(document, _readerDocument)
                    || (_readerChapterIndex != sourceChapterIndex
                        && !ReferenceEquals(host, CurrentReaderHost))))
            {
                // A foreground jump took over while the speculative load was
                // finishing. Discard its hidden document so it cannot be
                // reused as stale chapter state on the next navigation.
                if (!ReferenceEquals(host, CurrentReaderHost))
                {
                    _readerLoadedHostSources.Remove(host);
                    host.Stop();
                }
            }
        }
        catch (OperationCanceledException) when (preloadCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // Preloading is an optimization. The visible host remains usable
            // and will load the chapter on demand if this fails.
        }
        finally
        {
            if (ReferenceEquals(_readerChapterPreloadCancellation, preloadCancellation))
            {
                _readerChapterPreloadCancellation = null;
                _readerChapterPreloadTarget = null;
            }
        }
    }

    private void CancelReaderChapterPreload(Uri? requestedTarget = null)
    {
        if (_readerChapterPreloadCancellation is not { } preload) return;
        // A click on the chapter already being prepared can use that work.
        // All other foreground destinations take priority over speculation.
        if (requestedTarget is not null
            && ReaderNavigationLocationPolicy.TargetsSameDocument(_readerChapterPreloadTarget, requestedTarget))
            return;
        _readerChapterPreloadCancellation = null;
        _readerChapterPreloadTarget = null;
        preload.Cancel();
    }

    /// <summary>
    /// DEBUG chapter-switch profiler: appends one line per stage to
    /// reader-timing.log so a slow vertical chapter turn can be attributed to
    /// navigation, font waiting, vertical cell preparation or the reveal.
    /// </summary>
    private void LogReaderChapterTiming(string stage, Stopwatch clock)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("LIBZORA_READER_TIMING"),
                "1",
                StringComparison.Ordinal))
            return;
        try
        {
            var entry = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                stage,
                elapsedMs = clock.ElapsedMilliseconds,
                chapter = _readerChapterIndex,
                vertical = _readerLayout.VerticalWriting,
                flowMode = _readerLayout.FlowMode
            });
            Directory.CreateDirectory(_paths.Logs);
            File.AppendAllText(
                Path.Combine(_paths.Logs, "reader-timing.log"),
                entry + Environment.NewLine);
        }
        catch
        {
            // Timing is diagnostic only and must never break a chapter turn.
        }
    }
}
