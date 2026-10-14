using Runic.Navigation;

namespace Runic.Application.Views;

/// <summary>
/// Watches files, symbolic links, directories and Git checkouts, and reports their changes in a model context.
/// </summary>
/// <remarks>
/// <para>
/// Events from <see cref="FileSystemWatcher"/> are debounced: a change is reported once no event arrived for
/// <see cref="RunicPathWatcherOptions.Debounce"/>, or ten debounce intervals after the first event of a burst at
/// the latest. The watcher then reads each affected target, and reports the targets whose state differs from the
/// last reading, so noise such as a lock file next to a Git ref is not reported.
/// </para>
/// <para>
/// Both callbacks run in turns of the model context, so they can set ViewModel state directly. The watcher
/// stops when it is disposed and when the context closes; no callback starts after <see cref="Dispose"/> returns
/// in a turn of the context.
/// </para>
/// <para>
/// Where events cannot be trusted, the watcher reads its targets every
/// <see cref="RunicPathWatcherOptions.PollingInterval"/> instead: on network and FUSE file systems, while a
/// directory a target needs is missing, after a watch failed to start, and with
/// <see cref="RunicPathWatcherOptions.UsePolling"/>. Otherwise it still reads them every 30 seconds, or every
/// polling interval when that is longer, to recover from events the platform lost.
/// </para>
/// </remarks>
public sealed class RunicPathWatcher : IDisposable
{
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly object _evaluating = new();
    private readonly IRunicModelContext _context;
    private readonly Action<RunicPathChange> _changed;
    private readonly Action<RunicPathWatchError>? _failed;
    private readonly RunicPathWatcherOptions _options;
    private readonly TargetState[] _states;
    private readonly Dictionary<string, Subscription> _subscriptions = new(WatchPaths.Comparer);
    // Directories that cannot be watched: remote ones for good, failed ones until the next poll retries them.
    private readonly HashSet<string> _remote = new(WatchPaths.Comparer);
    private readonly Dictionary<string, RunicPathWatchErrorKind> _failedWatches = new(WatchPaths.Comparer);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly ITimer _debounce;
    private readonly ITimer _poll;
    private CancellationTokenRegistration _closed;
    private TimeSpan _pollPeriod = Timeout.InfiniteTimeSpan;
    private DateTimeOffset? _firstPending;
    private volatile bool _disposed;

    private RunicPathWatcher(
        IRunicModelContext context,
        RunicWatchTarget[] targets,
        Action<RunicPathChange> changed,
        Action<RunicPathWatchError>? failed,
        RunicPathWatcherOptions options)
    {
        _context = context;
        _changed = changed;
        _failed = failed;
        _options = options;
        _states = [.. targets.Select(target => new TargetState(target, target.Probe()))];
        _debounce = options.TimeProvider.CreateTimer(static state => ((RunicPathWatcher)state!).Evaluate(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _poll = options.TimeProvider.CreateTimer(static state => ((RunicPathWatcher)state!).OnPoll(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Starts watching <paramref name="targets"/>.</summary>
    /// <remarks>
    /// The targets' current state is read before this method returns, and later changes are reported. Read the
    /// initial state after starting the watcher, so that a change in between is not lost.
    /// </remarks>
    /// <param name="context">The model context in which <paramref name="changed"/> and <paramref name="failed"/> run.</param>
    /// <param name="targets">The targets; a change reports the instances that changed.</param>
    /// <param name="changed">Receives each change in a turn of <paramref name="context"/>.</param>
    /// <param name="failed">
    /// Receives problems in a turn of <paramref name="context"/>: a missing directory, lost events or a watch that
    /// could not start. The watcher keeps working, by polling where needed. Without a callback, problems are written to
    /// <see cref="System.Diagnostics.Trace"/> as event 1015.
    /// </param>
    /// <param name="options">Debounce, polling and time; null uses the defaults.</param>
    /// <returns>The watcher; dispose it with the ViewModel that owns it.</returns>
    /// <exception cref="ArgumentException">There are no targets, or an option is out of range.</exception>
    public static RunicPathWatcher Start(
        IRunicModelContext context,
        IEnumerable<RunicWatchTarget> targets,
        Action<RunicPathChange> changed,
        Action<RunicPathWatchError>? failed = null,
        RunicPathWatcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(changed);
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.TimeProvider, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Debounce, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.PollingInterval, TimeSpan.Zero, nameof(options));
        var list = targets.ToArray();
        if (list.Length == 0) throw new ArgumentException("Watch at least one target.", nameof(targets));
        if (list.Any(target => target is null)) throw new ArgumentException("A target is null.", nameof(targets));

        var watcher = new RunicPathWatcher(context, list, changed, failed, options);
        watcher.Begin();
        return watcher;
    }

    private void Begin()
    {
        List<RunicPathWatchError> errors = [];
        List<IDisposable> released = [];
        lock (_gate) Arm(errors, released);
        Release(released);
        Report(errors);
        _options.Observer?.Started(this);
        // Registered last: a context that already closed disposes the watcher at once.
        if (_context is IRunicModelContextLifetime lifetime)
            _closed = lifetime.Closed.UnsafeRegister(static state => ((RunicPathWatcher)state!).Dispose(), this);
    }

    /// <summary>Stops watching. Changes that were not reported yet are dropped.</summary>
    public void Dispose()
    {
        List<IDisposable> released;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            released = [.. _subscriptions.Values.Select(subscription => subscription.Lease)];
            _subscriptions.Clear();
        }
        Release(released);
        _debounce.Dispose();
        _poll.Dispose();
        _closed.Unregister();
        _options.Observer?.Stopped(this);
    }

    // Marks targets changed as if an event named them, for RunicPathWatchTester. Returns false when none matches.
    internal bool Signal(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_disposed) return false;
            var found = false;
            foreach (var state in _states)
            {
                if (!string.Equals(state.Target.FullPath, full, WatchPaths.Comparison)) continue;
                state.Dirty = state.Forced = true;
                found = true;
            }
            if (found) ScheduleDebounce();
            return found;
        }
    }

    private void OnPath(string fullPath)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var matched = false;
            foreach (var state in _states)
            {
                if (!state.Probe.Specs.Any(spec => RunicWatchTarget.Matches(spec, fullPath))) continue;
                state.Dirty = true;
                // A write keeps a file's size and can keep its time stamp; trust the event.
                if (state.Target.Kind == WatchTargetKind.File) state.Forced = true;
                matched = true;
            }
            if (matched) ScheduleDebounce();
        }
    }

    private void OnFailure(string directory, Exception exception, bool stopped)
    {
        List<RunicPathWatchError> errors = [];
        List<IDisposable> released = [];
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var state in _states)
            {
                if (!state.Probe.Specs.Any(spec => WatchPaths.Comparer.Equals(spec.Directory, directory))) continue;
                state.Dirty = true;
                if (state.Target.Kind == WatchTargetKind.File) state.Forced = true;
            }
            if (stopped)
            {
                foreach (var (key, subscription) in _subscriptions.Where(item => WatchPaths.Comparer.Equals(item.Value.Directory, directory)).ToArray())
                {
                    released.Add(subscription.Lease);
                    _subscriptions.Remove(key);
                    _failedWatches[key] = RunicPathWatchErrorKind.Failed;
                    AddOnce(errors, $"failed|{key}", new(RunicPathWatchErrorKind.Failed, directory, exception));
                }
            }
            else
                errors.Add(new(RunicPathWatchErrorKind.Overflow, directory, exception));
            ScheduleDebounce();
        }
        Release(released);
        Report(errors);
    }

    private void ScheduleDebounce()
    {
        var now = _options.TimeProvider.GetUtcNow();
        _firstPending ??= now;
        var latest = _firstPending.Value + _options.Debounce * 10 - now;
        var due = _options.Debounce < latest ? _options.Debounce : latest;
        _debounce.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    private void OnPoll()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var state in _states) state.Dirty = true;
            // Retry watches that failed; a freed inotify watch or a restarted service may let them start now.
            _failedWatches.Clear();
        }
        Evaluate();
    }

    private void Evaluate()
    {
        List<RunicPathWatchError> errors = [];
        List<IDisposable> released = [];
        RunicPathChange? change = null;
        lock (_evaluating)
        {
            TargetState[] dirty;
            bool[] forced;
            lock (_gate)
            {
                if (_disposed) return;
                _firstPending = null;
                dirty = [.. _states.Where(state => state.Dirty)];
                forced = [.. dirty.Select(state => state.Forced)];
                foreach (var state in dirty) state.Dirty = state.Forced = false;
            }

            // Read outside the gate, so file-system latency doesn't hold up event threads.
            var probes = dirty.Select(state => state.Target.Probe()).ToArray();

            lock (_gate)
            {
                if (_disposed) return;
                List<RunicWatchTarget> changed = [];
                for (var i = 0; i < dirty.Length; i++)
                {
                    if (forced[i] || !string.Equals(dirty[i].Probe.Signature, probes[i].Signature, StringComparison.Ordinal))
                        changed.Add(dirty[i].Target);
                    dirty[i].Probe = probes[i];
                }
                Arm(errors, released);
                if (changed.Count > 0) change = new(changed);
            }
        }
        Release(released);
        Report(errors);
        if (change is not null) Deliver(change);
    }

    // Starts and stops directory watches to match the targets' current probes. Called under the gate; the caller
    // releases the leases of stopped watches after leaving it.
    private void Arm(List<RunicPathWatchError> errors, List<IDisposable> released)
    {
        var wanted = new Dictionary<string, (string Directory, bool Recursive)>(WatchPaths.Comparer);
        foreach (var state in _states)
        {
            var missingKey = $"missing|{state.Target.FullPath}";
            if (state.Probe.MissingDirectory is { } missing)
                AddOnce(errors, missingKey, new(RunicPathWatchErrorKind.Missing, missing, null));
            else
                _reported.Remove(missingKey);
            if (_options.UsePolling) continue;
            foreach (var spec in state.Probe.Specs)
            {
                if (!Directory.Exists(spec.Directory)) continue;
                wanted.TryAdd(Key(spec.Directory, spec.Recursive), (spec.Directory, spec.Recursive));
            }
        }

        foreach (var key in _subscriptions.Keys.Where(key => !wanted.ContainsKey(key)).ToArray())
        {
            released.Add(_subscriptions[key].Lease);
            _subscriptions.Remove(key);
        }
        foreach (var (key, (directory, recursive)) in wanted)
        {
            if (_subscriptions.ContainsKey(key) || _remote.Contains(key) || _failedWatches.ContainsKey(key)) continue;
            if (IsRemote(directory))
            {
                _remote.Add(key);
                continue;
            }
            try
            {
                _subscriptions.Add(key, new(directory, SharedDirectoryWatches.Acquire(directory, recursive, new Sink(this))));
                _reported.Remove($"failed|{key}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
            {
                var kind = IsWatchLimit(exception) ? RunicPathWatchErrorKind.WatchLimit : RunicPathWatchErrorKind.Failed;
                _failedWatches[key] = kind;
                AddOnce(errors, $"failed|{key}", new(kind, directory, exception));
            }
        }

        // Poll quickly while a target lacks a watch; otherwise only as a backstop.
        var polled = _options.UsePolling || _states.Any(state =>
            state.Probe.MissingDirectory is not null
            || state.Probe.Specs.Any(spec => !_subscriptions.ContainsKey(Key(spec.Directory, spec.Recursive))));
        var period = polled ? _options.PollingInterval
            : _options.PollingInterval > Backstop ? _options.PollingInterval : Backstop;
        if (period != _pollPeriod)
        {
            _pollPeriod = period;
            _poll.Change(period, period);
        }
    }

    // Outside the gate: disposing a FileSystemWatcher can wait for its event thread, which may be waiting for the gate.
    private static void Release(List<IDisposable> released)
    {
        foreach (var lease in released) lease.Dispose();
    }

    private void AddOnce(List<RunicPathWatchError> errors, string key, RunicPathWatchError error)
    {
        if (_reported.Add(key)) errors.Add(error);
    }

    private void Deliver(RunicPathChange change)
    {
        var posted = _context.TryPost(() =>
        {
            if (_disposed) return;
            try { _changed(change); }
            finally { _options.Observer?.Delivered(this, change); }
        });
        if (!posted) Dispose();
    }

    private void Report(List<RunicPathWatchError> errors)
    {
        foreach (var error in errors)
        {
            if (_failed is null)
            {
                ViewsLog.PathWatchProblem(TraceFallbackLogger.Instance, error.Exception, error.ToString());
                continue;
            }
            _context.TryPost(() =>
            {
                if (!_disposed) _failed(error);
            });
        }
    }

    private static string Key(string directory, bool recursive) => (recursive ? "r|" : "n|") + directory;

    private static bool IsRemote(string directory)
    {
        if (OperatingSystem.IsWindows() && directory.StartsWith(@"\\", StringComparison.Ordinal)
            && !directory.StartsWith(@"\\?\", StringComparison.Ordinal)) return true;
        try
        {
            var drive = new DriveInfo(directory);
            if (drive.DriveType == DriveType.Network) return true;
            // inotify sees only local changes on FUSE file systems such as sshfs.
            return OperatingSystem.IsLinux() && drive.DriveFormat.Equals("fuse", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    // .NET reports inotify's ENOSPC (fs.inotify.max_user_watches) and EMFILE (max_user_instances) as an IOException
    // whose HResult is the errno and whose message names the limit.
    private static bool IsWatchLimit(Exception exception) =>
        OperatingSystem.IsLinux() && exception is IOException io
            && (io.HResult is 28 or 24 || io.Message.Contains("inotify", StringComparison.OrdinalIgnoreCase));

    private sealed class TargetState(RunicWatchTarget target, WatchProbe probe)
    {
        public RunicWatchTarget Target { get; } = target;
        public WatchProbe Probe { get; set; } = probe;
        public bool Dirty { get; set; }
        public bool Forced { get; set; }
    }

    private sealed record Subscription(string Directory, IDisposable Lease);

    // The shared watch holds the sink until the watcher disposes its lease.
    private sealed class Sink(RunicPathWatcher owner) : IDirectoryWatchSink
    {
        public void OnPath(string fullPath) => owner.OnPath(fullPath);

        public void OnFailure(string directory, Exception exception, bool stopped) => owner.OnFailure(directory, exception, stopped);
    }
}

/// <summary>Options of a <see cref="RunicPathWatcher"/>.</summary>
public sealed class RunicPathWatcherOptions
{
    /// <summary>Gets the quiet period after an event before targets are read and a change is reported. Default 250 ms.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Gets how often targets are read where events cannot be trusted. Default 2 seconds.</summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets whether to read targets every <see cref="PollingInterval"/> instead of watching for events.</summary>
    public bool UsePolling { get; init; }

    /// <summary>Gets the time provider for debouncing and polling. Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal IRunicPathWatchObserver? Observer { get; init; }
}

/// <summary>Targets of a <see cref="RunicPathWatcher"/> that changed together.</summary>
public sealed class RunicPathChange
{
    internal RunicPathChange(IReadOnlyList<RunicWatchTarget> targets) => Targets = targets;

    /// <summary>Gets the targets that changed, in the order they were passed to <see cref="RunicPathWatcher.Start"/>.</summary>
    public IReadOnlyList<RunicWatchTarget> Targets { get; }

    /// <summary>Gets whether <paramref name="target"/> is among the changed targets.</summary>
    /// <param name="target">A target passed to <see cref="RunicPathWatcher.Start"/>.</param>
    public bool Contains(RunicWatchTarget target) => Targets.Contains(target);
}

/// <summary>A problem a <see cref="RunicPathWatcher"/> works around.</summary>
public sealed class RunicPathWatchError
{
    internal RunicPathWatchError(RunicPathWatchErrorKind kind, string path, Exception? exception)
    {
        Kind = kind;
        Path = path;
        Exception = exception;
    }

    /// <summary>Gets what went wrong.</summary>
    public RunicPathWatchErrorKind Kind { get; }

    /// <summary>Gets the directory concerned.</summary>
    public string Path { get; }

    /// <summary>Gets the platform's exception, if there is one.</summary>
    public Exception? Exception { get; }

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        RunicPathWatchErrorKind.Missing => $"{Path} does not exist; polling until it does.",
        RunicPathWatchErrorKind.Overflow => $"Events for {Path} were lost; its targets were read again.",
        RunicPathWatchErrorKind.WatchLimit => $"The watch limit was reached for {Path}; polling it instead. Raise fs.inotify.max_user_watches or fs.inotify.max_user_instances. {Exception?.Message}",
        _ => $"{Path} cannot be watched; polling it instead. {Exception?.Message}",
    };
}

/// <summary>The kind of a <see cref="RunicPathWatchError"/>.</summary>
public enum RunicPathWatchErrorKind
{
    /// <summary>A directory a target needs does not exist. The watcher polls until it does, then reports the change.</summary>
    Missing,

    /// <summary>The platform dropped events, for example after an event-buffer overflow. The watcher read the targets again.</summary>
    Overflow,

    /// <summary>A watch could not start because a per-user limit, such as <c>fs.inotify.max_user_watches</c>, was reached. The watcher polls instead.</summary>
    WatchLimit,

    /// <summary>A watch could not start or stopped. The watcher polls instead and retries the watch on each poll.</summary>
    Failed,
}

internal interface IRunicPathWatchObserver
{
    void Started(RunicPathWatcher watcher);

    void Stopped(RunicPathWatcher watcher);

    // Called in the model turn after the change callback returned.
    void Delivered(RunicPathWatcher watcher, RunicPathChange change);
}
