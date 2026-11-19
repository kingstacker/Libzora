using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// TOC navigation, panel layout and zen toggle.
/// </summary>
public partial class MainWindow
{
    private void SetReaderCompactSelectedItem(EpubReaderNavigationItem? item)
    {
        _readerCompactSelectedTarget = item?.Target;
        RefreshReaderCompactMarkers();
        QueueReaderCompactScrollIndicatorUpdate();
    }

    private void RefreshReaderCompactMarkers()
    {
        HideReaderCompactHoverLabel();
        ReaderTocCompactList.ItemsSource = _readerCompactNavigationItems
            .Select(item => new ReaderTocMarker(
                item,
                _readerCompactSelectedTarget is not null
                    && item.Target.Equals(
                        _readerCompactSelectedTarget,
                        StringComparison.OrdinalIgnoreCase))
            { Palette = ReaderPalette.For(CurrentReaderAppearance.Theme) })
            .ToArray();
    }

    private void QueueReaderCompactScrollIndicatorUpdate()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ReaderTocCompactPanel.IsVisible)
            {
                UpdateReaderCompactScrollIndicators();
                UpdateReaderCompactMarkerWave();
            }
        }, DispatcherPriority.Background);
    }

    private void NavigateToReaderTocItem(EpubReaderNavigationItem item)
    {
        if (_readerIsPdf)
        {
            // Keep the body stable while a PDF destination is being resolved;
            // the PDF navigation commits the full TOC state after the page and
            // its outline offset have both settled.
            _ = ObserveReaderTaskAsync(
                NavigateToReaderItemAsync(
                    item,
                    _readerSession.GetTokenOr(CancellationToken.None),
                    ReaderNavigationIntent.Toc));
            return;
        }

        // A TOC click is an explicit user target: it must start at the target
        // chapter's first line (or its own anchor), never inherit a leftover
        // "move to chapter end" intent from a superseded previous-chapter turn.
        SetReaderCompactSelectedItem(item);
        // Selecting the item in the full TOC list triggers the selection
        // handler for real user clicks. This is a programmatic selection,
        // therefore use the guarded sync helper and start exactly one jump.
        SetReaderTocSelection(item);
        _ = ObserveReaderTaskAsync(
            NavigateToReaderItemAsync(
                item,
                _readerSession.GetTokenOr(CancellationToken.None),
                ReaderNavigationIntent.Toc));
    }

    private void ApplyReaderPanelLayout()
    {
        if (!ReaderRoot.IsVisible) return;

        // Mirror the WinUI reference's TOC column sizing: the first column is
        // 286 when expanded, 52 when the minimal rail is shown, and 0 when the
        // TOC is hidden entirely.
        ReaderRoot.ColumnDefinitions[0].Width = new GridLength(
            _readerTocExpanded ? 286d : _readerTocMinimal ? ReaderTocMinimalWidth : 0d);
        ReaderTocPanel.IsVisible = _readerTocExpanded;
        ReaderTocCompactPanel.IsVisible = _readerTocMinimal;
        if (ReaderAssistantPanel.IsVisible && !_readerZenMode)
            SetReaderAiPanelWidth(GetReaderAssistantPanelWidth());
        UpdateReaderToolbarAutoHide();
        // Opening the TOC changes the layout, not the availability of the
        // toolbar action. Keep its label at full opacity in both states.
        ReaderTocToggleButton.Opacity = 1;
        ScheduleLinuxReaderTextFallbackReflow();
        ScheduleReaderRelayout();
    }

    private void UpdateReaderZenTocToggle()
    {
        var label = _readerTocMinimal ? T("关闭极简目录") : T("显示极简目录");
        if (ReaderZenTocButton is not null)
            ReaderZenTocButton.Content = label;
        if (ReaderZenTitleTocText is not null)
            ReaderZenTitleTocText.Text = label;
        if (ReaderZenPopupTocText is not null)
            ReaderZenPopupTocText.Text = label;
    }
}
