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
/// last reading, so noise such as a lock file next to a Git ref is not reported. A target that changes and changes
/// back within that window is therefore not reported, except a <see cref="RunicWatchTarget.File"/> or
/// <see cref="RunicWatchTarget.Files"/>, for which an event alone counts.
/// </para>
/// <para>
/// Both callbacks run in turns of the model context, so they can set ViewModel state directly. The watcher
/// stops when it is disposed and when the context closes; no callback starts after <see cref="Dispose"/> returns
/// in a turn of the context.
/// </para>
/// <para>
/// Where events cannot be trusted, the watcher reads its targets every
/// <see cref="RunicPathWatcherOptions.PollingInterval"/> instead: on network, FUSE and virtiofs file systems, while
/// a directory a target needs is missing, after a watch failed to start, and with
/// <see cref="RunicPathWatcherOptions.UsePolling"/>. Otherwise it still reads them every 30 seconds, or every
/// polling interval when that is longer, to recover from events the platform lost. It also watches the parent of
/// each watched directory, so that a directory deleted and created again, as by <c>rm -rf</c> and <c>git clone</c>,
/// is watched again at once.
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
    private readonly Dictionary<string, IDirectoryWatchLease> _subscriptions = new(WatchPaths.Comparer);
    // Directories that cannot be watched: remote ones for good, failed ones until the next poll retries them.
    private readonly HashSet<string> _remote = new(WatchPaths.Comparer);
    private readonly Dictionary<string, RunicPathWatchErrorKind> _failedWatches = new(WatchPaths.Comparer);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly Sink _sink;
    private readonly ITimer _debounce;
    private readonly ITimer _poll;
    private CancellationTokenRegistration _closed;
    private TimeSpan _pollPeriod = Timeout.InfiniteTimeSpan;
    private DateTimeOffset? _firstPending;
    private volatile bool _disposed;
    private int _requested;
    private int _events;
    private int _evaluations;
    private int _deliveries;

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
        _sink = new Sink(this);
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
    /// Receives problems in a turn of <paramref name="context"/>: a missing directory, a checkout that is not a Git
    /// repository, lost events or a watch that could not start. The watcher keeps working, by polling where needed.
    /// Without a callback, problems are written to <see cref="System.Diagnostics.Trace"/> as event 1015.
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
        // The targets were read in the constructor; arm their watches without reporting a change.
        Volatile.Write(ref _requested, 1);
        Evaluate();
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
            released = [.. _subscriptions.Values];
            _subscriptions.Clear();
        }
        Release(released);
        _debounce.Dispose();
        _poll.Dispose();
        _closed.Unregister();
        _options.Observer?.Stopped(this);
    }

    // The watcher's state, for test failure messages.
    internal string Describe()
    {
        lock (_gate)
        {
            var states = string.Join("; ", _states.Select(state =>
                $"{state.Target} dirty={state.Dirty} forced={state.Forced} missing={state.Probe.MissingDirectory} problem={state.Probe.NotRepository} signature={state.Probe.Signature.Replace('\n', '|')} specs=[{string.Join(", ", state.Probe.Specs)}]"));
            return $"disposed={_disposed} events={Volatile.Read(ref _events)} evaluations={_evaluations} deliveries={_deliveries} poll={_pollPeriod} pending={_firstPending} "
                + $"watches=[{string.Join(", ", _subscriptions.Keys)}] failed=[{string.Join(", ", _failedWatches.Keys)}] remote=[{string.Join(", ", _remote)}] targets: {states}";
        }
    }

    // Whether the watch reads its targets only at the 30-second backstop, that is, every target has a working watch.
    internal bool IsEventDriven
    {
        get
        {
            lock (_gate) return _pollPeriod >= Backstop && !_options.UsePolling;
        }
    }

    // Drops platform events, so that tests can exercise the polling backstop.
    internal bool IgnoreEvents { get; set; }

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
                state.Dirty = state.Forced = state.ByEvent = true;
                found = true;
            }
            if (found) ScheduleDebounce();
            return found;
        }
    }

    private void OnPath(string fullPath)
    {
        if (IgnoreEvents) return;
        lock (_gate)
        {
            if (_disposed) return;
            var matched = false;
            foreach (var state in _states)
            {
                foreach (var spec in state.Probe.Specs)
                {
                    if (RunicWatchTarget.Matches(spec, fullPath))
                    {
                        state.Dirty = state.ByEvent = true;
                        // A write keeps a file's size and can keep its time stamp, and a file renamed over an
                        // entry can have the same size and time as the one it replaced; trust the event.
                        if (state.Target.Kind is WatchTargetKind.File or WatchTargetKind.Files) state.Forced = true;
                        matched = true;
                    }
                    // The parent's watch names a watched directory: it may have been replaced. Evaluating checks it.
                    else if (WatchPaths.Comparer.Equals(spec.Directory, fullPath))
                    {
                        state.Dirty = state.ByEvent = true;
                        matched = true;
                    }
                }
            }
            if (!matched) return;
            Interlocked.Increment(ref _events);
            ScheduleDebounce();
        }
    }

    private void OnFailure(string key, string directory, Exception exception, bool stopped)
    {
        List<RunicPathWatchError> errors = [];
        IDisposable? released = null;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var state in _states)
            {
                if (!state.Probe.Specs.Any(spec => WatchPaths.Comparer.Equals(spec.Directory, directory))) continue;
                state.Dirty = true;
                if (state.Target.Kind is WatchTargetKind.File or WatchTargetKind.Files) state.Forced = true;
            }
            if (stopped)
            {
                if (_subscriptions.Remove(key, out var lease))
                {
                    released = lease;
                    _failedWatches[key] = RunicPathWatchErrorKind.Failed;
                    AddOnce(errors, $"failed|{key}", new(RunicPathWatchErrorKind.Failed, directory, exception));
                }
            }
            else
                errors.Add(new(RunicPathWatchErrorKind.Overflow, directory, exception));
            ScheduleDebounce();
        }
        // This runs on the platform's event thread, which on macOS serves every watcher; release elsewhere.
        if (released is not null) ThreadPool.QueueUserWorkItem(static lease => lease.Dispose(), released, preferLocal: false);
        Report(errors);
    }

    // Called under the gate.
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
        // One-shot: the next read is due a full period after this one, however long it took.
        lock (_gate)
        {
            if (!_disposed) _poll.Change(_pollPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    // Runs one evaluation at a time. A timer that fires while another evaluation runs leaves a request for it and
    // returns, so slow file systems never pile up blocked timer threads.
    private void Evaluate()
    {
        Volatile.Write(ref _requested, 1);
        while (Volatile.Read(ref _requested) == 1 && !_disposed)
        {
            if (!Monitor.TryEnter(_evaluating)) return;
            try
            {
                while (Interlocked.Exchange(ref _requested, 0) == 1 && !_disposed) EvaluateOnce();
            }
            finally
            {
                Monitor.Exit(_evaluating);
            }
        }
    }

    private void EvaluateOnce()
    {
        List<RunicPathWatchError> errors = [];
        List<IDisposable> released = [];
        RunicPathChange? change = null;

        TargetState[] dirty;
        bool[] forced;
        bool[] byEvent;
        lock (_gate)
        {
            if (_disposed) return;
            _evaluations++;
            _firstPending = null;
            dirty = [.. _states.Where(state => state.Dirty)];
            forced = [.. dirty.Select(state => state.Forced)];
            byEvent = [.. dirty.Select(state => state.ByEvent)];
            foreach (var state in dirty) state.Dirty = state.Forced = state.ByEvent = false;
        }

        // Read outside the gate, so file-system latency doesn't hold up event threads.
        var probes = dirty.Select(state => state.Target.Probe()).ToArray();

        WatchProbe[] current;
        Dictionary<string, IDirectoryWatchLease> subscribed;
        HashSet<string> skipped;
        lock (_gate)
        {
            if (_disposed) return;
            List<RunicWatchTarget> changed = [];
            List<RunicPathChangeEntry> entries = [];
            var reportedByEvent = false;
            for (var i = 0; i < dirty.Length; i++)
            {
                // Only an evaluation after Begin reports changes; the first one arms the watches.
                if (_evaluations > 1
                    && (forced[i] || !string.Equals(dirty[i].Probe.Signature, probes[i].Signature, StringComparison.Ordinal)))
                {
                    changed.Add(dirty[i].Target);
                    entries.AddRange(dirty[i].Target.ChangedEntries(dirty[i].Probe, probes[i]));
                    reportedByEvent |= byEvent[i];
                }
                dirty[i].Probe = probes[i];
            }
            if (changed.Count > 0) change = new(changed, entries) { ByEvent = reportedByEvent };
            current = [.. _states.Select(state => state.Probe)];
            subscribed = new(_subscriptions, WatchPaths.Comparer);
            skipped = new(_remote.Concat(_failedWatches.Keys), WatchPaths.Comparer);
        }

        var plan = PlanWatches(current, subscribed, skipped);

        lock (_gate)
        {
            if (_disposed)
            {
                released.AddRange(plan.Started.Values);
            }
            else
            {
                Apply(plan, current, errors, released);
            }
        }
        Release(released);
        Report(errors);
        if (change is not null) Deliver(change);
    }

    private sealed record WatchPlan(
        Dictionary<string, (string Directory, bool Recursive)> Wanted,
        Dictionary<string, IDirectoryWatchLease> Started,
        HashSet<string> Remote,
        Dictionary<string, (RunicPathWatchErrorKind Kind, string Directory, Exception Exception)> Failed,
        HashSet<string> Replaced);

    // Works out and starts the directory watches the targets need, outside the gate: it checks directories, file
    // systems and identities, and starts platform watches.
    private WatchPlan PlanWatches(WatchProbe[] probes, Dictionary<string, IDirectoryWatchLease> subscribed, HashSet<string> skipped)
    {
        var plan = new WatchPlan(new(WatchPaths.Comparer), new(WatchPaths.Comparer), new(WatchPaths.Comparer), new(WatchPaths.Comparer), new(WatchPaths.Comparer));
        if (_options.UsePolling) return plan;
        foreach (var spec in probes.SelectMany(probe => probe.Specs))
        {
            if (Directory.Exists(spec.Directory))
                plan.Wanted.TryAdd(SharedDirectoryWatches.Key(spec.Directory, spec.Recursive), (spec.Directory, spec.Recursive));
            // The parent shows the directory being replaced, which its own watch cannot, or a missing one returning.
            if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(spec.Directory)) is { } parent && Directory.Exists(parent))
                plan.Wanted.TryAdd(SharedDirectoryWatches.Key(parent, false), (parent, false));
        }

        foreach (var (key, (directory, recursive)) in plan.Wanted)
        {
            if (subscribed.TryGetValue(key, out var lease))
            {
                try
                {
                    if (lease.Revalidate()) plan.Replaced.Add(directory);
                }
                catch (Exception exception) when (IsWatchFailure(exception))
                {
                    plan.Failed[key] = (Classify(exception), directory, exception);
                }
                continue;
            }
            if (skipped.Contains(key)) continue;
            if (IsRemote(directory))
            {
                plan.Remote.Add(key);
                continue;
            }
            try
            {
                plan.Started[key] = SharedDirectoryWatches.Acquire(directory, recursive, _sink);
            }
            catch (Exception exception) when (IsWatchFailure(exception))
            {
                plan.Failed[key] = (Classify(exception), directory, exception);
            }
        }
        return plan;
    }

    // Commits a plan. Called under the gate.
    private void Apply(WatchPlan plan, WatchProbe[] probes, List<RunicPathWatchError> errors, List<IDisposable> released)
    {
        _remote.UnionWith(plan.Remote);
        foreach (var (key, lease) in plan.Started)
        {
            if (!_subscriptions.TryAdd(key, lease)) released.Add(lease);
            else _reported.Remove($"failed|{key}");
        }
        foreach (var (key, (kind, directory, exception)) in plan.Failed)
        {
            if (_subscriptions.Remove(key, out var lease)) released.Add(lease);
            _failedWatches[key] = kind;
            AddOnce(errors, $"failed|{key}", new(kind, directory, exception));
        }
        foreach (var key in _subscriptions.Keys.Where(key => !plan.Wanted.ContainsKey(key)).ToArray())
        {
            released.Add(_subscriptions[key]);
            _subscriptions.Remove(key);
        }

        // A replaced directory's targets missed their events: read them again.
        if (plan.Replaced.Count > 0)
        {
            foreach (var state in _states)
            {
                if (!state.Probe.Specs.Any(spec => plan.Replaced.Contains(spec.Directory))) continue;
                state.Dirty = true;
            }
            Volatile.Write(ref _requested, 1);
        }

        for (var i = 0; i < _states.Length; i++)
        {
            var probe = probes[i];
            var path = _states[i].Target.FullPath;
            if (probe.MissingDirectory is { } missing)
                AddOnce(errors, $"missing|{path}", new(RunicPathWatchErrorKind.Missing, missing, null));
            else
                _reported.Remove($"missing|{path}");
            if (probe.NotRepository is { } repository)
                AddOnce(errors, $"repository|{path}", new(RunicPathWatchErrorKind.NotGitRepository, repository, null));
            else
                _reported.Remove($"repository|{path}");
        }

        // Poll quickly while a target lacks a watch; otherwise only as a backstop.
        var polled = _options.UsePolling || probes.Any(probe =>
            probe.MissingDirectory is not null
            || probe.Specs.Any(spec => !_subscriptions.ContainsKey(SharedDirectoryWatches.Key(spec.Directory, spec.Recursive))));
        var period = polled ? _options.PollingInterval
            : _options.PollingInterval > Backstop ? _options.PollingInterval : Backstop;
        if (period != _pollPeriod)
        {
            _pollPeriod = period;
            _poll.Change(period, Timeout.InfiniteTimeSpan);
        }
    }

    private void AddOnce(List<RunicPathWatchError> errors, string key, RunicPathWatchError error)
    {
        if (_reported.Add(key)) errors.Add(error);
    }

    // Outside the gate: disposing a FileSystemWatcher can wait for its event thread, which may be waiting for the gate.
    private static void Release(List<IDisposable> released)
    {
        foreach (var lease in released) lease.Dispose();
    }

    private void Deliver(RunicPathChange change)
    {
        Interlocked.Increment(ref _deliveries);
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
                if (error.Kind is RunicPathWatchErrorKind.Missing) ViewsLog.PathWatchPolling(TraceFallbackLogger.Instance, error);
                else ViewsLog.PathWatchProblem(TraceFallbackLogger.Instance, error.Exception, error.ToString());
                continue;
            }
            _context.TryPost(() =>
            {
                if (!_disposed) _failed(error);
            });
        }
    }

    private static bool IsWatchFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException;

    private static RunicPathWatchErrorKind Classify(Exception exception) =>
        IsWatchLimit(exception) ? RunicPathWatchErrorKind.WatchLimit : RunicPathWatchErrorKind.Failed;

    private static bool IsRemote(string directory)
    {
        if (OperatingSystem.IsWindows() && directory.StartsWith(@"\\", StringComparison.Ordinal)
            && !directory.StartsWith(@"\\?\", StringComparison.Ordinal)) return true;
        try
        {
            var drive = new DriveInfo(directory);
            if (drive.DriveType == DriveType.Network) return true;
            // inotify sees only local changes on FUSE file systems such as sshfs, and on virtiofs, the host's shared
            // folders in a virtual machine. .NET reports their statfs type as "fuse" (virtiofs is FUSE-based) and
            // gives them no network drive type; "virtiofs" covers a runtime that names it.
            return OperatingSystem.IsLinux() && drive.DriveFormat is var format
                && (format.Equals("fuse", StringComparison.OrdinalIgnoreCase) || format.Equals("virtiofs", StringComparison.OrdinalIgnoreCase));
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
        // An event, not a poll, named the target since its last reading.
        public bool ByEvent { get; set; }
    }

    // One sink per watcher; the shared watch holds it until the watcher releases its leases.
    private sealed class Sink(RunicPathWatcher owner) : IDirectoryWatchSink
    {
        public void OnPath(string fullPath) => owner.OnPath(fullPath);

        public void OnFailure(string key, string directory, Exception exception, bool stopped) =>
            owner.OnFailure(key, directory, exception, stopped);
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
    internal RunicPathChange(IReadOnlyList<RunicWatchTarget> targets, IReadOnlyList<RunicPathChangeEntry> entries)
    {
        Targets = targets;
        Entries = entries;
    }

    /// <summary>Gets the targets that changed, in the order they were passed to <see cref="RunicPathWatcher.Start"/>.</summary>
    public IReadOnlyList<RunicWatchTarget> Targets { get; }

    /// <summary>Gets the paths that changed: at least one for each of <see cref="Targets"/>, in the same order.</summary>
    /// <remarks>
    /// <para>
    /// The watcher compares what it read of each target before and after the change. For a
    /// <see cref="RunicWatchTarget.File"/>, the entry is the file. For a <see cref="RunicWatchTarget.Directory"/> or
    /// <see cref="RunicWatchTarget.Files"/>, the entries are the matching entries that were added
    /// (<see cref="RunicPathChangeKind.Created"/>) or removed (<see cref="RunicPathChangeKind.Deleted"/>), a renamed
    /// entry being both, in ordinal order of their names; for <see cref="RunicWatchTarget.Files"/> also those whose
    /// size, write time, kind or link target changed (<see cref="RunicPathChangeKind.Changed"/>). Where the
    /// watcher cannot name what changed, as for a <see cref="RunicWatchTarget.Link"/>, a
    /// <see cref="RunicWatchTarget.GitHead"/>, an unreadable directory or an event that named an entry whose
    /// metadata stayed the same, the entry is the target's own full path with <see cref="RunicPathChangeKind.Changed"/>.
    /// </para>
    /// <para>
    /// Changes are compared between readings, so an entry created and deleted again between two readings is not
    /// listed.
    /// </para>
    /// </remarks>
    public IReadOnlyList<RunicPathChangeEntry> Entries { get; }

    // Whether an event, rather than a poll, led to reading a target that changed. For tests.
    internal bool ByEvent { get; init; }

    /// <summary>Gets whether <paramref name="target"/> is among the changed targets.</summary>
    /// <param name="target">A target passed to <see cref="RunicPathWatcher.Start"/>.</param>
    public bool Contains(RunicWatchTarget target) => Targets.Contains(target);
}

/// <summary>A path that changed, one of the <see cref="RunicPathChange.Entries"/> of a change.</summary>
public sealed class RunicPathChangeEntry
{
    internal RunicPathChangeEntry(RunicWatchTarget target, string path, RunicPathChangeKind kind)
    {
        Target = target;
        Path = path;
        Kind = kind;
    }

    /// <summary>Gets the target that reported the path, an instance passed to <see cref="RunicPathWatcher.Start"/>.</summary>
    public RunicWatchTarget Target { get; }

    /// <summary>Gets the full path that changed: an entry of a directory target, or the target's own path.</summary>
    public string Path { get; }

    /// <summary>Gets how the path changed.</summary>
    public RunicPathChangeKind Kind { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind} {Path}";
}

/// <summary>How a <see cref="RunicPathChangeEntry"/> changed.</summary>
/// <remarks>The set is open: a minor release may add kinds, so treat an unknown kind as <see cref="Changed"/>.</remarks>
public enum RunicPathChangeKind
{
    /// <summary>The path was written, replaced or otherwise changed, or the watcher cannot say how.</summary>
    Changed,

    /// <summary>The path did not exist at the previous reading and exists now.</summary>
    Created,

    /// <summary>The path existed at the previous reading and does not exist now.</summary>
    Deleted,
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
        RunicPathWatchErrorKind.NotGitRepository => $"{Path} is not a Git repository; watching for one to appear.",
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

    /// <summary>
    /// A <see cref="RunicWatchTarget.GitHead"/> directory exists but is not a Git repository. The watcher watches the
    /// directory for a <c>.git</c> or <c>HEAD</c> to appear and reports the change when one does.
    /// </summary>
    NotGitRepository,
}

internal interface IRunicPathWatchObserver
{
    void Started(RunicPathWatcher watcher);

    void Stopped(RunicPathWatcher watcher);

    // Called in the model turn after the change callback returned.
    void Delivered(RunicPathWatcher watcher, RunicPathChange change);
}
