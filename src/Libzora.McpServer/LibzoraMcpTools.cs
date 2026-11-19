using System.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Libzora.McpServer;

/// <summary>
/// MCP tools backed by Libzora's existing SQLite library, reader data,
/// conversion, email, and mounted-device services.
/// </summary>

[McpServerToolType]
public sealed partial class LibzoraMcpTools
{
    private const int DefaultLibraryLimit = 100;
    private const int DefaultSearchLimit = 50;
    private const int DefaultRecentLimit = 20;
    private const int MaximumResultLimit = 200;
    private const int MaximumQueryLength = 200;
    private const int MaximumDeviceIdLength = 200;
    private const int MaximumPathLength = 4_096;

    private readonly IBookLibraryService _library;
    private readonly ReaderDataService _readerData;
    private readonly IKindleDeviceService _devices;
    private readonly IKindleDeviceService? _ejectDevices;
    private readonly IBookFormatConverter? _formatConverter;
    private readonly KindleEmailSettingsStore? _emailSettingsStore;
    private readonly KindleEmailSender? _emailSender;

    public LibzoraMcpTools(
        IBookLibraryService library,
        ReaderDataService readerData,
        IKindleDeviceService devices)
        : this(library, readerData, devices, null, null, null, null)
    {
    }

    public LibzoraMcpTools(
        IBookLibraryService library,
        ReaderDataService readerData,
        IKindleDeviceService devices,
        IBookFormatConverter? formatConverter,
        KindleEmailSettingsStore? emailSettingsStore,
        KindleEmailSender? emailSender,
        IKindleDeviceService? ejectDevices = null)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _readerData = readerData ?? throw new ArgumentNullException(nameof(readerData));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _ejectDevices = ejectDevices;
        _formatConverter = formatConverter;
        _emailSettingsStore = emailSettingsStore;
        _emailSender = emailSender;
    }
}
