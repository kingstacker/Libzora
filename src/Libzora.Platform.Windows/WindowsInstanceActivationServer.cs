using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Libzora.Platform.Windows;

/// <summary>
/// Per-session named-pipe endpoint that lets a second Libzora launch hand
/// files to the already-running instance. The primary instance starts the
/// server before Avalonia initializes; the callback feeds
/// <c>ExternalFileOpenQueue</c>, so files that arrive before the main window
/// exists are imported when startup completes instead of being lost.
/// </summary>
public sealed class WindowsInstanceActivationServer : IDisposable
{
    private const int ProtocolVersion = 1;
    private const int MaximumFileCount = 512;
    private const int MaximumPathBytes = 32 * 1024;
    private const int ServerReadTimeoutSeconds = 10;
    private static readonly TimeSpan ClientConnectTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ClientReplyTimeout = TimeSpan.FromSeconds(3);
    private static readonly byte[] Acknowledged = [1];

    private readonly string _pipeName;
    private readonly Action<IReadOnlyList<string>> _onActivation;
    private readonly CancellationTokenSource _cancellation = new();
    private NamedPipeServerStream? _firstPipe;
    private Task? _acceptLoop;
    private bool _disposed;

    private WindowsInstanceActivationServer(
        string pipeName,
        Action<IReadOnlyList<string>> onActivation)
    {
        _pipeName = pipeName;
        _onActivation = onActivation;
    }

    internal static string PipeNameForSession(int sessionId) =>
        $"Libzora.InstanceActivation.{sessionId}.v1";

    public static string DefaultPipeName =>
        PipeNameForSession(Process.GetCurrentProcess().SessionId);

    public static WindowsInstanceActivationServer? TryStart(
        Action<IReadOnlyList<string>> onActivation) =>
        TryStart(DefaultPipeName, onActivation);

    internal static WindowsInstanceActivationServer? TryStart(
        string pipeName,
        Action<IReadOnlyList<string>> onActivation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(onActivation);
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var server = new WindowsInstanceActivationServer(pipeName, onActivation);
            // NamedPipeServerStream reserves the pipe name as soon as the
            // first instance is constructed, so a second launch racing startup
            // can connect immediately and wait for the accept loop.
            server._firstPipe = server.CreatePipe();
            server._acceptLoop = Task.Run(server.AcceptLoopAsync);
            return server;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends an activation request to the primary instance. The running app
    /// acknowledges only after the paths have been handed to the import queue,
    /// so the caller can fall back to a plain window activation when this
    /// returns <see langword="false"/>.
    /// </summary>
    public static bool TryRequestActivation(IReadOnlyList<string>? files = null) =>
        TryRequestActivation(DefaultPipeName, files);

    internal static bool TryRequestActivation(
        string pipeName,
        IReadOnlyList<string>? files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (!OperatingSystem.IsWindows()) return false;

        byte[] payload;
        try
        {
            payload = BuildPayload(files);
        }
        catch (ArgumentException)
        {
            return false;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.None);
            pipe.Connect(unchecked((int)ClientConnectTimeout.TotalMilliseconds));
            pipe.ReadTimeout = unchecked((int)ClientReplyTimeout.TotalMilliseconds);
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();
            return pipe.ReadByte() == Acknowledged[0];
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or TimeoutException
            or PlatformNotSupportedException
            or ObjectDisposedException)
        {
            return false;
        }
    }

    private static byte[] BuildPayload(IReadOnlyList<string>? files)
    {
        var entries = new List<string>();
        if (files is not null)
        {
            foreach (var file in files)
            {
                if (string.IsNullOrWhiteSpace(file)) continue;
                if (Encoding.UTF8.GetByteCount(file) > MaximumPathBytes) continue;
                entries.Add(file);
                if (entries.Count == MaximumFileCount) break;
            }
        }

        using var buffer = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header[..4], ProtocolVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], entries.Count);
        buffer.Write(header);

        Span<byte> lengthBytes = stackalloc byte[4];
        foreach (var entry in entries)
        {
            var pathBytes = Encoding.UTF8.GetBytes(entry);
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, pathBytes.Length);
            buffer.Write(lengthBytes);
            buffer.Write(pathBytes);
        }
        return buffer.ToArray();
    }

    private NamedPipeServerStream CreatePipe() => new(
        _pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task AcceptLoopAsync()
    {
        NamedPipeServerStream? pipe = _firstPipe;
        _firstPipe = null;
        while (!_cancellation.IsCancellationRequested)
        {
            var retryDelay = false;
            try
            {
                pipe ??= CreatePipe();
                await pipe.WaitForConnectionAsync(_cancellation.Token).ConfigureAwait(false);
                await HandleRequestAsync(pipe).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // A per-request read timeout (a stalled client) must not stop
                // the listener; drop that client and accept the next request.
                retryDelay = true;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A client that disconnects mid-request must not stop the
                // listener. Back off briefly to avoid a tight retry loop.
                retryDelay = true;
            }
            catch (Exception)
            {
                // Activation is best effort and must never take the app down.
                retryDelay = true;
            }
            finally
            {
                if (pipe is not null)
                {
                    try { pipe.Dispose(); } catch { }
                    pipe = null;
                }
            }

            if (!retryDelay) continue;
            try
            {
                await Task.Delay(100, _cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HandleRequestAsync(NamedPipeServerStream pipe)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(ServerReadTimeoutSeconds));

        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
        var version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (version != ProtocolVersion || count < 0 || count > MaximumFileCount) return;

        var files = new List<string>(count);
        var lengthBytes = new byte[4];
        for (var index = 0; index < count; index++)
        {
            await pipe.ReadExactlyAsync(lengthBytes, timeout.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length < 0 || length > MaximumPathBytes) return;

            var pathBytes = new byte[length];
            await pipe.ReadExactlyAsync(pathBytes, timeout.Token).ConfigureAwait(false);
            var path = Encoding.UTF8.GetString(pathBytes);
            if (!string.IsNullOrWhiteSpace(path)) files.Add(path);
        }

        try
        {
            _onActivation(files);
        }
        catch
        {
            // The queue callback must not terminate the listener.
        }

        await pipe.WriteAsync(Acknowledged, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cancellation.Cancel(); } catch { }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _firstPipe?.Dispose(); } catch { }
        _cancellation.Dispose();
    }
}
