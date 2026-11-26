using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

public sealed record Stage3DashboardDayViewModel(
    string DateLabel,
    string TimeLabel,
    double BarHeight,
    double BarOpacity)
{
    public string DetailLabel { get; init; } = "";
    public double ListeningBarHeight { get; init; }
    public double VisualBarHeight => Math.Max(0d, BarHeight - ListeningBarHeight);
    public bool HasListening => ListeningBarHeight > 0d;
    public bool HasVisual => VisualBarHeight > 0d;
}

public sealed record Stage3DashboardBarViewModel(
    string Label,
    string ValueLabel,
    double BarWidth)
{
    public double ListeningBarWidth { get; init; }
    public double VisualBarWidth => Math.Max(0d, BarWidth - ListeningBarWidth);
    public bool HasListening => ListeningBarWidth > 0d;
    public bool HasVisual => VisualBarWidth > 0d;
}

public sealed record Stage3DashboardRecentViewModel(
    string Title,
    double ProgressPercent,
    long Seconds,
    DateTimeOffset UpdatedAt)
{
    public Guid BookId { get; init; }
    public Guid BookFileId { get; init; }
    public long ListeningSeconds { get; init; }
    public string ProgressLabel => $"{ProgressPercent:0.#}%";

    public string DurationLabel => ListeningSeconds > 0
        ? UiText.Get("{0}（含听书 {1}）", FormatTime(Seconds), FormatTime(ListeningSeconds))
        : FormatTime(Seconds);

    public string UpdatedLabel => UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string FormatTime(long seconds) => seconds switch
    {
        < 60 => UiText.Get("{0} 秒", seconds),
        < 3600 => UiText.Get("{0} 分钟", Math.Max(1, seconds / 60)),
        _ => UiText.Get("{0:0.#} 小时", seconds / 3600d)
    };
}
