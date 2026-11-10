namespace PcTidy.Features.Search.Index;

/// <summary>Source of "something changed under this root" hints. Tests inject their own.</summary>
public interface IChangeHintSource
{
    /// <summary>Starts watching <paramref name="root"/>; <paramref name="onHint"/> gets (root, changed path). Dispose the result to stop.</summary>
    IDisposable Watch(string root, Action<string, string> onHint, Action<string> onOverflow);
}

/// <summary>
/// FileSystemWatcher-based hints (read-only: it only listens). An event names the changed item; after a quiet period
/// the index lists the folder that holds it again (from disk, never from the event's details). Errors (buffer
/// overflow) mark the root for a full re-scan. A root that can't be watched (network share, access denied) still gets the periodic re-scan.
/// </summary>
public sealed class FileSystemWatcherHints : IChangeHintSource
{
    public IDisposable Watch(string root, Action<string, string> onHint, Action<string> onOverflow)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            FileSystemEventHandler changed = (_, e) => onHint(root, e.FullPath);
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Changed += changed;
            watcher.Renamed += (_, e) => onHint(root, e.FullPath);
            watcher.Error += (_, _) => onOverflow(root);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            return NoWatch.Instance;
        }
    }

    private sealed class NoWatch : IDisposable
    {
        public static readonly NoWatch Instance = new();
        public void Dispose() { }
    }
}
