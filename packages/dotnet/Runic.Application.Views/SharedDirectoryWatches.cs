namespace Runic.Application.Views;

internal interface IDirectoryWatchSink
{
    // A path below the watched directory was created, deleted, written or renamed (old and new path).
    void OnPath(string fullPath);

    // The watch overflowed (stopped: false) or failed and was removed (stopped: true).
    void OnFailure(string directory, Exception exception, bool stopped);
}

// One FileSystemWatcher per directory for the whole process. On Linux every FileSystemWatcher holds its own inotify
// instance, and fs.inotify.max_user_instances (128 by default) is shared by all of the user's processes.
internal static class SharedDirectoryWatches
{
    private const int BufferSize = 64 * 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(WatchPaths.Comparer);

    // Starts or joins the watch of a directory. Throws when the watch cannot start.
    public static IDisposable Acquire(string directory, bool recursive, IDirectoryWatchSink sink)
    {
        var key = (recursive ? "r|" : "n|") + directory;
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(key, directory, recursive);
                entry.Start();
                Entries.Add(key, entry);
            }
            entry.Sinks = [.. entry.Sinks, sink];
            return new Lease(entry, sink);
        }
    }

    private sealed class Lease(Entry entry, IDirectoryWatchSink sink) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            FileSystemWatcher? stopped = null;
            lock (Gate)
            {
                entry.Sinks = [.. entry.Sinks.Where(item => !ReferenceEquals(item, sink))];
                if (entry.Sinks.Length == 0) stopped = entry.Stop();
            }
            // Outside the gate: disposing can wait for the platform's event thread, which may be delivering an
            // event to a sink that waits for the gate.
            stopped?.Dispose();
        }
    }

    private sealed class Entry(string key, string directory, bool recursive)
    {
        private FileSystemWatcher? _watcher;

        public IDirectoryWatchSink[] Sinks { get; set; } = [];

        public void Start()
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = recursive,
                InternalBufferSize = BufferSize,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                    | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            try
            {
                watcher.EnableRaisingEvents = true;
            }
            catch
            {
                watcher.Dispose();
                throw;
            }
            _watcher = watcher;
        }

        // Called under Gate. Returns the watcher for the caller to dispose after leaving the gate.
        public FileSystemWatcher? Stop()
        {
            if (Entries.TryGetValue(key, out var current) && ReferenceEquals(current, this)) Entries.Remove(key);
            var watcher = _watcher;
            _watcher = null;
            return watcher;
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            foreach (var sink in Sinks) sink.OnPath(e.FullPath);
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            foreach (var sink in Sinks)
            {
                sink.OnPath(e.OldFullPath);
                sink.OnPath(e.FullPath);
            }
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            var exception = e.GetException();
            var stopped = exception is not InternalBufferOverflowException;
            IDirectoryWatchSink[] sinks;
            FileSystemWatcher? failed = null;
            lock (Gate)
            {
                sinks = Sinks;
                // Any other failure can leave the watcher stopped; drop it so the next lease starts a new one.
                if (stopped) failed = Stop();
            }
            // Not on this thread: on macOS this is the FSEvents thread that disposal waits for.
            if (failed is not null) ThreadPool.QueueUserWorkItem(static watcher => watcher.Dispose(), failed, preferLocal: false);
            foreach (var sink in sinks) sink.OnFailure(directory, exception, stopped);
        }
    }
}
