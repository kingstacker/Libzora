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

public sealed class PlatformDiagnosticViewModel : ObservableObject, IDisposable
{
    public PlatformDiagnosticViewModel(PlatformDiagnostic item)
    {
        Item = item;
        UiText.LanguageChanged += OnLanguageChanged;
    }

    private bool _isRepairing;

    public PlatformDiagnostic Item { get; }
    public string Name => UiText.Localize(Item.Name);
    public string Detail => UiText.Localize(Item.Detail);
    public bool CanRepair => Item.RepairKind != PlatformDiagnosticRepairKind.None;
    public bool CanStartRepair => CanRepair && !IsRepairing;
    public bool IsRepairing
    {
        get => _isRepairing;
        set
        {
            if (!SetProperty(ref _isRepairing, value)) return;
            OnPropertyChanged(nameof(CanStartRepair));
            OnPropertyChanged(nameof(StatusLabel));
        }
    }
    public string RepairLabel => Item.RepairKind switch
    {
        PlatformDiagnosticRepairKind.InstallCalibre => UiText.Get("下载/安装 Calibre"),
        PlatformDiagnosticRepairKind.InstallTts => UiText.Get("安装 TTS"),
        _ => UiText.Get("修复")
    };
    public string StatusLabel => IsRepairing
        ? UiText.Get("正在安装 {0}", Name)
        : Item.Status switch
        {
            PlatformDiagnosticStatus.Ready => UiText.Get("正常"),
            PlatformDiagnosticStatus.Warning => UiText.Get("注意"),
            _ => UiText.Get("不可用")
        };
    public IBrush StatusBrush => Item.Status switch
    {
        PlatformDiagnosticStatus.Ready => AppAppearanceResources.GetBrush("SuccessBrush"),
        PlatformDiagnosticStatus.Warning => AppAppearanceResources.GetBrush("WarningBrush"),
        _ => AppAppearanceResources.GetBrush("DangerBrush")
    };

    public void RefreshLocalizedProperties()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(RepairLabel));
        OnPropertyChanged(nameof(StatusLabel));
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshLocalizedProperties();

    public void Dispose() => UiText.LanguageChanged -= OnLanguageChanged;
}

public sealed class LibraryTrashItemViewModel : ObservableObject, IDisposable
{
    public LibraryTrashItemViewModel(LibraryTrashItem item)
    {
        Item = item;
        UiText.LanguageChanged += OnLanguageChanged;
    }

    public LibraryTrashItem Item { get; }
    public string DisplayTitle => Item.DisplayTitle;
    public string DetailText => string.Join(
        " · ",
        new[]
        {
            Item.KindLabel,
            Item.FormatLabel,
            Item.SizeLabel,
            Item.DeletedAtLabel
        }.Where(value => value.Length > 0));
    public string RestoreLabel => UiText.Get("恢复");
    public string PurgeLabel => UiText.Get("永久删除");

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(RestoreLabel));
        OnPropertyChanged(nameof(PurgeLabel));
    }

    public void Dispose() => UiText.LanguageChanged -= OnLanguageChanged;
}
