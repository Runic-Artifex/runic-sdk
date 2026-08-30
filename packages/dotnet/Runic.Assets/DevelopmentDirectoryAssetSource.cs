using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Assets;

/// <summary>
/// Exposes a refreshable Linux development directory pinned to one no-follow root handle.
/// Every refresh publishes an immutable manifest from that same root identity.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class DevelopmentDirectoryAssetSource : IAssetSnapshotSource, IAssetSourceChangeNotifier, IDisposable
{
    private readonly string _root;
    private readonly string _entryPoint;
    private readonly LinuxAssetRoot _rootHandle;
    private readonly object _refreshGate = new();
    private readonly object _notificationGate = new();
    private AssetManifest _manifest;
    private EventHandler<AssetSourceChangedEventArgs>? _changed;
    private AssetSourceChangedEventArgs? _pendingChange;
    private DevelopmentAssetWatch? _watch;
    private bool _isDispatching;
    private int _activeCallbacks;
    private int _callbackThreadId;
    private volatile bool _disposed;
    private bool _disposeCompleted;

    /// <summary>Scans a local development directory and creates its first manifest snapshot.</summary>
    public DevelopmentDirectoryAssetSource(string rootDirectory, string entryPointRelativePath)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "DevelopmentDirectoryAssetSource is supported on Linux only because it requires no-follow directory handles.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.GetFullPath(rootDirectory);
        _entryPoint = AssetPath.Normalize(entryPointRelativePath);
        _rootHandle = new LinuxAssetRoot(_root);
        try
        {
            _manifest = Scan(CancellationToken.None);
        }
        catch
        {
            _rootHandle.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public AssetManifest Manifest => Volatile.Read(ref _manifest);

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
    /// Starts the source's sole debounced filesystem watcher. File changes are coalesced into one
    /// refresh after the configured quiet period and successful replacement snapshots publish
    /// through <see cref="Changed"/>. Dispose the returned lease or cancel the token to stop it.
    /// </summary>
    public IAssetWatch StartWatching(
        AssetWatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new AssetWatchOptions();
        ValidateWatchOptions(options);

        DevelopmentAssetWatch watch;
        lock (_refreshGate)
        {
            ThrowIfDisposed();
            if (_watch is not null)
            {
                throw new InvalidOperationException("A development asset source has exactly one active filesystem watcher.");
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
            watch.Start(cancellationToken);
            return watch;
        }
        catch
        {
            watch.Dispose();
            throw;
        }
    }

    /// <summary>Atomically replaces the manifest with a fresh deterministic directory scan.</summary>
    public AssetManifest Refresh(CancellationToken cancellationToken = default)
    {
        AssetSourceChangedEventArgs? change = null;
        AssetManifest result;
        bool dispatch;
        lock (_refreshGate)
        {
            ThrowIfDisposed();
            AssetManifest replacement = Scan(cancellationToken);
            AssetManifest previous = Manifest;
            if (HasSameAssets(previous, replacement))
            {
                return previous;
            }

            Interlocked.Exchange(ref _manifest, replacement);
            change = new AssetSourceChangedEventArgs(previous, replacement);
            result = replacement;
            dispatch = QueueChange(change);
        }

        if (dispatch)
        {
            DispatchPendingChanges();
        }

        return result;
    }

    /// <inheritdoc />
    public ValueTask ValidateAsync(CancellationToken cancellationToken = default)
    {
        lock (_refreshGate)
        {
            ThrowIfDisposed();
            AssetManifest snapshot = Manifest;
            foreach (AssetDescriptor descriptor in snapshot.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using FileStream stream = OpenFile(descriptor.RelativePath);
                AssetHashing.Verify(descriptor, stream);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        ValueTask<AssetReadSnapshot> snapshot = OpenSnapshotAsync(relativePath, cancellationToken);
        if (snapshot.IsCompletedSuccessfully)
        {
            return ValueTask.FromResult(snapshot.Result.Content);
        }

        return OpenStreamAsync(snapshot);
    }

    /// <inheritdoc />
    public ValueTask<AssetReadSnapshot> OpenSnapshotAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        lock (_refreshGate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            string path = AssetPath.Normalize(relativePath);
            AssetManifest manifest = Manifest;
            if (!manifest.TryGetAsset(path, out AssetDescriptor? descriptor))
            {
                throw new FileNotFoundException("The requested asset is not declared by the current manifest.", path);
            }

            return ValueTask.FromResult(new AssetReadSnapshot(
                descriptor!,
                OpenSnapshot(descriptor!, cancellationToken)));
        }
    }

    private static async ValueTask<Stream> OpenStreamAsync(ValueTask<AssetReadSnapshot> snapshot) =>
        (await snapshot.ConfigureAwait(false)).Content;

    private AssetManifest Scan(CancellationToken cancellationToken)
    {
        var descriptors = new List<AssetDescriptor>();
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
                relativePath = AssetPath.Normalize(relativePath);
                if (_rootHandle.IsDirectory(relativePath))
                {
                    pending.Push(relativePath);
                    continue;
                }

                using FileStream stream = OpenFile(relativePath);
                descriptors.Add(AssetHashing.Describe(
                    relativePath,
                    stream,
                    mediaType: null,
                    StringComparer.Ordinal.Equals(relativePath, _entryPoint),
                    AssetCacheMode.NoStore));
            }
        }

        return new AssetManifest(descriptors);
    }

    private FileStream OpenFile(string relativePath)
    {
        return _rootHandle.OpenRead(AssetPath.Normalize(relativePath));
    }

    private Stream OpenSnapshot(AssetDescriptor descriptor, CancellationToken cancellationToken)
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
            using FileStream input = OpenFile(descriptor.RelativePath);
            CopyAndVerifySnapshot(input, snapshot, descriptor, cancellationToken);
            snapshot.Position = 0;
            Stream result = new ReadOnlySnapshotStream(snapshot);
            snapshot = null;
            return result;
        }
        finally
        {
            snapshot?.Dispose();
        }
    }

    private static void CopyAndVerifySnapshot(
        Stream input,
        Stream output,
        AssetDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read > descriptor.Length - total)
            {
                throw new InvalidDataException($"Asset '{descriptor.RelativePath}' changed while its snapshot was read.");
            }

            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
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
    /// Stops future refreshes and notification admission. Queued notifications are discarded and
    /// every external or repeated disposal call waits until callbacks admitted before disposal have
    /// finished. Disposal called by the active callback returns without waiting for itself; a later
    /// external disposal still waits for that callback. Subscriber exceptions are isolated and never
    /// affect a published refresh. Notifications are delivered in refresh order by one dispatcher;
    /// handlers may unsubscribe or refresh reentrantly, and changes queued by reentrancy run afterward.
    /// </summary>
    public void Dispose()
    {
        DevelopmentAssetWatch? watch;
        lock (_refreshGate)
        {
            _disposed = true;
            watch = _watch;
            _watch = null;
        }

        watch?.Dispose();

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

    internal void DetachWatch(DevelopmentAssetWatch watch)
    {
        lock (_refreshGate)
        {
            if (ReferenceEquals(_watch, watch))
            {
                _watch = null;
            }
        }
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
