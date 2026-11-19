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

/// <summary>
/// Sending library books to a Kindle device, by USB or email.
/// </summary>
public partial class MainWindow
{
    private async Task ApplyDeviceModelAsync(string? model)
    {
        if (CurrentDevice is not { } device) return;
        var normalized = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        try
        {
            if (normalized is null)
            {
                await _deviceModelStore.DeleteModelAsync(device.Identity, _lifetimeCancellation.Token);
                // A global default is only needed until the user has a chance
                // to identify this device. Choosing the device's own name from
                // the picker should remove that onboarding fallback as well.
                if (_appSettings.DefaultDeviceModel is not null)
                {
                    _appSettings = AppSettings.Normalize(_appSettings with { DefaultDeviceModel = null });
                    await _appSettingsStore.SaveAsync(_appSettings, _lifetimeCancellation.Token);
                }
            }
            else
                await _deviceModelStore.SetModelAsync(device.Identity, normalized, _lifetimeCancellation.Token);
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("无法保存设备型号：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法保存设备型号"), UiText.Localize(exception.Message));
            return;
        }

        _deviceDisplayName = normalized ?? device.Name;
        KindleStatusText.Text = _deviceDisplayName;
        KindleConnectionText.Text = T("已连接");
        KindleConnectionText.IsVisible = false;
        DevicePageDeviceText.Text = $"{_deviceDisplayName} · {device.ConnectionLabel}";
        DeviceNameButton.IsEnabled = true;
        if (DeviceResourcePage.IsVisible)
            DeviceResourceStatusText.Text = $"{_deviceDisplayName} · {device.ConnectionLabel}";
    }

    private void DeviceNameButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentDevice is not null) ShowDeviceModelPicker();
    }

    private async void SendSelectedBooksToKindleBatchButton_Click(object? sender, RoutedEventArgs e) =>
        await SendSelectedBooksToKindleAsync();

    private async void SendSelectedBooksByEmailBatchButton_Click(object? sender, RoutedEventArgs e) =>
        await SendSelectedBooksByEmailAsync();

    private sealed class PreparedKindleTransfer : IDisposable
    {
        private readonly string? _temporaryDirectory;

        public PreparedKindleTransfer(BookFile file, string sourcePath, string? temporaryDirectory = null, string? coverOverridePath = null)
        {
            File = file;
            SourcePath = sourcePath;
            CoverOverridePath = coverOverridePath;
            _temporaryDirectory = temporaryDirectory;
        }

        public BookFile File { get; }
        public string SourcePath { get; }

        // Library cover to prefer over the file's embedded one when building
        // the Kindle home-screen thumbnail (e.g. an online-matched cover).
        public string? CoverOverridePath { get; }

        public void Dispose()
        {
            if (_temporaryDirectory is null) return;
            try
            {
                if (System.IO.File.Exists(SourcePath)) System.IO.File.Delete(SourcePath);
                if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // CoverPath is stored relative to the data root; older entries may be
    // absolute. Returns null when the book has no readable local cover.
    private string? ResolveBookCoverAbsolutePath(Book book)
    {
        if (string.IsNullOrWhiteSpace(book.CoverPath)) return null;
        try
        {
            var path = Path.IsPathRooted(book.CoverPath)
                ? book.CoverPath
                : Path.GetFullPath(Path.Combine(_paths.Data, book.CoverPath));
            return File.Exists(path) ? path : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<PreparedKindleTransfer> PrepareKindleTransferAsync(
        KindleDevice device,
        Book book,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        BookFile? requestedSource = null)
    {
        var sourceCandidates = DeviceTransferPolicy.GetCandidates(device.Profile, book.Files);
        var sourceFile = requestedSource is not null
            ? sourceCandidates.FirstOrDefault(file => file.Id == requestedSource.Id)
            : sourceCandidates.FirstOrDefault();
        if (sourceFile is null)
            throw new NotSupportedException(T("当前设备没有可发送的书籍格式，支持：{0}。", string.Join("、", device.Profile.BookFormats).ToUpperInvariant()));
        var sourcePath = _library.GetAbsoluteFilePath(sourceFile);
        if (!File.Exists(sourcePath))
            sourcePath = await EnsureBookFileAvailableAsync(sourceFile, cancellationToken)
                ?? sourcePath;
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(T("找不到本地书籍文件，请先刷新书库。"), sourcePath);
        var coverOverridePath = ResolveBookCoverAbsolutePath(book);

        var requiresMetadataRepair = device.Profile.UsesKindleThumbnails
            && KindleTransferPolicy.RequiresLegacyMetadataRepair(sourceFile, sourcePath);
        if (!DeviceTransferPolicy.RequiresKindleConversion(device.Profile, sourceFile) && !requiresMetadataRepair)
            return new PreparedKindleTransfer(sourceFile, sourcePath, coverOverridePath: coverOverridePath);

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Libzora", "kindle-ready", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var destinationPath = Path.Combine(
            temporaryDirectory,
            KindleTransferPolicy.CreateSafeFileName(book.Title, ".azw3"));
        try
        {
            var conversionProgress = progress is null
                ? null
                : new Progress<FormatConversionProgress>(value => progress.Report(new TransferProgress(
                    value.RoundedPercentage,
                    100,
                    T("正在生成 Kindle 兼容版本 · {0}%", value.RoundedPercentage))));
            try
            {
                await _formatConverter.ConvertAsync(
                    sourcePath,
                    destinationPath,
                    conversionProgress,
                    cancellationToken,
                    new FormatConversionMetadata(book.Title, book.Authors, coverOverridePath));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Some hand-built EPUBs trip a Calibre bug in mobi_output's
                // remove_html_cover (KeyError on a manifest path). Round-tripping
                // through a normalized EPUB repairs the manifest and the second
                // pass then converts cleanly.
                var normalized = Path.Combine(
                    temporaryDirectory,
                    KindleTransferPolicy.CreateSafeFileName(book.Title, ".epub"));
                var metadata = new FormatConversionMetadata(book.Title, book.Authors, coverOverridePath);
                await _formatConverter.ConvertAsync(
                    sourcePath,
                    normalized,
                    conversionProgress,
                    cancellationToken,
                    metadata);
                await _formatConverter.ConvertAsync(
                    normalized,
                    destinationPath,
                    conversionProgress,
                    cancellationToken,
                    metadata);
            }

            var output = new FileInfo(destinationPath);
            if (!output.Exists || output.Length < 1024)
                throw new InvalidDataException(T("Kindle 兼容版本生成失败：输出文件为空。"));

            // A missing cover is not fatal: the Kindle falls back to its
            // default thumbnail, so send anyway instead of blocking the book.

            var preparedFile = new BookFile
            {
                Id = sourceFile.Id,
                BookId = sourceFile.BookId,
                Format = "azw3",
                RelativePath = Path.GetFileName(destinationPath),
                Size = output.Length,
                Sha256 = await Hashing.Sha256Async(destinationPath, cancellationToken)
            };
            return new PreparedKindleTransfer(preparedFile, destinationPath, temporaryDirectory, coverOverridePath);
        }
        catch
        {
            try { if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
