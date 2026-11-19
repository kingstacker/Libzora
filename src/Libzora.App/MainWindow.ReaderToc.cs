using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Port of the WinUI reference's minimal TOC rail (极简目录): a narrow marker
/// rail that keeps the chapter map visible without taking the reading column
/// away from the body. Markers wave toward the pointer, a floating label shows
/// the hovered chapter title, the wheel scrolls with a short eased animation
/// and the up/down indicators reflect the scroll extent.
/// </summary>
public sealed record ReaderTocMarker(EpubReaderNavigationItem Item, bool IsCurrent)
{
    public ReaderPalette Palette { get; set; } = ReaderPalette.For(ReaderTheme.Classic);
    public string Title => Item.Title;
    public IBrush Fill => GetFill(IsCurrent);
    public IBrush GetFill(bool isCurrent) => isCurrent ? Palette.AccentBrush : Palette.BorderBrush;
}
public partial class MainWindow
{
    private const double ReaderTocMinimalWidth = 52d;
    private const double ReaderCompactMarkerMinimumWidth = 8d;
    // A right-only wave starts at the centered 8 px resting marker. Keep its
    // longest stroke inside the 52 px rail (including the right divider).
    private const double ReaderCompactMarkerMaximumWidth = 28d;
    private const double ReaderCompactMarkerWaveRadius = 96d;
    private const double ReaderCompactScrollAnimationDurationMs = 160d;
    private bool _readerTocMinimal;
    private bool _readerTocExpanded = true;
    private IReadOnlyList<EpubReaderNavigationItem> _readerCompactNavigationItems = [];
    private string? _readerCompactSelectedTarget;
    private DispatcherTimer? _readerCompactScrollTimer;
    private bool _readerCompactScrollAnimating;
    private double _readerCompactScrollStart;
    private double _readerCompactScrollTarget;
    private DateTimeOffset _readerCompactScrollStartedAt;
    private bool _readerCompactPointerActive;
    private double _readerCompactPointerY;
    private Control? _readerChapterPreviewTarget;
    private bool _readerChapterPreviewAbove;
    private readonly Dictionary<string, string> _readerChapterPreviewTextCache = new(StringComparer.OrdinalIgnoreCase);
    private int _readerChapterPreviewRequestVersion;

    // 目录列表的滚动条自动隐藏：滚动时淡入，静止约一秒后淡出，悬停条上
    // 期间保持可见。滚动条属于 ListBox 的模板，需要在模板完成后把状态类
    // 加到内部 ScrollViewer 上；直接设置 ScrollBar 的 Opacity 会在模板重建
    // 或主题刷新时失效。
    private const int ReaderTocScrollBarIdleHideMs = 900;
    private const string ReaderTocScrollClass = "readerTocScroll";
    private const string ReaderTocScrollingClass = "readerTocScrolling";
    private ScrollViewer? _readerTocScrollViewer;
    private ScrollBar? _readerTocScrollBar;
    private DispatcherTimer? _readerTocScrollBarHideTimer;
    private bool _readerTocScrollBarAttachPending;

    private void ReaderTocList_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        QueueReaderTocScrollBarAttach();
        ScheduleReaderTocTopSpacerRepair();
    }

    private bool _readerTocTopSpacerRepairPending;
    private bool _readerTocTopSpacerRepaired;

    // The virtualizing panel keeps a spacer for the rows it believes are above
    // the viewport. When the rail settles on the first chapter that spacer can
    // survive and paint as an empty strip above row 0. Detect exactly that state
    // — offset at the very top but the first row pushed down — and re-realize
    // the source so the panel measures again from the first row.
    private void ScheduleReaderTocTopSpacerRepair()
    {
        if (_readerTocTopSpacerRepairPending) return;
        _readerTocTopSpacerRepairPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _readerTocTopSpacerRepairPending = false;
            RepairReaderTocTopSpacer();
        }, DispatcherPriority.Loaded);
    }

    private void RepairReaderTocTopSpacer()
    {
        if (_readerTocTopSpacerRepaired || !ReaderTocPanel.IsVisible) return;
        if (GetReaderTocScrollViewer() is not { } scrollViewer) return;
        if (scrollViewer.Offset.Y > 0.5) return;
        if (ReaderTocList.ContainerFromIndex(0) is not { } firstRow) return;
        if (firstRow.TranslatePoint(default, scrollViewer)?.Y is not { } top || top <= 2) return;

        var selected = ReaderTocList.SelectedItem as ReaderTocRow;
        var source = ReaderTocList.ItemsSource;
        _suppressReaderTocSelectionNavigation = true;
        try
        {
            ReaderTocList.SelectedItems?.Clear();
            PdfAnnotTrace.Write($"toc spacer repair fired: firstRowTop={top:F1}");
            _readerTocTopSpacerRepaired = true;
            ReaderTocList.ItemsSource = null;
            ReaderTocList.ItemsSource = source;
            scrollViewer.Offset = default;
            if (selected is not null && (source as IReadOnlyList<ReaderTocRow>)?.Contains(selected) == true)
                ReaderTocList.SelectedItems?.Add(selected);
        }
        finally
        {
            _suppressReaderTocSelectionNavigation = false;
        }
    }

    // ---- Folding ------------------------------------------------------
    // _readerTocItems stays the flat, authoritative navigation list: the
    // progress slider, previous/next chapter and search all index into it.
    // _readerTocRows is index-aligned with it and carries only presentation
    // state, so folding can never change what those features see.
    private ReaderTocRow[] _readerTocRows = [];
    // Destination of the outline entry the reader followed from an explicit
    // click. The rail keeps it selected instead of snapping back to the first
    // entry that shares its page (a datasheet puts several headings on one
    // page, sometimes with the same offset). Stored as a target string because
    // PDF navigation rebuilds the item objects on every jump.
    private string? _readerPinnedTocTarget;
    private double? _readerPinnedTocNavigationTop;
}
