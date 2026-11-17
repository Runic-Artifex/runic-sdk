namespace Runic.Application.Views;

internal interface IDirectoryWatchSink
{
    // A path below the watched directory was created, deleted, written or renamed (old and new path).
    void OnPath(string fullPath);

    // The watch with this key overflowed (stopped: false) or failed and was removed (stopped: true). Replaced: it stopped
    // because its directory was deleted or replaced, which is no failure of the watch; a new one can start at once.
    void OnFailure(string key, string directory, Exception exception, bool stopped, bool replaced);
}

internal interface IDirectoryWatchLease : IDisposable
{
    string Key { get; }

    string Directory { get; }

    // Restarts the shared watch when its directory was replaced since the watch started, and returns whether it did or
    // whether another lease or Acquire restarted it since this lease last looked: either way this lease's sink missed
    // the replacement's events (#1008). Throws like Acquire when the new watch cannot start.
    bool Revalidate();
}

// One FileSystemWatcher per directory for the whole process. On Linux every FileSystemWatcher holds its own inotify
// instance, and fs.inotify.max_user_instances (128 by default) is shared by all of the user's processes.
internal static class SharedDirectoryWatches
{
    private const int BufferSize = 64 * 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(WatchPaths.Comparer);

    public static string Key(string directory, bool recursive) => (recursive ? "r|" : "n|") + directory;

    // Starts or joins the watch of a directory. Throws when the watch cannot start.
    public static IDirectoryWatchLease Acquire(string directory, bool recursive, IDirectoryWatchSink sink)
    {
        var key = Key(directory, recursive);
        var identity = DirectoryIdentity.Read(directory);
        FileSystemWatcher? replaced = null;
        Lease lease;
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(key, directory, recursive);
                entry.Start(identity);
                Entries.Add(key, entry);
            }
            // Don't join a watch whose directory was replaced: it would report nothing.
            else if (entry.IsReplacedBy(identity))
                replaced = entry.Restart(identity);
            entry.Sinks = [.. entry.Sinks, sink];
            lease = new Lease(entry, sink);
        }
        replaced?.Dispose();
        return lease;
    }

    private sealed class Lease(Entry entry, IDirectoryWatchSink sink) : IDirectoryWatchLease
    {
        private int _disposed;
        // Under Gate: the entry's generation this lease last saw, created under Gate after any restart.
        private int _generation = entry.Generation;

        public string Key => entry.Key;

        public string Directory => entry.Directory;

        public bool Revalidate()
        {
            var identity = DirectoryIdentity.Read(entry.Directory);
            FileSystemWatcher? replaced;
            lock (Gate)
            {
                if (Volatile.Read(ref _disposed) != 0 || !entry.IsCurrent) return false;
                if (entry.IsReplacedBy(identity)) replaced = entry.Restart(identity);
                else if (entry.Generation == _generation) return false;
                else replaced = null;
                _generation = entry.Generation;
            }
            replaced?.Dispose();
            return true;
        }

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
        private string? _identity;

        public string Key { get; } = key;

        public string Directory { get; } = directory;

        public IDirectoryWatchSink[] Sinks { get; set; } = [];

        // Called under Gate: how often the watch was restarted for a replaced directory.
        public int Generation { get; private set; }

        // Called under Gate.
        public bool IsCurrent => Entries.TryGetValue(Key, out var current) && ReferenceEquals(current, this);

        // Called under Gate. An unknown identity never counts as a replacement.
        public bool IsReplacedBy(string? identity) =>
            identity is not null && _identity is not null && !string.Equals(identity, _identity, StringComparison.Ordinal);

        // Called under Gate.
        public void Start(string? identity)
        {
            _watcher = Create();
            _identity = identity;
        }

        // Called under Gate. Starts a new watcher before giving up the old one, which the caller disposes after leaving
        // the gate. When the new one cannot start, the entry is removed so that the next lease starts afresh.
        public FileSystemWatcher? Restart(string? identity)
        {
            FileSystemWatcher created;
            try
            {
                created = Create();
            }
            catch
            {
                var stale = Stop();
                stale?.Dispose();
                throw;
            }
            var old = _watcher;
            _watcher = created;
            _identity = identity;
            Generation++;
            return old;
        }

        // Called under Gate. Returns the watcher for the caller to dispose after leaving the gate.
        public FileSystemWatcher? Stop()
        {
            if (IsCurrent) Entries.Remove(Key);
            var watcher = _watcher;
            _watcher = null;
            return watcher;
        }

        private FileSystemWatcher Create()
        {
            var watcher = new FileSystemWatcher(Directory)
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
            return watcher;
        }

        private void OnChanged(object? sender, FileSystemEventArgs e)
        {
            foreach (var sink in Sinks) sink.OnPath(e.FullPath);
        }

        private void OnRenamed(object? sender, RenamedEventArgs e)
        {
            foreach (var sink in Sinks)
            {
                sink.OnPath(e.OldFullPath);
                sink.OnPath(e.FullPath);
            }
        }

        private void OnError(object? sender, ErrorEventArgs e)
        {
            var exception = e.GetException();
            var stopped = exception is not InternalBufferOverflowException;
            // Windows stops a watch whose directory is deleted (ERROR_ACCESS_DENIED from ReadDirectoryChangesW), and the
            // error can arrive before a lease restarts the watch for the directory's replacement (#1008). A missing or
            // delete-pending directory has no identity and does not exist.
            var identity = stopped ? DirectoryIdentity.Read(Directory) : null;
            var missing = stopped && identity is null && !System.IO.Directory.Exists(Directory);
            IDirectoryWatchSink[] sinks;
            FileSystemWatcher? failed = null;
            bool replaced;
            lock (Gate)
            {
                // A watcher replaced by Restart reports for a directory that is gone.
                if (!ReferenceEquals(sender, _watcher)) return;
                sinks = Sinks;
                replaced = stopped && (missing || IsReplacedBy(identity));
                // Any other failure can leave the watcher stopped; drop it so the next lease starts a new one.
                if (stopped) failed = Stop();
            }
            // Not on this thread: on macOS this is the FSEvents thread that disposal waits for.
            if (failed is not null) ThreadPool.QueueUserWorkItem(static watcher => watcher.Dispose(), failed, preferLocal: false);
            foreach (var sink in sinks) sink.OnFailure(Key, Directory, exception, stopped, replaced);
        }
    }
}
