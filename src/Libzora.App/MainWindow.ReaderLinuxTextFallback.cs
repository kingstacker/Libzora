using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Xml;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Linux plain-text fallback surface: activation, scroll state and page
/// rendering.
/// </summary>
public partial class MainWindow
{
    private async Task UpdateLinuxReaderTextFallbackAsync(
        CancellationToken cancellationToken,
        IReaderHost? host = null)
    {
        // The native WebKit surface is authoritative for every Linux reading
        // mode, including vertical writing. The old Avalonia text surface is
        // intentionally kept unreachable so Linux uses the same HTML/CSS
        // layout engine for horizontal and vertical EPUB content.
        if (!UseLinuxPlainTextRecoveryFallback)
        {
            HideLinuxReaderTextFallback();
            if (OperatingSystem.IsLinux() && !_readerIsPdf)
                SetReaderHostLayer(revealActiveHost: true);
            return;
        }

        if (!OperatingSystem.IsLinux()
            || _readerIsPdf
            || _readerDocument is null
            || _readerChapterIndex < 0
            || _readerChapterIndex >= _readerDocument.Chapters.Count)
        {
            HideLinuxReaderTextFallback();
            return;
        }

        var chapterPath = _readerDocument.Chapters[_readerChapterIndex];
        if (!File.Exists(chapterPath))
        {
            HideLinuxReaderTextFallback();
            return;
        }

        // This branch is retained only for diagnostics and historical tests;
        // the production reader never enters it and always shows WebKit.

        var targetTitle = _readerLinuxTextFallbackTargetTitle
            ?? GetReaderChapterDisplayName(_readerChapterIndex);
        var fragment = string.IsNullOrWhiteSpace(targetTitle)
            ? null
            : DecodeReaderFragment(_readerCurrentFragment);
        var endFragment = DecodeReaderFragment(_readerLinuxTextFallbackEndFragment);
        var content = await Task.Run(
            () => ExtractReaderFallbackContent(chapterPath, fragment, targetTitle, endFragment),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(
                Environment.GetEnvironmentVariable("LIBZORA_TRACE_FALLBACK_TITLE"),
                "1",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"fallback-content target={targetTitle} fragment={fragment} end={endFragment} blocks="
                + string.Join(
                    " || ",
                    content.Blocks.Take(10).Select(block =>
                        $"title={block.IsChapterTitle},boundary={block.IsParagraphBoundary},text={block.Text}")));
        }

        var text = content.Text;
        if (string.IsNullOrWhiteSpace(text))
            text = GetReaderChapterDisplayName(_readerChapterIndex);

        var fontSize = 16d * _readerLayout.FontScale;
        var visible = !string.IsNullOrWhiteSpace(text)
            || content.Blocks.Any(block =>
                !string.IsNullOrWhiteSpace(block.ImagePath)
                || !string.IsNullOrWhiteSpace(block.FootnoteHref));
        _readerLinuxTextFallbackUpdating = true;
        try
        {
            ClearLinuxReaderTextFallbackSelectionState();
            _readerLinuxTextFallbackText = text;
            ReaderLinuxTextFallbackText.Text = text;
            ReaderLinuxTextFallbackText.FontSize = fontSize;
            ReaderLinuxTextFallbackText.LineHeight = fontSize * _readerLayout.LineHeight;
            ReaderLinuxTextFallbackText.MaxWidth = Math.Max(320, _readerLayout.MaxWidth);
            ReaderLinuxTextFallbackPageLeft.FontSize = fontSize;
            ReaderLinuxTextFallbackPageLeft.LineHeight = fontSize * _readerLayout.LineHeight;
            ReaderLinuxTextFallbackPageRight.FontSize = fontSize;
            ReaderLinuxTextFallbackPageRight.LineHeight = fontSize * _readerLayout.LineHeight;
            UpdateLinuxReaderTextFallbackBlocks(content.Blocks, text, fontSize);
            if (_readerLayout.FlowMode == 0)
                ReaderLinuxTextFallbackScroll.Offset = new Vector(0, Math.Max(0, _readerScrollPosition));
            else
                _readerLinuxTextFallbackPageIndex = _readerLinuxTextFallbackMoveToChapterEnd || _readerScrollPosition < 0
                    ? -1
                    : (int)Math.Round(Math.Max(0, _readerScrollPosition));
            ReaderLinuxTextFallbackOverlay.IsVisible = visible;
            UpdateLinuxReaderTextFallbackMode();
        }
        finally
        {
            _readerLinuxTextFallbackUpdating = false;
        }

        if (visible)
        {
            ReaderActiveHostSlot.IsVisible = false;
            ReaderActiveHostSlot.IsHitTestVisible = false;
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (!IsLinuxReaderTextFallbackActive()) return;
                    ClampLinuxReaderTextFallbackOffset();
                    RebuildLinuxReaderTextFallbackPages();
                    SyncLinuxReaderTextFallbackState(saveProgress: false);
                    PrimeReaderContinuousEdgeTracking();
                    ReaderLinuxTextFallbackOverlay.Focus();
                },
                DispatcherPriority.Loaded);
        }
    }

    private void HideLinuxReaderTextFallback()
    {
        ReaderLinuxTextFallbackOverlay.IsVisible = false;
        ReaderLinuxTextFallbackText.Text = string.Empty;
        ReaderLinuxTextFallbackPageLeft.Inlines?.Clear();
        ReaderLinuxTextFallbackPageRight.Inlines?.Clear();
        ReaderLinuxTextFallbackPageLeft.Text = string.Empty;
        ReaderLinuxTextFallbackPageRight.Text = string.Empty;
        ReaderLinuxTextFallbackPageImageLeft.Source = null;
        ReaderLinuxTextFallbackPageImageRight.Source = null;
        ReaderLinuxTextFallbackPageImageLeft.IsVisible = false;
        ReaderLinuxTextFallbackPageImageRight.IsVisible = false;
        ReaderLinuxTextFallbackPageFootnoteLeft.Content = string.Empty;
        ReaderLinuxTextFallbackPageFootnoteLeft.Tag = null;
        ReaderLinuxTextFallbackPageFootnoteLeft.IsVisible = false;
        ReaderLinuxTextFallbackPageFootnoteRight.Content = string.Empty;
        ReaderLinuxTextFallbackPageFootnoteRight.Tag = null;
        ReaderLinuxTextFallbackPageFootnoteRight.IsVisible = false;
        ReaderLinuxTextFallbackPageVertical.FootnoteHrefResolver = null;
        ReaderLinuxTextFallbackPageVertical.Text = string.Empty;
        ReaderLinuxTextFallbackPageVertical.SelectionStart = 0;
        ReaderLinuxTextFallbackPageVertical.SelectionEnd = 0;
        ReaderLinuxTextFallbackPageVertical.IsVisible = false;
        _readerLinuxTextFallbackText = string.Empty;
        _readerLinuxTextFallbackPages.Clear();
        _readerLinuxTextFallbackPageItems.Clear();
        _readerLinuxTextFallbackMoveToChapterEnd = false;
        _readerLinuxTextFallbackEndFragment = null;
        _readerLinuxTextFallbackPendingReflowAnchor = null;
        _readerLinuxTextFallbackReflowSequence++;
        ClearLinuxReaderTextFallbackImages();
        ClearLinuxReaderTextFallbackSelectionState();
    }

    private void ReaderLinuxTextFallbackScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_readerLinuxTextFallbackUpdating) return;
        if (_readerLayout.FlowMode != 0) return;
        if (_readerLinuxTextFallbackSelectionScrollLocked)
        {
            RestoreLinuxTextFallbackSelectionScrollOffset();
            return;
        }
        SyncLinuxReaderTextFallbackScrollState(saveProgress: true);
    }

    private void RestoreLinuxTextFallbackSelectionScrollOffset()
    {
        if (!_readerLinuxTextFallbackSelectionScrollLocked
            || _readerLinuxTextFallbackRestoringSelectionScroll)
        {
            return;
        }

        var target = new Vector(
            Math.Max(0, _readerLinuxTextFallbackSelectionScrollOffset.X),
            Math.Max(0, _readerLinuxTextFallbackSelectionScrollOffset.Y));
        var current = ReaderLinuxTextFallbackScroll.Offset;
        if (Math.Abs(current.X - target.X) <= 0.5
            && Math.Abs(current.Y - target.Y) <= 0.5)
            return;

        _readerLinuxTextFallbackRestoringSelectionScroll = true;
        try
        {
            ReaderLinuxTextFallbackScroll.Offset = target;
        }
        finally
        {
            _readerLinuxTextFallbackRestoringSelectionScroll = false;
        }
    }

    private void ReleaseLinuxTextFallbackSelectionScrollLock()
    {
        if (!_readerLinuxTextFallbackSelectionScrollLocked) return;

        RestoreLinuxTextFallbackSelectionScrollOffset();
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!IsLinuxReaderTextFallbackActive() || _readerLayout.FlowMode != 0)
                {
                    _readerLinuxTextFallbackSelectionScrollLocked = false;
                    return;
                }

                RestoreLinuxTextFallbackSelectionScrollOffset();
                _readerLinuxTextFallbackSelectionScrollLocked = false;
                SyncLinuxReaderTextFallbackScrollState(saveProgress: false);
            },
            DispatcherPriority.Background);
    }

    private bool IsLinuxReaderTextFallbackActive() =>
        OperatingSystem.IsLinux()
        && !_readerIsPdf
        && ReaderLinuxTextFallbackOverlay.IsVisible;

    private ReaderScrollState? CaptureLinuxReaderTextFallbackState()
    {
        if (!IsLinuxReaderTextFallbackActive()) return null;

        if (_readerLayout.FlowMode == 0)
        {
            var extent = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Height);
            var viewport = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Height);
            var position = Math.Max(0, ReaderLinuxTextFallbackScroll.Offset.Y);
            var maximum = Math.Max(0, extent - viewport);
            return new ReaderScrollState(
                position,
                maximum > 0 ? Math.Clamp(position / maximum, 0, 1) : 0,
                Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Width),
                extent,
                Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Width),
                viewport);
        }

        var pageCount = GetLinuxReaderTextFallbackPageCount();
        var spreadSize = _readerLayout.TwoPageMode ? 2 : 1;
        var maximumPage = Math.Max(0, pageCount - spreadSize);
        var pageIndex = _readerLinuxTextFallbackPageIndex < 0
            ? maximumPage
            : Math.Clamp(_readerLinuxTextFallbackPageIndex, 0, maximumPage);
        if (_readerLayout.TwoPageMode && pageIndex % 2 != 0)
            pageIndex--;
        return new ReaderScrollState(
            pageIndex,
            maximumPage > 0 ? Math.Clamp(pageIndex / (double)maximumPage, 0, 1) : 0,
            pageCount,
            pageCount,
            spreadSize,
            spreadSize);
    }

    private bool TryApplyLinuxReaderTextFallbackState()
    {
        var state = CaptureLinuxReaderTextFallbackState();
        if (state is null) return false;
        ApplyReaderScrollState(state);
        return true;
    }

    private void ApplyReaderScrollState(ReaderScrollState state)
    {
        _readerScrollPosition = state.Position;
        _readerScrollRatio = state.Ratio;
        _readerScrollWidth = state.ScrollWidth;
        _readerScrollHeight = state.ScrollHeight;
        _readerClientWidth = state.ClientWidth;
        _readerClientHeight = state.ClientHeight;
    }

    private void SyncLinuxReaderTextFallbackScrollState(bool saveProgress)
    {
        if (!OperatingSystem.IsLinux()
            || _readerIsPdf
            || !ReaderLinuxTextFallbackOverlay.IsVisible)
        {
            return;
        }

        var extent = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Height);
        var viewport = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Height);
        var maximum = Math.Max(0, extent - viewport);
        _readerScrollPosition = Math.Max(0, ReaderLinuxTextFallbackScroll.Offset.Y);
        _readerScrollRatio = maximum > 0 ? Math.Clamp(_readerScrollPosition / maximum, 0, 1) : 0;
        _readerScrollHeight = extent;
        _readerClientHeight = viewport;
        _readerScrollWidth = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Width);
        _readerClientWidth = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Width);
        UpdateReaderToolbar();
        if (saveProgress)
            _ = ObserveReaderTaskAsync(SaveReaderProgressAsync(CancellationToken.None));
    }

    private void SyncLinuxReaderTextFallbackPagedState(bool saveProgress)
    {
        if (!IsLinuxReaderTextFallbackActive()) return;
        var pageCount = GetLinuxReaderTextFallbackPageCount();
        var spreadSize = _readerLayout.TwoPageMode ? 2 : 1;
        var maximum = Math.Max(0, pageCount - spreadSize);
        _readerLinuxTextFallbackPageIndex = _readerLinuxTextFallbackPageIndex < 0
            ? maximum
            : Math.Clamp(_readerLinuxTextFallbackPageIndex, 0, maximum);
        if (_readerLayout.TwoPageMode && _readerLinuxTextFallbackPageIndex % 2 != 0)
            _readerLinuxTextFallbackPageIndex--;
        RenderLinuxReaderTextFallbackPage();
        _readerScrollPosition = _readerLinuxTextFallbackPageIndex;
        _readerScrollRatio = maximum > 0
            ? Math.Clamp(_readerLinuxTextFallbackPageIndex / (double)maximum, 0, 1)
            : 0;
        _readerScrollWidth = pageCount;
        _readerClientWidth = spreadSize;
        _readerScrollHeight = pageCount;
        _readerClientHeight = spreadSize;
        UpdateReaderToolbar();
        if (saveProgress)
            _ = ObserveReaderTaskAsync(SaveReaderProgressAsync(CancellationToken.None));
    }

    private void SyncLinuxReaderTextFallbackState(bool saveProgress)
    {
        if (_readerLayout.FlowMode == 0)
            SyncLinuxReaderTextFallbackScrollState(saveProgress);
        else
            SyncLinuxReaderTextFallbackPagedState(saveProgress);
    }

    private void UpdateLinuxReaderTextFallbackMode()
    {
        var paged = _readerLayout.FlowMode == 1;
        ReaderLinuxTextFallbackScroll.IsVisible = !paged;
        ReaderLinuxTextFallbackPagedRoot.IsVisible = paged;
        var twoPage = paged && _readerLayout.TwoPageMode;
        ReaderLinuxTextFallbackPagedContent.ColumnDefinitions.Clear();
        if (twoPage)
        {
            ReaderLinuxTextFallbackPagedContent.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ReaderLinuxTextFallbackPagedContent.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(28)));
            ReaderLinuxTextFallbackPagedContent.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ReaderLinuxTextFallbackPagedContent.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            ReaderLinuxTextFallbackPagedContent.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ReaderLinuxTextFallbackPagedContent.HorizontalAlignment = HorizontalAlignment.Center;
        }
        Grid.SetColumn(ReaderLinuxTextFallbackPageRight, twoPage ? 2 : 0);
        Grid.SetColumn(ReaderLinuxTextFallbackPageImageRight, twoPage ? 2 : 0);
        if (paged)
            RebuildLinuxReaderTextFallbackPages();
    }
}
