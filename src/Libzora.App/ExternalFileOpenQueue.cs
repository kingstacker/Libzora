namespace Libzora;

/// <summary>
/// Holds files that arrived before the main window was ready. macOS can
/// deliver Finder document activations while Avalonia is still starting, and
/// the second-instance socket can theoretically beat window construction too.
/// </summary>
public static class ExternalFileOpenQueue
{
    private static readonly object Gate = new();
    private static readonly List<string> Pending = [];
    private static Action<IReadOnlyList<string>>? _handler;

    /// <summary>
    /// Registers the main window as the live import target. Files enqueued
    /// after registration are handed over immediately instead of waiting for
    /// the next startup drain. Files queued before registration stay pending
    /// for <see cref="Drain"/> so no path is delivered twice.
    /// </summary>
    public static void SetHandler(Action<IReadOnlyList<string>>? handler)
    {
        lock (Gate) _handler = handler;
    }

    public static void Enqueue(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[]? delivered = null;
        Action<IReadOnlyList<string>>? handler;
        lock (Gate)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!File.Exists(path)) continue;
                if (!Pending.Contains(path, StringComparer.OrdinalIgnoreCase))
                    Pending.Add(path);
            }

            handler = _handler;
            if (handler is not null && Pending.Count > 0)
            {
                delivered = Pending.ToArray();
                Pending.Clear();
            }
        }

        if (delivered is null) return;
        try
        {
            handler!(delivered);
        }
        catch
        {
            // If the live handler cannot accept the request (for example while
            // the window is shutting down), keep the paths queued rather than
            // dropping the user's file.
            lock (Gate)
            {
                foreach (var path in delivered)
                    if (!Pending.Contains(path, StringComparer.OrdinalIgnoreCase))
                        Pending.Add(path);
            }
        }
    }

    public static string[] Drain()
    {
        lock (Gate)
        {
            var paths = Pending.ToArray();
            Pending.Clear();
            return paths;
        }
    }
}
