using System.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Libzora.McpServer;

/// <summary>
/// Sending, conversion, import and deletion.
/// </summary>
public sealed partial class LibzoraMcpTools
{
    /// <summary>
    /// Sends one local book file through USB or the configured Send to Kindle
    /// email account. dryRun is mandatory in the schema and defaults to true.
    /// The existing web workflow is UI-bound and therefore only supports a
    /// dry-run from this standalone server.
    /// </summary>
    [McpServerTool(
        Name = "send_to_kindle",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Send a book to Kindle over usb, email, or web. This can have a real external/device effect; dryRun is required and defaults to true. web actual execution is unavailable because the existing KindleWebSendWorkflow requires the desktop UI page.")]
    public async Task<SendToKindleResult> SendToKindleAsync(
        [Description("Book GUID returned by the library; provide this or filePath, but not both.")] string? bookId = null,
        [Description("Absolute or relative local file path; provide this or bookId, but not both.")] string? filePath = null,
        [Description("Transfer channel: usb, email, or web. Defaults to usb.")] string channel = "usb",
        [Description("When true, only return the planned action list. Defaults to true; false performs the send for usb/email.")] bool dryRun = true,
        [Description("Optional connected-device identity, root path, or exact name for the usb channel.")] string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedChannel = NormalizeChannel(channel);
        var source = await ResolveSendSourceAsync(bookId, filePath, normalizedChannel, cancellationToken);
        var actions = new List<string>();
        string? deviceIdentity = null;
        string? deviceName = null;
        string? recipient = null;
        string? sender = null;

        switch (normalizedChannel)
        {
            case "usb":
            {
                var device = await ResolveSingleDeviceAsync(deviceId, cancellationToken);
                deviceIdentity = device.Identity;
                deviceName = device.Name;
                actions.Add($"Send '{source.Path}' as {source.Format.ToUpperInvariant()} to USB device '{device.Name}'.");
                if (!dryRun)
                {
                    if (DeviceTransferPolicy.RequiresKindleConversion(device.Profile, source.BookFile))
                    {
                        var temporaryDirectory = Path.Combine(
                            Path.GetTempPath(),
                            "Libzora",
                            "mcp-kindle-send",
                            Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(temporaryDirectory);
                        try
                        {
                            var convertedPath = Path.Combine(
                                temporaryDirectory,
                                KindleTransferPolicy.CreateSafeFileName(source.Title, ".azw3"));
                            var converter = _formatConverter
                                ?? throw new McpException("Book format conversion is not registered in this MCP server.");
                            await converter.ConvertAsync(
                                source.Path,
                                convertedPath,
                                cancellationToken: cancellationToken,
                                metadata: new FormatConversionMetadata(source.Title, string.Empty));
                            var convertedInfo = new FileInfo(convertedPath);
                            var convertedFile = new BookFile
                            {
                                Id = source.BookFile.Id,
                                BookId = source.BookFile.BookId,
                                Format = "azw3",
                                RelativePath = Path.GetFileName(convertedPath),
                                Size = convertedInfo.Length,
                                Sha256 = await Hashing.Sha256Async(convertedPath, cancellationToken)
                            };
                            await _devices.SendBookAsync(
                                device,
                                convertedFile,
                                convertedPath,
                                cancellationToken: cancellationToken);
                        }
                        finally
                        {
                            try { Directory.Delete(temporaryDirectory, recursive: true); }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                    }
                    else
                    {
                        await _devices.SendBookAsync(
                            device,
                            source.BookFile,
                            source.Path,
                            cancellationToken: cancellationToken);
                    }
                }
                break;
            }
            case "email":
            {
                var settings = await RequireEmailSettingsAsync(cancellationToken);
                recipient = settings.KindleEmailAddress;
                sender = settings.SenderEmailAddress;
                actions.Add($"Send '{source.Path}' as an email attachment from '{sender}' to '{recipient}'.");
                if (!dryRun)
                    await RequireEmailSender().SendAsync(
                        settings,
                        source.Path,
                        $"Libzora：{source.Title}",
                        cancellationToken);
                break;
            }
            case "web":
                actions.Add($"Stage '{source.Path}' in the existing Send to Kindle web workflow.");
                if (!dryRun)
                    throw new McpException(
                        "The existing KindleWebSendWorkflow requires the desktop UI's IKindleWebPage and cannot run in the standalone MCP server.");
                break;
        }

        return new SendToKindleResult(
            normalizedChannel,
            dryRun,
            !dryRun,
            source.BookId,
            source.Title,
            source.Path,
            source.Format,
            deviceIdentity,
            deviceName,
            recipient,
            sender,
            actions);
    }

    /// <summary>
    /// Converts a local file or a stored book format using the existing
    /// BookFormatConversionPolicy/IBookFormatConverter path. Existing output
    /// files and existing target formats are never overwritten.
    /// </summary>
    [McpServerTool(
        Name = "convert_book",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Convert a book to targetFormat using the existing Calibre-backed converter. The operation writes a file, never overwrites an existing output, and returns an error on conflicts.")]
    public async Task<ConvertBookResult> ConvertBookAsync(
        [Description("Target output format, for example epub, azw3, pdf, or mobi.")] string targetFormat,
        [Description("Absolute or relative source file path; provide this or bookId, but not both.")] string? filePath = null,
        [Description("Book GUID returned by the library; provide this or filePath, but not both.")] string? bookId = null,
        [Description("Destination directory. For a bookId without this value, the converted file is added to that book in the library; for a filePath it defaults to the source directory.")] string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var target = BookFormatConversionPolicy.Normalize(targetFormat);
        if (!BookFormatConversionPolicy.IsConvertibleFormat(target))
            throw new McpException("targetFormat must be one of epub, azw3, pdf, or mobi.");

        var source = await ResolveConversionSourceAsync(filePath, bookId, target, cancellationToken);
        if (!BookFormatConversionPolicy.IsCalibreInputFormat(source.Format))
            throw new McpException($"The source format '{source.Format}' is not supported by the existing conversion policy.");

        if (source.Book is not null
            && source.Book.Files.Any(file => string.Equals(
                BookFormatConversionPolicy.Normalize(file.Format),
                target,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new McpException($"Book '{source.Book.Id}' already has a {target.ToUpperInvariant()} file; no file was overwritten.");
        }

        var title = source.Book?.Title ?? Path.GetFileNameWithoutExtension(source.Path);
        var authors = source.Book?.Authors ?? string.Empty;
        var outputDirectoryPath = string.IsNullOrWhiteSpace(outputDirectory)
            ? source.Book is null
                ? Path.GetDirectoryName(source.Path) ?? throw new McpException("The source file has no destination directory.")
                : null
            : ValidatePath(outputDirectory, nameof(outputDirectory));
        var metadata = source.Book is null
            ? null
            : new FormatConversionMetadata(source.Book.Title, source.Book.Authors);

        if (source.Book is not null && outputDirectoryPath is null)
        {
            var libraryOutputPath = Path.Combine(
                Path.GetDirectoryName(source.Path)
                    ?? throw new McpException("The library source has no parent directory."),
                KindleTransferPolicy.CreateSafeFileName(title, "." + target));
            if (File.Exists(libraryOutputPath))
                throw new McpException($"The library conversion output already exists and was not overwritten: {libraryOutputPath}");

            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "LibzoraMcpConversions",
                Guid.NewGuid().ToString("N"));
            var temporaryOutput = Path.Combine(
                temporaryDirectory,
                KindleTransferPolicy.CreateSafeFileName(title, "." + target));
            try
            {
                Directory.CreateDirectory(temporaryDirectory);
                await RequireFormatConverter().ConvertAsync(
                    source.Path,
                    temporaryOutput,
                    cancellationToken: cancellationToken,
                    metadata: metadata);
                var addedFile = await _library.AddFileToBookAsync(
                    source.Book.Id,
                    temporaryOutput,
                    cancellationToken);
                var absolutePath = Path.GetFullPath(_library.GetAbsoluteFilePath(addedFile));
                return new ConvertBookResult(
                    source.Book.Id,
                    source.Path,
                    absolutePath,
                    target,
                    true,
                    false);
            }
            finally
            {
                TryDeleteDirectory(temporaryDirectory);
            }
        }

        var destinationDirectory = outputDirectoryPath
            ?? throw new McpException("A destination directory is required for this conversion.");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(
            destinationDirectory,
            KindleTransferPolicy.CreateSafeFileName(title, "." + target));
        if (File.Exists(destination))
            throw new McpException($"The conversion output already exists and was not overwritten: {destination}");
        if (string.Equals(Path.GetFullPath(source.Path), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new McpException("The conversion output cannot be the source file.");

        await RequireFormatConverter().ConvertAsync(
            source.Path,
            destination,
            cancellationToken: cancellationToken,
            metadata: metadata);
        return new ConvertBookResult(
            source.Book?.Id,
            source.Path,
            Path.GetFullPath(destination),
            target,
            false,
            false);
    }

    /// <summary>
    /// Imports one file through the existing library import API. The conflict
    /// resolver deliberately rejects same-content and same-metadata conflicts
    /// so import never overwrites or silently merges an existing book.
    /// </summary>
    [McpServerTool(
        Name = "import_book",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Import one local book file into the Libzora library. Existing files are never overwritten; duplicate or same-book conflicts return an error message.")]
    public async Task<ImportBookResult> ImportBookAsync(
        [Description("Absolute or relative local book file path to import.")] string filePath,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = ValidateExistingFilePath(filePath, nameof(filePath));
        var result = await _library.ImportAsync(
            [sourcePath],
            cancellationToken: cancellationToken,
            conflictResolver: conflict => throw new InvalidOperationException(
                $"A book with the same title and author already exists: '{conflict.ExistingBook.Title}'. Import was not performed."));
        var item = result.Items.SingleOrDefault()
            ?? throw new McpException("The library import returned no result.");

        if (!item.Succeeded || !item.Added || item.BookId is not { } bookId)
        {
            throw new McpException(item.Message ?? "The book already exists or could not be imported; no file was overwritten.");
        }

        var book = item.Book ?? await _library.GetBookAsync(bookId, cancellationToken);
        if (book is null)
            throw new McpException($"The book was imported but could not be read back from the library: {bookId}.");

        var importedFormat = BookFormatConversionPolicy.Normalize(Path.GetExtension(sourcePath));
        var importedFiles = book.Files
            .Where(file => string.Equals(
                BookFormatConversionPolicy.Normalize(file.Format),
                importedFormat,
                StringComparison.OrdinalIgnoreCase))
            .Select(file => new BookFilePathInfo(
                file.Id,
                file.Format,
                Path.GetFullPath(_library.GetAbsoluteFilePath(file)),
                file.Size,
                File.Exists(_library.GetAbsoluteFilePath(file))))
            .ToArray();
        return new ImportBookResult(
            book.Id,
            book.Title,
            importedFormat,
            importedFiles,
            item.Message ?? "Imported");
    }

    /// <summary>
    /// Removes one library book by moving it to the library trash. dryRun
    /// defaults to true so a client must explicitly request the deletion.
    /// </summary>
    [McpServerTool(
        Name = "delete_book",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Move a Libzora library book and its files to the trash (recoverable in the desktop app). dryRun defaults to true; set dryRun=false to actually delete.")]
    public async Task<DeleteBookResult> DeleteBookAsync(
        [Description("Book GUID returned by list_library or search_books.")] Guid bookId,
        [Description("When true, only report the planned action. Defaults to true; false performs the deletion.")] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var book = await GetBookOrThrowAsync(bookId, cancellationToken);
        var actions = new[] { $"Move '{book.Title}' and its library files to trash." };
        if (dryRun)
            return new DeleteBookResult(book.Id, book.Title, dryRun, false, actions);

        await _library.DeleteAsync(bookId, cancellationToken);
        return new DeleteBookResult(book.Id, book.Title, false, true, actions);
    }

    /// <summary>
    /// Removes one book file from a connected device by its device-relative
    /// path (as reported by list_device_library). dryRun defaults to true so
    /// a client must explicitly request the deletion.
    /// </summary>
    [McpServerTool(
        Name = "delete_device_book",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Delete one book from a connected Kindle or supported reader device. dryRun defaults to true; set dryRun=false to actually delete.")]
    public async Task<DeleteDeviceBookResult> DeleteDeviceBookAsync(
        [Description("Device-relative path of the book file, exactly as reported by list_device_library.")] string relativePath,
        [Description("Optional device identity, root path, or exact device name; required when more than one device is connected.")] string? deviceId = null,
        [Description("When true, only report the planned action. Defaults to true; false performs the deletion.")] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new McpException("relativePath must not be empty.");

        var device = await ResolveSingleDeviceAsync(deviceId, cancellationToken);
        var books = await _devices.ScanBooksAsync(device, cancellationToken);
        var book = books.FirstOrDefault(candidate => string.Equals(
                candidate.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException(
                $"No book with relative path '{relativePath}' was found on device '{device.Name}' ({device.RootPath}).");

        var actions = new[] { $"Delete '{book.Title}' ({book.RelativePath}) from '{device.Name}'." };
        if (dryRun)
            return new DeleteDeviceBookResult(device.Identity, device.Name, book.RelativePath, book.Title, dryRun, false, actions);

        await _devices.RemoveBookAsync(device, book, cancellationToken);
        return new DeleteDeviceBookResult(device.Identity, device.Name, book.RelativePath, book.Title, false, true, actions);
    }

private async Task<Book> GetBookOrThrowAsync(Guid bookId, CancellationToken cancellationToken)
    {
        return await _library.GetBookAsync(bookId, cancellationToken)
            ?? throw new McpException($"Book '{bookId}' was not found in the Libzora library.");
    }

    private async Task<KindleDevice> ResolveSingleDeviceAsync(
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var filter = ValidateOptionalDeviceId(deviceId);
        var devices = await _devices.DetectDevicesAsync(cancellationToken);
        if (filter is not null)
        {
            devices = devices.Where(device => DeviceMatches(device, filter)).ToArray();
            if (devices.Count == 0)
                throw new McpException($"No connected device matched '{filter}'.");
        }

        if (devices.Count == 0)
            throw new McpException("No connected reader device was detected.");
        if (devices.Count > 1)
            throw new McpException("More than one connected device was detected; provide deviceId.");
        return devices[0];
    }
}
