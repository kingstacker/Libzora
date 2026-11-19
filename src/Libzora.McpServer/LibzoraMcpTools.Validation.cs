using System.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Libzora.McpServer;

/// <summary>
/// Argument validation and email helpers.
/// </summary>
public sealed partial class LibzoraMcpTools
{
    private async Task<ResolvedSendSource> ResolveSendSourceAsync(
        string? bookId,
        string? filePath,
        string channel,
        CancellationToken cancellationToken)
    {
        var hasBookId = !string.IsNullOrWhiteSpace(bookId);
        var hasFilePath = !string.IsNullOrWhiteSpace(filePath);
        if (hasBookId == hasFilePath)
            throw new McpException("Provide exactly one of bookId or filePath.");

        if (hasBookId)
        {
            var id = ParseBookId(bookId!);
            var book = await GetBookOrThrowAsync(id, cancellationToken);
            var candidates = channel switch
            {
                "email" => KindleEmailSelectionPolicy.GetCandidates(book.Files),
                "web" => KindleWebFilePolicy.GetCandidates(book.Files),
                _ => KindleTransferPolicy.GetCandidates(book.Files)
            };
            var file = candidates.FirstOrDefault()
                ?? throw new McpException(
                    $"Book '{book.Title}' has no file supported by the {channel} channel.");
            var path = ValidateExistingFilePath(
                _library.GetAbsoluteFilePath(file),
                "bookId");
            ValidateSendFormatAndSize(path, channel);
            return new ResolvedSendSource(book.Id, book.Title, path, file.Format, file);
        }

        var sourcePath = ValidateExistingFilePath(filePath!, nameof(filePath));
        var format = BookFormatConversionPolicy.Normalize(Path.GetExtension(sourcePath));
        var sourceHash = await Hashing.Sha256Async(sourcePath, cancellationToken);
        var sourceIdentity = new Guid(Convert.FromHexString(sourceHash[..32]));
        var sourceFile = new BookFile
        {
            Id = sourceIdentity,
            BookId = Guid.Empty,
            Format = format,
            RelativePath = sourcePath,
            Size = new FileInfo(sourcePath).Length,
            Sha256 = sourceHash
        };
        if (channel == "usb" && KindleTransferPolicy.GetCandidates([sourceFile]).Count == 0)
            throw new McpException($"The file format '{format}' is not supported by USB Kindle transfer.");
        ValidateSendFormatAndSize(sourcePath, channel);
        return new ResolvedSendSource(
            null,
            Path.GetFileNameWithoutExtension(sourcePath),
            sourcePath,
            format,
            sourceFile);
    }

    private static void ValidateSendFormatAndSize(string path, string channel)
    {
        var fileInfo = new FileInfo(path);
        if (channel == "email")
        {
            if (!KindleEmailSelectionPolicy.IsSupportedFormat(fileInfo.Extension))
                throw new McpException("The email channel only supports EPUB or PDF files.");
            if (!KindleEmailSelectionPolicy.IsWithinAttachmentLimit(fileInfo.Length))
                throw new McpException("The email attachment exceeds the existing 50 MB Send to Kindle limit.");
        }
        else if (channel == "web")
        {
            try
            {
                KindleWebFilePolicy.Inspect(path);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                throw new McpException(exception.Message);
            }
        }
    }

    private async Task<KindleEmailSettings> RequireEmailSettingsAsync(CancellationToken cancellationToken)
    {
        if (_emailSettingsStore is null)
            throw new McpException("Kindle email settings are not registered in this MCP server.");
        var settings = await _emailSettingsStore.LoadAsync(cancellationToken);
        var validationError = settings.Validate();
        if (validationError is not null)
            throw new McpException(validationError);
        return settings;
    }

    private KindleEmailSender RequireEmailSender() =>
        _emailSender ?? throw new McpException("Kindle email sending is not registered in this MCP server.");

    private IBookFormatConverter RequireFormatConverter() =>
        _formatConverter ?? throw new McpException("Book format conversion is not registered in this MCP server.");

    private async Task<ResolvedConversionSource> ResolveConversionSourceAsync(
        string? filePath,
        string? bookId,
        string targetFormat,
        CancellationToken cancellationToken)
    {
        var hasFilePath = !string.IsNullOrWhiteSpace(filePath);
        var hasBookId = !string.IsNullOrWhiteSpace(bookId);
        if (hasFilePath == hasBookId)
            throw new McpException("Provide exactly one of filePath or bookId.");

        if (hasBookId)
        {
            var id = ParseBookId(bookId!);
            var book = await GetBookOrThrowAsync(id, cancellationToken);
            var sourceFile = BookFormatConversionPolicy
                .GetSourceCandidates(book.Files, targetFormat)
                .FirstOrDefault()
                ?? throw new McpException(
                    $"Book '{book.Title}' has no supported source format for {targetFormat.ToUpperInvariant()} conversion.");
            var sourcePath = ValidateExistingFilePath(
                _library.GetAbsoluteFilePath(sourceFile),
                "bookId");
            return new ResolvedConversionSource(book, sourcePath, sourceFile.Format);
        }

        var externalPath = ValidateExistingFilePath(filePath!, nameof(filePath));
        return new ResolvedConversionSource(
            null,
            externalPath,
            BookFormatConversionPolicy.Normalize(Path.GetExtension(externalPath)));
    }

    private static string NormalizeChannel(string? channel)
    {
        var normalized = (channel ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not ("usb" or "email" or "web"))
            throw new McpException("channel must be one of usb, email, or web.");
        return normalized;
    }

    private static int ValidateRecentLimit(int value)
    {
        if (value is < 1 or > 100)
            throw new McpException($"limit must be between 1 and 100; received {value}.");
        return value;
    }

    private static string ValidatePath(string? path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new McpException($"{parameterName} must not be blank.");
        var normalized = path.Trim();
        if (normalized.Length > MaximumPathLength)
            throw new McpException($"{parameterName} is too long.");
        try
        {
            return Path.GetFullPath(normalized);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new McpException($"{parameterName} is not a valid path: {exception.Message}");
        }
    }

    private static string ValidateExistingFilePath(string? path, string parameterName)
    {
        var normalized = ValidatePath(path, parameterName);
        if (!File.Exists(normalized))
            throw new McpException($"File '{normalized}' does not exist.");
        return normalized;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
