using System.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Libzora.McpServer;

/// <summary>
/// Private send/conversion records.
/// </summary>
public sealed partial class LibzoraMcpTools
{
    private sealed record ResolvedSendSource(
        Guid? BookId,
        string Title,
        string Path,
        string Format,
        BookFile BookFile);

    private sealed record ResolvedConversionSource(
        Book? Book,
        string Path,
        string Format);

    private sealed record ProgressCandidate(
        BookFile File,
        double ProgressPercent,
        DateTimeOffset UpdatedAt,
        string Source);
}
