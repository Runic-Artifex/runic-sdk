using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Assets;

/// <summary>Coalesces local filesystem signals into one source-owned refresh loop; the source owns its lifetime.</summary>
internal sealed class DevelopmentAssetWatch : IDisposable
{
    private readonly DevelopmentDirectoryAssetSource _source;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly TimeSpan _debounceDelay;
    private readonly TimeSpan _retryDelay;
    private readonly int _maxRetryAttempts;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _refreshCancellation = new();
    private bool _disposed;
    private bool _disposeCompleted;
    private bool _refreshing;
    private bool _pending;
    private int _retryAttempts;
    private TimeSpan _nextRefreshDelay;

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

    internal void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _watcher.EnableRaisingEvents = true;
        }
    }

    public void Dispose() => Dispose(waitForRefresh: true);

    // waitForRefresh is false when a subscriber of this watch's own refresh disposes the source.
    internal void Dispose(bool waitForRefresh)
    {
        bool wait;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending = false;
            wait = _refreshing && waitForRefresh;
            _watcher.EnableRaisingEvents = false;
        }

        _refreshCancellation.Cancel();
        _watcher.Dispose();
        _timer.Dispose();

        if (wait)
        {
            lock (_gate)
            {
                while (_refreshing)
                {
                    Monitor.Wait(_gate);
                }
            }
        }

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
            if (_disposed || !_pending || _refreshing)
            {
                return;
            }

            _pending = false;
            _refreshing = true;
        }

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            _ = await _source.RefreshAsync(_refreshCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            // A frontend build can leave the directory half written, for example without its entry point for a moment.
            ScheduleRetry();
        }
        catch (OperationCanceledException) when (_refreshCancellation.IsCancellationRequested)
        {
            // Disposal cancels an in-flight directory scan before waiting for this refresh.
        }
        catch (ObjectDisposedException)
        {
            // The source was disposed during the refresh.
        }
        finally
        {
            lock (_gate)
            {
                _refreshing = false;
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
            }
        }

        if (disposeCancellation)
        {
            _refreshCancellation.Dispose();
        }
    }
}
