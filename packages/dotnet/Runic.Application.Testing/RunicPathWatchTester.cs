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
/// callbacks ran.
/// </remarks>
public sealed class RunicPathWatchTester
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private readonly object _gate = new();
    private readonly List<RunicPathWatcher> _watchers = [];
    private readonly Queue<RunicPathChange> _changes = new();
    private TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
    /// <exception cref="TimeoutException">No change was reported in time.</exception>
    public async Task<RunicPathChange> NextChangeAsync(TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var step = Options.Debounce > TimeSpan.Zero ? Options.Debounce : TimeSpan.FromMilliseconds(1);
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            Task arrived;
            lock (_gate)
            {
                if (_changes.TryDequeue(out var change)) return change;
                arrived = _arrived.Task;
            }
            if (TimeProvider.System.GetElapsedTime(started) > limit)
                throw new TimeoutException($"No path change was reported within {limit.TotalSeconds:0.#} s.");
            Time.Advance(step);
            await Task.WhenAny(arrived, Task.Delay(20)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until no change is reported for <paramref name="quiet"/> of real time while <see cref="Time"/> advances,
    /// and returns the changes reported meanwhile; use it to assert that nothing (more) is reported.
    /// </summary>
    /// <param name="quiet">The real time to wait; null waits 300 ms.</param>
    public async Task<IReadOnlyList<RunicPathChange>> DrainAsync(TimeSpan? quiet = null)
    {
        var limit = quiet ?? TimeSpan.FromMilliseconds(300);
        var step = Options.Debounce > TimeSpan.Zero ? Options.Debounce : TimeSpan.FromMilliseconds(1);
        var started = TimeProvider.System.GetTimestamp();
        while (TimeProvider.System.GetElapsedTime(started) < limit)
        {
            Time.Advance(step);
            await Task.Delay(20).ConfigureAwait(false);
        }
        lock (_gate)
        {
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
    /// <exception cref="TimeoutException">The change was not reported in time.</exception>
    public Task<RunicPathChange> TriggerAsync(string path, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        RunicPathWatcher[] watchers;
        lock (_gate) watchers = [.. _watchers];
        var signalled = false;
        foreach (var watcher in watchers) signalled |= watcher.Signal(path);
        if (!signalled)
            throw new InvalidOperationException($"No active watcher started with these options watches {path}.");
        return NextChangeAsync(timeout);
    }

    private sealed class Observer(RunicPathWatchTester owner) : IRunicPathWatchObserver
    {
        public void Started(RunicPathWatcher watcher)
        {
            lock (owner._gate) owner._watchers.Add(watcher);
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
                owner._changes.Enqueue(change);
                arrived = owner._arrived;
                owner._arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            arrived.TrySetResult();
        }
    }
}
