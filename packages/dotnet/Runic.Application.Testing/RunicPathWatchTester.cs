using Microsoft.Extensions.Time.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing;

/// <summary>
/// Drives the <see cref="RunicPathWatcher"/>s started with its <see cref="Options"/>: their debounce and polling run
/// on a <see cref="FakeTimeProvider"/>, a test awaits the next reported change, and a test can report a change of a
/// path it cannot modify, such as <c>/run/current-system</c>.
/// </summary>
/// <remarks>
/// Pass <see cref="Options"/> to the ViewModel that starts the watcher, typically through its constructor or
/// dependency injection. One tester can serve several watchers; their changes are returned in the order their
/// callbacks ran. Dispose the tester at the end of the test to stop the watchers its options started.
/// </remarks>
public sealed class RunicPathWatchTester : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private readonly object _gate = new();
    private readonly List<RunicPathWatcher> _watchers = [];
    private readonly Queue<RunicPathChange> _changes = new();
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    /// <summary>Creates a tester with a new <see cref="FakeTimeProvider"/>.</summary>
    /// <param name="debounce">The watchers' debounce; null keeps the default of <see cref="RunicPathWatcherOptions"/>.</param>
    public RunicPathWatchTester(TimeSpan? debounce = null)
    {
        Time = new FakeTimeProvider();
        var defaults = new RunicPathWatcherOptions();
        Options = new RunicPathWatcherOptions
        {
            Debounce = debounce ?? defaults.Debounce,
            TimeProvider = Time,
            Observer = new Observer(this),
        };
    }

    /// <summary>Gets the clock of the watchers' debounce and polling. Time passes only when the test advances it.</summary>
    public FakeTimeProvider Time { get; }

    /// <summary>Gets the options to start watchers with.</summary>
    public RunicPathWatcherOptions Options { get; }

    /// <summary>Gets the number of watchers started with <see cref="Options"/> and not yet disposed.</summary>
    public int ActiveWatchers
    {
        get
        {
            lock (_gate) return _watchers.Count;
        }
    }

    /// <summary>
    /// Returns the next change not returned yet, after its callback ran in the model context. While none is
    /// reported, advances <see cref="Time"/> by one debounce interval at a time, so file-system events that arrive
    /// are delivered.
    /// </summary>
    /// <param name="timeout">How long to wait in real time; null waits 10 seconds.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <exception cref="TimeoutException">No change was reported in time.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<RunicPathChange> NextChangeAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? DefaultTimeout;
        var step = Options.Debounce > TimeSpan.Zero ? Options.Debounce : TimeSpan.FromMilliseconds(1);
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task arrived;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_changes.TryDequeue(out var change)) return change;
                arrived = _arrived.Task;
            }
            if (TimeProvider.System.GetElapsedTime(started) > limit)
                throw new TimeoutException($"No path change was reported within {limit.TotalSeconds:0.#} s.");
            Time.Advance(step);
            await Task.WhenAny(arrived, Task.Delay(20, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until no change is reported for <paramref name="quiet"/> of real time while <see cref="Time"/> advances,
    /// and returns the changes reported meanwhile; use it to assert that nothing (more) is reported.
    /// </summary>
    /// <param name="quiet">The real time to wait; null waits 300 ms.</param>
    /// <param name="cancellationToken">Stops waiting; the changes reported meanwhile stay queued.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<IReadOnlyList<RunicPathChange>> DrainAsync(TimeSpan? quiet = null, CancellationToken cancellationToken = default)
    {
        var limit = quiet ?? TimeSpan.FromMilliseconds(300);
        var step = Options.Debounce > TimeSpan.Zero ? Options.Debounce : TimeSpan.FromMilliseconds(1);
        var started = TimeProvider.System.GetTimestamp();
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        while (TimeProvider.System.GetElapsedTime(started) < limit)
        {
            Time.Advance(step);
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var changes = _changes.ToArray();
            _changes.Clear();
            return changes;
        }
    }

    /// <summary>
    /// Reports a change of <paramref name="path"/> to every active watcher that watches it, as if the platform had
    /// reported an event, and returns <see cref="NextChangeAsync"/>: the triggered change, unless an earlier one was
    /// not returned yet. The targets need not have changed.
    /// </summary>
    /// <param name="path">The path a target was created with, compared as a full path.</param>
    /// <param name="timeout">How long to wait in real time; null waits 10 seconds.</param>
    /// <exception cref="InvalidOperationException">No active watcher has a target with this path.</exception>
    /// <param name="cancellationToken">Stops waiting for the change.</param>
    /// <exception cref="TimeoutException">The change was not reported in time.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<RunicPathChange> TriggerAsync(string path, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        RunicPathWatcher[] watchers;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            watchers = [.. _watchers];
        }
        var signalled = false;
        foreach (var watcher in watchers) signalled |= watcher.Signal(path);
        if (!signalled)
            throw new InvalidOperationException($"No active watcher started with these options watches {path}.");
        return NextChangeAsync(timeout, cancellationToken);
    }

    /// <summary>
    /// Stops the active watchers started with <see cref="Options"/>, drops the changes not returned yet and detaches
    /// from the options: a watcher started with them later runs on <see cref="Time"/> but is not tracked.
    /// </summary>
    /// <remarks>Disposing a watcher again, as its ViewModel does, has no effect.</remarks>
    public void Dispose()
    {
        RunicPathWatcher[] watchers;
        TaskCompletionSource arrived;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            watchers = [.. _watchers];
            _watchers.Clear();
            _changes.Clear();
            arrived = _arrived;
        }
        foreach (var watcher in watchers) watcher.Dispose();
        // Wakes a NextChangeAsync, which then throws ObjectDisposedException.
        arrived.TrySetResult();
    }

    private sealed class Observer(RunicPathWatchTester owner) : IRunicPathWatchObserver
    {
        public void Started(RunicPathWatcher watcher)
        {
            lock (owner._gate)
                if (!owner._disposed) owner._watchers.Add(watcher);
        }

        public void Stopped(RunicPathWatcher watcher)
        {
            lock (owner._gate) owner._watchers.Remove(watcher);
        }

        public void Delivered(RunicPathWatcher watcher, RunicPathChange change)
        {
            TaskCompletionSource arrived;
            lock (owner._gate)
            {
                if (owner._disposed) return;
                owner._changes.Enqueue(change);
                arrived = owner._arrived;
                owner._arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            arrived.TrySetResult();
        }
    }
}
