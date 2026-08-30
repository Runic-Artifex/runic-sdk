using System;
using System.IO;
using System.Threading;

namespace Runic.Assets;

/// <summary>Coalesces local filesystem signals into one source-owned refresh loop.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class DevelopmentAssetWatch : IAssetWatch
{
    private readonly DevelopmentDirectoryAssetSource _source;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly TimeSpan _debounceDelay;
    private readonly TimeSpan _retryDelay;
    private readonly int _maxRetryAttempts;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _refreshCancellation = new();
    private CancellationTokenRegistration _cancellationRegistration;
    private bool _disposed;
    private bool _disposeCompleted;
    private bool _refreshing;
    private bool _pending;
    private int _retryAttempts;
    private TimeSpan _nextRefreshDelay;
    private int _callbackThreadId;

    internal DevelopmentAssetWatch(
        DevelopmentDirectoryAssetSource source,
        string root,
        TimeSpan debounceDelay,
        TimeSpan retryDelay,
        int maxRetryAttempts)
    {
        _source = source;
        _debounceDelay = debounceDelay;
        _retryDelay = retryDelay;
        _maxRetryAttempts = maxRetryAttempts;
        _nextRefreshDelay = debounceDelay;
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 8 * 1024,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime,
            Filter = "*",
        };
        _watcher.Changed += OnFileSystemSignal;
        _watcher.Created += OnFileSystemSignal;
        _watcher.Deleted += OnFileSystemSignal;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnWatcherError;
        _timer = new Timer(static state => ((DevelopmentAssetWatch)state!).RefreshAfterDebounce(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool IsWatching
    {
        get
        {
            lock (_gate)
            {
                return !_disposed;
            }
        }
    }

    internal void Start(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _watcher.EnableRaisingEvents = true;
        }

        _cancellationRegistration = cancellationToken.Register(static state =>
            ((DevelopmentAssetWatch)state!).Dispose(), this);
    }

    public void Dispose()
    {
        bool waitForRefresh;
        lock (_gate)
        {
            if (_disposed)
            {
                if (!_disposeCompleted
                    && _callbackThreadId != Environment.CurrentManagedThreadId)
                {
                    while (!_disposeCompleted)
                    {
                        Monitor.Wait(_gate);
                    }
                }

                return;
            }

            _disposed = true;
            _pending = false;
            waitForRefresh = _refreshing
                && _callbackThreadId != Environment.CurrentManagedThreadId;
            _watcher.EnableRaisingEvents = false;
        }

        _refreshCancellation.Cancel();
        _cancellationRegistration.Dispose();
        _watcher.Dispose();
        _timer.Dispose();

        if (waitForRefresh)
        {
            lock (_gate)
            {
                while (_refreshing)
                {
                    Monitor.Wait(_gate);
                }
            }
        }

        _source.DetachWatch(this);
        CompleteDisposeIfReady();
    }

    private void OnFileSystemSignal(object sender, FileSystemEventArgs eventArgs) => ScheduleRefresh();

    private void OnRenamed(object sender, RenamedEventArgs eventArgs) => ScheduleRefresh();

    private void OnWatcherError(object sender, ErrorEventArgs eventArgs) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = true;
            _retryAttempts = 0;
            _nextRefreshDelay = _debounceDelay;
            _timer.Change(_nextRefreshDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void RefreshAfterDebounce()
    {
        lock (_gate)
        {
            if (_disposed || !_pending)
            {
                return;
            }

            if (_refreshing)
            {
                return;
            }

            _pending = false;
            _refreshing = true;
            _callbackThreadId = Environment.CurrentManagedThreadId;
        }

        try
        {
            _ = _source.Refresh(_refreshCancellation.Token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            ScheduleRetry();
        }
        catch (OperationCanceledException) when (_refreshCancellation.IsCancellationRequested)
        {
            // Disposal cancels an in-flight directory scan before waiting for this callback.
        }
        finally
        {
            lock (_gate)
            {
                _refreshing = false;
                _callbackThreadId = 0;
                if (!_disposed && _pending)
                {
                    _timer.Change(_nextRefreshDelay, Timeout.InfiniteTimeSpan);
                }

                Monitor.PulseAll(_gate);
            }

            CompleteDisposeIfReady();
        }
    }

    private void ScheduleRetry()
    {
        lock (_gate)
        {
            if (_disposed || _retryAttempts >= _maxRetryAttempts)
            {
                return;
            }

            _retryAttempts++;
            _pending = true;
            _nextRefreshDelay = _retryDelay;
            _timer.Change(_nextRefreshDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void CompleteDisposeIfReady()
    {
        bool disposeCancellation = false;
        lock (_gate)
        {
            if (_disposed && !_refreshing && !_disposeCompleted)
            {
                _disposeCompleted = true;
                disposeCancellation = true;
                Monitor.PulseAll(_gate);
            }
        }

        if (disposeCancellation)
        {
            _refreshCancellation.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
