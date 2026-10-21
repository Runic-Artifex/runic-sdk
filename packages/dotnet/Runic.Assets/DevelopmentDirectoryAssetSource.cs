using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Assets;

/// <summary>
/// Exposes a refreshable local development directory, such as a frontend's build output. Every refresh publishes an
/// immutable manifest, every file is served with <see cref="AssetCacheMode.NoStore"/>, and reads are verified
/// against the manifest digest.
/// </summary>
/// <remarks>
/// <para>
/// No path component below the root may be a symbolic link or reparse point. On Linux and macOS the source pins the
/// root with one no-follow directory handle and opens every file relative to it, so replacing the root or an
/// ancestor directory cannot redirect a published source. Windows checks each component for a reparse point before
/// it opens a file, but does not pin the root: a directory moved into the root's place is read from then on.
/// </para>
/// <para>
/// Call <see cref="StartWatching"/> once to refresh after file changes, and dispose the source to stop.
/// </para>
/// </remarks>
public sealed class DevelopmentDirectoryAssetSource : IAssetSource, IAssetSourceChangeNotifier, IDisposable
{
    private readonly string _entryPoint;
    private readonly AssetDirectoryRoot _rootHandle;
    // One refresh at a time. No subscriber runs while it is held: notifications are dispatched after it is released.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _notificationGate = new();
    private AssetManifest _manifest;
    private IReadOnlyList<string> _skippedEntries = Array.Empty<string>();
    private EventHandler<AssetSourceChangedEventArgs>? _changed;
    private AssetSourceChangedEventArgs? _pendingChange;
    private DevelopmentAssetWatch? _watch;
    private bool _isDispatching;
    private int _activeCallbacks;
    private int _callbackThreadId;
    private volatile bool _disposed;
    private bool _disposeCompleted;

    /// <summary>Scans a local development directory and creates its first manifest snapshot.</summary>
    /// <param name="rootDirectory">The directory, absolute or relative to the current directory.</param>
    /// <param name="entryPointRelativePath">The entry document, relative to <paramref name="rootDirectory"/>, such as <c>index.html</c>.</param>
    /// <exception cref="InvalidDataException">The directory contains a symbolic link or reparse point.</exception>
    /// <exception cref="ArgumentException">The directory does not contain the entry point.</exception>
    /// <exception cref="PlatformNotSupportedException">The operating system is not Linux, Windows or macOS.</exception>
    public DevelopmentDirectoryAssetSource(string rootDirectory, string entryPointRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string root = Path.GetFullPath(rootDirectory);
        _entryPoint = AssetPath.Normalize(entryPointRelativePath);
        _rootHandle = AssetDirectoryRoot.Open(root);
        try
        {
            // The synchronous scan completes before it returns.
            ValueTask<(AssetManifest Manifest, IReadOnlyList<string> Skipped)> scan =
                ScanAsync(synchronous: true, CancellationToken.None);
            (_manifest, _skippedEntries) = scan.IsCompleted ? scan.Result : scan.AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            _rootHandle.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public AssetManifest Manifest => Volatile.Read(ref _manifest);

    /// <summary>
    /// Gets the directory entries the latest scan skipped because their names are not valid asset
    /// paths, such as names containing <c>:</c>, <c>?</c>, <c>#</c>, a backslash, or a percent-encoded octet.
    /// </summary>
    public IReadOnlyList<string> SkippedEntries => Volatile.Read(ref _skippedEntries);

    /// <inheritdoc />
    public event EventHandler<AssetSourceChangedEventArgs>? Changed
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_notificationGate)
            {
                ThrowIfDisposed();
                _changed += value;
            }
        }
        remove
        {
            lock (_notificationGate)
            {
                _changed -= value;
            }
        }
    }

    /// <summary>
    /// Starts the source's filesystem watcher. File changes are coalesced into one refresh after the configured quiet
    /// period, and each replacement manifest is published through <see cref="Changed"/>. Subscribe before calling it.
    /// The watch runs until the source is disposed.
    /// </summary>
    /// <param name="options">The debounce and retry bounds; <see langword="null"/> uses the defaults.</param>
    /// <exception cref="InvalidOperationException">The source is already watching.</exception>
    /// <exception cref="ObjectDisposedException">The source is disposed.</exception>
    public void StartWatching(AssetWatchOptions? options = null)
    {
        options ??= new AssetWatchOptions();
        ValidateWatchOptions(options);

        DevelopmentAssetWatch watch;
        lock (_stateGate)
        {
            ThrowIfDisposed();
            if (_watch is not null)
            {
                throw new InvalidOperationException("A development asset source has exactly one filesystem watcher.");
            }

            watch = new DevelopmentAssetWatch(
                this,
                _rootHandle.WatchPath,
                options.DebounceDelay,
                options.RetryDelay,
                options.MaxRetryAttempts);
            _watch = watch;
        }

        try
        {
            watch.Start();
        }
        catch
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_watch, watch))
                {
                    _watch = null;
                }
            }

            watch.Dispose(waitForRefresh: true);
            throw;
        }
    }

    /// <summary>
    /// Scans the directory again and atomically replaces the manifest when its assets changed, then raises
    /// <see cref="Changed"/>. Refreshes run one at a time.
    /// </summary>
    /// <returns>The current manifest: the replacement, or the previous one when nothing changed.</returns>
    /// <exception cref="ArgumentException">The directory no longer contains the entry point.</exception>
    /// <exception cref="InvalidDataException">The directory contains a symbolic link or reparse point.</exception>
    /// <exception cref="ObjectDisposedException">The source is disposed.</exception>
    public async ValueTask<AssetManifest> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AssetManifest result;
        bool dispatch;
        try
        {
            ThrowIfDisposed();
            (AssetManifest replacement, IReadOnlyList<string> skipped) =
                await ScanAsync(synchronous: false, cancellationToken).ConfigureAwait(false);
            ThrowIfDisposed();
            Volatile.Write(ref _skippedEntries, skipped);
            AssetManifest previous = Manifest;
            if (HasSameAssets(previous, replacement))
            {
                return previous;
            }

            Interlocked.Exchange(ref _manifest, replacement);
            result = replacement;
            dispatch = QueueChange(new AssetSourceChangedEventArgs(previous, replacement));
        }
        finally
        {
            _refreshGate.Release();
        }

        if (dispatch)
        {
            DispatchPendingChanges();
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask ValidateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        AssetManifest snapshot = Manifest;
        foreach (AssetDescriptor descriptor in snapshot.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream stream = OpenFile(descriptor.RelativePath);
            await using (stream.ConfigureAwait(false))
            {
                await AssetHashing.VerifyAsync(descriptor, stream, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<AssetReadSnapshot> OpenAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        string path = AssetPath.Normalize(relativePath);
        if (!Manifest.TryGetAsset(path, out AssetDescriptor? descriptor))
        {
            throw new FileNotFoundException("The requested asset is not declared by the current manifest.", path);
        }

        // The root keeps its handle open while the file opens; copying and hashing run outside any gate, so
        // concurrent requests do not serialize.
        FileStream input = OpenFile(descriptor.RelativePath);
        await using (input.ConfigureAwait(false))
        {
            Stream content = await OpenSnapshotAsync(input, descriptor, cancellationToken).ConfigureAwait(false);
            return new AssetReadSnapshot(descriptor, content);
        }
    }

    private async ValueTask<(AssetManifest Manifest, IReadOnlyList<string> Skipped)> ScanAsync(
        bool synchronous,
        CancellationToken cancellationToken)
    {
        var descriptors = new List<AssetDescriptor>();
        var skipped = new List<string>();
        var pending = new Stack<string>();
        pending.Push("");
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            foreach (string name in _rootHandle.EnumerateEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = directory.Length == 0 ? name : directory + "/" + name;
                if (!TryNormalizeEntry(relativePath, name, out string normalized))
                {
                    // A development tool can leave odd names behind; one must not break the whole scan.
                    skipped.Add(relativePath);
                    continue;
                }

                relativePath = normalized;
                if (_rootHandle.IsDirectory(relativePath))
                {
                    pending.Push(relativePath);
                    continue;
                }

                bool isEntryPoint = StringComparer.Ordinal.Equals(relativePath, _entryPoint);
                FileStream stream = OpenFile(relativePath);
                await using (stream.ConfigureAwait(false))
                {
                    descriptors.Add(synchronous
                        ? AssetHashing.Describe(relativePath, stream, mediaType: null, isEntryPoint, AssetCacheMode.NoStore)
                        : await AssetHashing.DescribeAsync(
                            relativePath, stream, mediaType: null, isEntryPoint, AssetCacheMode.NoStore, cancellationToken)
                            .ConfigureAwait(false));
                }
            }
        }

        return (new AssetManifest(descriptors), skipped.ToArray());
    }

    private static bool TryNormalizeEntry(string relativePath, string name, out string normalized)
    {
        normalized = string.Empty;
        if (name.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            normalized = AssetPath.Normalize(relativePath);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private FileStream OpenFile(string relativePath)
    {
        return _rootHandle.OpenRead(AssetPath.Normalize(relativePath));
    }

    private static async ValueTask<Stream> OpenSnapshotAsync(
        FileStream input,
        AssetDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        string temporaryPath = Path.Combine(
            Path.GetTempPath(),
            "runic-assets-" + Guid.NewGuid().ToString("N") + ".snapshot");
        FileStream? snapshot = null;
        try
        {
            snapshot = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 81_920,
                FileOptions.DeleteOnClose | FileOptions.SequentialScan);
            await CopyAndVerifySnapshotAsync(input, snapshot, descriptor, cancellationToken).ConfigureAwait(false);
            snapshot.Position = 0;
            Stream result = new ReadOnlySnapshotStream(snapshot);
            snapshot = null;
            return result;
        }
        finally
        {
            if (snapshot is not null)
            {
                await snapshot.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask CopyAndVerifySnapshotAsync(
        Stream input,
        Stream output,
        AssetDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (read > descriptor.Length - total)
            {
                throw new InvalidDataException($"Asset '{descriptor.RelativePath}' changed while its snapshot was read.");
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }

        if (total != descriptor.Length
            || !StringComparer.Ordinal.Equals(
                Convert.ToHexStringLower(hash.GetHashAndReset()),
                descriptor.Sha256))
        {
            throw new InvalidDataException($"Asset '{descriptor.RelativePath}' does not match its published manifest digest.");
        }
    }

    private static bool HasSameAssets(AssetManifest left, AssetManifest right)
    {
        if (left.Assets.Count != right.Assets.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Assets.Count; index++)
        {
            if (left.Assets[index] != right.Assets[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Stops the watch, future refreshes and notification admission; it is the only way to stop watching. A refresh
    /// in progress finishes first. Queued notifications are discarded and every external or repeated disposal call
    /// waits until callbacks admitted before disposal have finished. Disposal called by the active callback returns
    /// without waiting for itself; a later external disposal still waits for that callback. Subscriber exceptions are
    /// isolated and never affect a published refresh. Notifications are delivered in refresh order by one
    /// dispatcher; handlers may unsubscribe or refresh reentrantly, and changes queued by reentrancy run afterward.
    /// </summary>
    public void Dispose()
    {
        DevelopmentAssetWatch? watch;
        lock (_stateGate)
        {
            _disposed = true;
            watch = _watch;
            _watch = null;
        }

        bool fromCallback;
        lock (_notificationGate)
        {
            fromCallback = _activeCallbacks != 0 && _callbackThreadId == Environment.CurrentManagedThreadId;
        }

        // A callback of the watch's own refresh cannot wait for that refresh to end.
        watch?.Dispose(waitForRefresh: !fromCallback);

        // No subscriber runs under the refresh gate, so a refresh in progress always finishes.
        _refreshGate.Wait();
        _refreshGate.Release();
        _rootHandle.Dispose();

        lock (_notificationGate)
        {
            _pendingChange = null;
            _changed = null;
            if (_activeCallbacks != 0
                && _callbackThreadId == Environment.CurrentManagedThreadId)
            {
                return;
            }

            while (!_disposeCompleted)
            {
                if (_activeCallbacks == 0)
                {
                    _disposeCompleted = true;
                    Monitor.PulseAll(_notificationGate);
                    break;
                }

                Monitor.Wait(_notificationGate);
            }
        }
    }

    private bool QueueChange(AssetSourceChangedEventArgs change)
    {
        lock (_notificationGate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_pendingChange is null)
            {
                _pendingChange = change;
            }
            else
            {
                _pendingChange = new AssetSourceChangedEventArgs(_pendingChange.Previous, change.Current);
            }

            if (_isDispatching)
            {
                return false;
            }

            _isDispatching = true;
            return true;
        }
    }

    private void DispatchPendingChanges()
    {
        while (true)
        {
            EventHandler<AssetSourceChangedEventArgs>? subscribers;
            AssetSourceChangedEventArgs next;
            lock (_notificationGate)
            {
                if (_disposed || _pendingChange is null)
                {
                    _pendingChange = null;
                    _isDispatching = false;
                    return;
                }

                next = _pendingChange;
                _pendingChange = null;
                subscribers = _changed;
            }

            if (subscribers is null)
            {
                continue;
            }

            foreach (EventHandler<AssetSourceChangedEventArgs> subscriber in subscribers.GetInvocationList())
            {
                if (!TryBeginCallback())
                {
                    return;
                }

                try
                {
                    subscriber(this, next);
                }
                catch
                {
                    // Subscriber failures cannot invalidate an already-published manifest.
                }
                finally
                {
                    EndCallback();
                }
            }
        }
    }

    private bool TryBeginCallback()
    {
        lock (_notificationGate)
        {
            if (_disposed)
            {
                _isDispatching = false;
                return false;
            }

            _activeCallbacks++;
            _callbackThreadId = Environment.CurrentManagedThreadId;
            return true;
        }
    }

    private void EndCallback()
    {
        lock (_notificationGate)
        {
            _activeCallbacks--;
            if (_activeCallbacks == 0)
            {
                _callbackThreadId = 0;
                if (_disposed)
                {
                    _disposeCompleted = true;
                }

                Monitor.PulseAll(_notificationGate);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void ValidateWatchOptions(AssetWatchOptions options)
    {
        if (options.DebounceDelay < TimeSpan.Zero
            || options.DebounceDelay > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "A development asset watch debounce delay must be between zero and five seconds.");
        }

        if (options.RetryDelay < TimeSpan.Zero
            || options.RetryDelay > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "A development asset watch retry delay must be between zero and five seconds.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxRetryAttempts);
        if (options.MaxRetryAttempts > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "A development asset watch allows at most ten retries per change burst.");
        }
    }

    private sealed class ReadOnlySnapshotStream : Stream
    {
        private readonly FileStream _inner;

        internal ReadOnlySnapshotStream(FileStream inner) => _inner = inner;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.FromException(new NotSupportedException());
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new NotSupportedException());
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
