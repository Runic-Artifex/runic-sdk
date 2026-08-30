using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Runic.Assets;

namespace Runic.Application.Testing;

/// <summary>Owns deterministic dependencies used by one headless application test.</summary>
public sealed class DeterministicApplicationTestHost : IApplicationHost, IApplicationCapabilityProvider
{
    private readonly ImmutableDictionary<string, ApplicationCapabilityStatus> _capabilities;
    private readonly List<string> _lifecycle = [];
    private readonly int _maximumLifecycleEvents;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ApplicationLifecycleState _state;

    /// <summary>Initializes deterministic test dependencies from stable seed values.</summary>
    public DeterministicApplicationTestHost(DateTimeOffset? initialTime = null, int idSeed = 0, IEnumerable<KeyValuePair<string, string>>? environment = null, bool completeShutdownOnWait = true, int maximumLifecycleEvents = 32, IEnumerable<ApplicationCapabilityStatus>? capabilities = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLifecycleEvents, 1);
        _maximumLifecycleEvents = maximumLifecycleEvents;
        Clock = new DeterministicClock(initialTime ?? DateTimeOffset.UnixEpoch);
        Timers = new DeterministicTimerScheduler(Clock);
        Ids = new DeterministicIdGenerator(idSeed);
        Environment = new DeterministicApplicationEnvironment(environment);
        Bridge = new InMemoryApplicationBridge();
        Assets = new InMemoryApplicationAssets();
        CompleteShutdownOnWait = completeShutdownOnWait;
        _capabilities = (capabilities ?? [])
            .ToImmutableDictionary(status => status.Name, status => status, StringComparer.Ordinal);
    }

    /// <summary>Gets the manually advanced clock.</summary>
    public DeterministicClock Clock { get; }
    /// <summary>Gets the timer scheduler advanced only by the test.</summary>
    public DeterministicTimerScheduler Timers { get; }
    /// <summary>Gets stable incrementing IDs.</summary>
    public DeterministicIdGenerator Ids { get; }
    /// <summary>Gets the immutable test environment.</summary>
    public DeterministicApplicationEnvironment Environment { get; }
    /// <summary>Gets the in-memory bridge transport.</summary>
    public InMemoryApplicationBridge Bridge { get; }
    /// <summary>Gets the in-memory asset source.</summary>
    public InMemoryApplicationAssets Assets { get; }
    /// <summary>Gets the deterministic headless window fake.</summary>
    public HeadlessApplicationWindow Window { get; } = new();
    /// <summary>Gets lifecycle operations in exact order.</summary>
    public ImmutableArray<string> Lifecycle => _lifecycle.ToImmutableArray();
    /// <summary>Gets the current deterministic host lifecycle state.</summary>
    public ApplicationLifecycleState State => _state;
    /// <summary>Gets the manifest received by the headless host.</summary>
    public ApplicationCompositionManifest? Manifest { get; private set; }
    /// <summary>Gets or sets whether waiting immediately observes controlled shutdown.</summary>
    public bool CompleteShutdownOnWait { get; set; }
    /// <summary>Gets an optional lifecycle fault injected by operation name.</summary>
    public Exception? StartFailure { get; set; }
    /// <summary>Gets an optional lifecycle fault injected while waiting.</summary>
    public Exception? WaitFailure { get; set; }
    /// <summary>Gets an optional lifecycle fault injected while stopping.</summary>
    public Exception? StopFailure { get; set; }
    /// <summary>Completes the controlled headless shutdown signal.</summary>
    public void CompleteShutdown() => _shutdown.TrySetResult();

    /// <inheritdoc />
    public ApplicationCapabilityStatus GetCapabilityStatus(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        return _capabilities.TryGetValue(capability, out ApplicationCapabilityStatus? status)
            ? status
            : ApplicationCapabilityStatus.Unavailable(capability, "not-configured-by-headless-host");
    }

    /// <inheritdoc />
    public ValueTask StartAsync(ApplicationCompositionManifest manifest, ReadOnlyMemory<string> arguments, CancellationToken cancellationToken)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Transition(ApplicationLifecycleState.Created, ApplicationLifecycleState.Starting, "start");
        if (StartFailure is not null)
        {
            _state = ApplicationLifecycleState.Faulted;
            return ValueTask.FromException(StartFailure);
        }
        _state = ApplicationLifecycleState.Started;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (_state is not (ApplicationLifecycleState.Started or ApplicationLifecycleState.Waiting or ApplicationLifecycleState.Faulted))
            throw new InvalidOperationException($"Cannot stop a deterministic host in state '{_state}'.");
        Record("stop");
        _state = ApplicationLifecycleState.Stopping;
        if (StopFailure is not null)
        {
            _state = ApplicationLifecycleState.Faulted;
            return ValueTask.FromException(StopFailure);
        }
        _state = ApplicationLifecycleState.Stopped;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        Transition(ApplicationLifecycleState.Started, ApplicationLifecycleState.Waiting, "wait");
        if (WaitFailure is not null)
        {
            _state = ApplicationLifecycleState.Faulted;
            throw WaitFailure;
        }
        if (CompleteShutdownOnWait) CompleteShutdown();
        await _shutdown.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_state is ApplicationLifecycleState.Disposed) return ValueTask.CompletedTask;
        Record("dispose");
        _state = ApplicationLifecycleState.Disposed;
        return ValueTask.CompletedTask;
    }

    private void Transition(ApplicationLifecycleState expected, ApplicationLifecycleState next, string eventName)
    {
        if (_state != expected) throw new InvalidOperationException($"Cannot {eventName} a deterministic host in state '{_state}'.");
        Record(eventName);
        _state = next;
    }

    private void Record(string eventName)
    {
        if (_lifecycle.Count == _maximumLifecycleEvents) throw new InvalidOperationException("The deterministic lifecycle log is full.");
        _lifecycle.Add(eventName);
    }
}

/// <summary>Represents the explicit lifecycle state of a deterministic headless host.</summary>
public enum ApplicationLifecycleState
{
    /// <summary>The host has not started.</summary>
    Created,
    /// <summary>The host is starting.</summary>
    Starting,
    /// <summary>The host has started.</summary>
    Started,
    /// <summary>The host is awaiting controlled shutdown.</summary>
    Waiting,
    /// <summary>The host is stopping.</summary>
    Stopping,
    /// <summary>The host stopped successfully.</summary>
    Stopped,
    /// <summary>A configured lifecycle operation faulted.</summary>
    Faulted,
    /// <summary>The host was disposed.</summary>
    Disposed,
}

/// <summary>A manually advanced UTC clock for deterministic lifecycle tests.</summary>
public sealed class DeterministicClock(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset _utcNow = initial.ToUniversalTime();
    private DeterministicTimerScheduler? _scheduler;
    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _utcNow;
    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return (_scheduler ?? throw new InvalidOperationException("Attach a DeterministicTimerScheduler before creating timers."))
            .CreateTimer(callback, state, dueTime, period);
    }
    internal void Attach(DeterministicTimerScheduler scheduler)
    {
        if (_scheduler is not null && !ReferenceEquals(_scheduler, scheduler)) throw new InvalidOperationException("A deterministic clock has exactly one timer scheduler.");
        _scheduler = scheduler;
    }
    /// <summary>Advances the clock by a non-negative amount.</summary>
    public void Advance(TimeSpan amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount.Ticks, 0L, nameof(amount));
        _utcNow = _utcNow.Add(amount);
    }
}

/// <summary>Schedules deterministic callbacks that run only when the test advances time.</summary>
public sealed class DeterministicTimerScheduler
{
    private readonly DeterministicClock _clock;
    private readonly List<(DateTimeOffset Due, long Sequence, Action Callback)> _scheduled = [];
    private readonly int _maximumScheduledCallbacks;
    private long _nextSequence;

    /// <summary>Creates the scheduler attached to one deterministic clock.</summary>
    public DeterministicTimerScheduler(
        DeterministicClock clock,
        int maximumScheduledCallbacks = 1024,
        int maximumCallbacksPerAdvance = 1024)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _maximumScheduledCallbacks = maximumScheduledCallbacks > 0
            ? maximumScheduledCallbacks
            : throw new ArgumentOutOfRangeException(nameof(maximumScheduledCallbacks));
        MaximumCallbacksPerAdvance = maximumCallbacksPerAdvance > 0
            ? maximumCallbacksPerAdvance
            : throw new ArgumentOutOfRangeException(nameof(maximumCallbacksPerAdvance));
        _clock.Attach(this);
    }

    /// <summary>Gets the deterministic callback work bound for one clock advance.</summary>
    public int MaximumCallbacksPerAdvance { get; }

    /// <summary>Schedules a callback relative to the controlled clock.</summary>
    public void Schedule(TimeSpan dueIn, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThan(dueIn.Ticks, 0L, nameof(dueIn));
        if (_scheduled.Count == _maximumScheduledCallbacks) throw new InvalidOperationException("The deterministic timer queue is full.");
        if (_nextSequence == long.MaxValue) throw new OverflowException("The deterministic timer sequence is exhausted.");
        _scheduled.Add((_clock.GetUtcNow().Add(dueIn), ++_nextSequence, callback));
        _scheduled.Sort(static (left, right) =>
        {
            int due = left.Due.CompareTo(right.Due);
            return due != 0 ? due : left.Sequence.CompareTo(right.Sequence);
        });
    }

    /// <summary>Advances time and runs due callbacks in deterministic insertion order.</summary>
    public void Advance(TimeSpan amount)
    {
        _clock.Advance(amount);
        DateTimeOffset now = _clock.GetUtcNow();
        int executed = 0;
        while (_scheduled.Count > 0 && _scheduled[0].Due <= now)
        {
            if (executed == MaximumCallbacksPerAdvance)
            {
                throw new InvalidOperationException("The deterministic timer advance callback budget is exhausted.");
            }
            Action callback = _scheduled[0].Callback;
            _scheduled.RemoveAt(0);
            callback();
            executed++;
        }
    }

    internal ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        new DeterministicTimer(this, callback, state, dueTime, period);

    private sealed class DeterministicTimer : ITimer
    {
        private readonly DeterministicTimerScheduler _scheduler;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;
        private long _generation;
        private bool _disposed;

        internal DeterministicTimer(DeterministicTimerScheduler scheduler, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _scheduler = scheduler;
            _callback = callback;
            _state = state;
            Change(dueTime, period);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(dueTime));
            if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(period));
            if (_disposed) return false;
            _period = period;
            long generation = ++_generation;
            if (dueTime != Timeout.InfiniteTimeSpan)
                _scheduler.Schedule(dueTime, () => Fire(generation));
            return true;
        }

        private void Fire(long generation)
        {
            if (_disposed || generation != _generation) return;
            _callback(_state);
            // System.Threading.Timer treats zero and infinite periods as one-shot.
            // Only a strictly positive period may schedule another deterministic tick.
            if (!_disposed && generation == _generation && _period > TimeSpan.Zero)
                _scheduler.Schedule(_period, () => Fire(generation));
        }

        public void Dispose() { _disposed = true; _generation++; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>Records native-window state without starting a platform UI.</summary>
public sealed class HeadlessApplicationWindow
{
    /// <summary>Gets whether the fake window is visible.</summary>
    public bool IsVisible { get; private set; }
    /// <summary>Gets the last document path presented by the fake window.</summary>
    public string? DocumentPath { get; private set; }
    /// <summary>Shows a canonical document path.</summary>
    public void Show(string documentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        if (IsVisible) throw new InvalidOperationException("The deterministic headless window is already visible.");
        if (documentPath.Length > 4096) throw new ArgumentOutOfRangeException(nameof(documentPath));
        DocumentPath = documentPath;
        IsVisible = true;
    }
    /// <summary>Closes the fake window.</summary>
    public void Close()
    {
        if (!IsVisible) throw new InvalidOperationException("The deterministic headless window is not visible.");
        IsVisible = false;
    }
}

/// <summary>Produces stable IDs without random or process-global state.</summary>
public sealed class DeterministicIdGenerator(int seed, int maximumPrefixLength = 256)
{
    private int _next = seed;
    private readonly int _maximumPrefixLength = maximumPrefixLength > 0
        ? maximumPrefixLength
        : throw new ArgumentOutOfRangeException(nameof(maximumPrefixLength));
    /// <summary>Gets the next stable ID with the supplied semantic prefix.</summary>
    public string Next(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (prefix.Length > _maximumPrefixLength) throw new ArgumentOutOfRangeException(nameof(prefix));
        int value;
        lock (this)
        {
            if (_next == int.MaxValue) throw new OverflowException("The deterministic ID sequence is exhausted.");
            value = ++_next;
        }
        return $"{prefix}-{value:D8}";
    }
}

/// <summary>Provides a frozen environment map for tests.</summary>
public sealed class DeterministicApplicationEnvironment(IEnumerable<KeyValuePair<string, string>>? values = null)
{
    private readonly ImmutableDictionary<string, string> _values = (values ?? [])
        .ToImmutableDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
    /// <summary>Looks up an environment value without consulting the process environment.</summary>
    public string? Get(string name) => _values.TryGetValue(name, out string? value) ? value : null;
}

/// <summary>In-memory request/response bridge fake with deterministic FIFO delivery.</summary>
public sealed class InMemoryApplicationBridge
{
    private readonly Queue<ApplicationBridgeMessage> _messages = [];
    private readonly int _maximumMessages;
    private readonly int _maximumPayloadBytes;
    private readonly int _maximumOperationLength;

    /// <summary>Creates a bounded message fake.</summary>
    public InMemoryApplicationBridge(int maximumMessages = 1024, int maximumPayloadBytes = 1024 * 1024, int maximumOperationLength = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumMessages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPayloadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOperationLength, 1);
        _maximumMessages = maximumMessages;
        _maximumPayloadBytes = maximumPayloadBytes;
        _maximumOperationLength = maximumOperationLength;
    }
    /// <summary>Sends one immutable bridge message.</summary>
    public void Send(string operation, ReadOnlyMemory<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (operation.Length > _maximumOperationLength) throw new InvalidOperationException("The bridge operation exceeds its deterministic bound.");
        if (payload.Length > _maximumPayloadBytes) throw new InvalidOperationException("The bridge payload exceeds its deterministic bound.");
        lock (_messages)
        {
            if (_messages.Count == _maximumMessages) throw new InvalidOperationException("The bridge queue is full.");
            _messages.Enqueue(new ApplicationBridgeMessage(operation, payload.Span));
        }
    }
    /// <summary>Dequeues the next sent message.</summary>
    public bool TryReceive(out ApplicationBridgeMessage? message)
    {
        lock (_messages)
        {
            if (_messages.TryDequeue(out ApplicationBridgeMessage? next)) { message = next.Copy(); return true; }
        }
        message = null;
        return false;
    }
}

/// <summary>One in-memory bridge message.</summary>
public sealed class ApplicationBridgeMessage
{
    private readonly byte[] _payload;

    internal ApplicationBridgeMessage(string operation, ReadOnlySpan<byte> payload)
    {
        Operation = operation;
        _payload = payload.ToArray();
    }

    /// <summary>Gets the operation name.</summary>
    public string Operation { get; }
    /// <summary>Gets an independent immutable-message payload copy.</summary>
    public byte[] Payload => (byte[])_payload.Clone();

    internal ApplicationBridgeMessage Copy() => new(Operation, _payload);
}

/// <summary>In-memory immutable asset bytes keyed by canonical path.</summary>
public sealed class InMemoryApplicationAssets : IAssetSource
{
    private const int MaximumPathLength = 4096;
    private const int MaximumMediaTypeLength = 256;
    private readonly Dictionary<string, byte[]> _assets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (bool IsEntryPoint, string MediaType)> _metadata = new(StringComparer.Ordinal);
    private readonly int _maximumAssets;
    private readonly int _maximumAssetBytes;
    private readonly int _maximumTotalBytes;
    private readonly string _entryPoint;
    private int _totalBytes;

    /// <summary>Creates a bounded source with one valid default entry document.</summary>
    public InMemoryApplicationAssets(int maximumAssets = 1024, int maximumAssetBytes = 16 * 1024 * 1024, int maximumTotalBytes = 64 * 1024 * 1024, string entryPoint = "index.html")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAssets, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAssetBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTotalBytes, 1);
        _maximumAssets = maximumAssets;
        _maximumAssetBytes = maximumAssetBytes;
        _maximumTotalBytes = maximumTotalBytes;
        _entryPoint = NormalizePath(entryPoint, nameof(entryPoint));
        Set(_entryPoint, "<!doctype html>"u8.ToArray(), isEntryPoint: true);
    }

    /// <summary>Adds or replaces an immutable asset snapshot.</summary>
    public void Set(string path, ReadOnlyMemory<byte> bytes, bool isEntryPoint = false, string mediaType = "application/octet-stream")
    {
        string normalizedPath = NormalizePath(path, nameof(path));
        if (bytes.Length > _maximumAssetBytes) throw new InvalidOperationException("The asset exceeds its deterministic bound.");
        ValidateMediaType(mediaType);
        lock (_assets)
        {
            if (!_assets.ContainsKey(normalizedPath) && _assets.Count == _maximumAssets) throw new InvalidOperationException("The asset store is full.");
            int previousLength = _assets.TryGetValue(normalizedPath, out byte[]? previous) ? previous.Length : 0;
            if (checked(_totalBytes - previousLength + bytes.Length) > _maximumTotalBytes) throw new InvalidOperationException("The deterministic asset store byte bound is exceeded.");
            _assets[normalizedPath] = bytes.ToArray();
            _metadata[normalizedPath] = (isEntryPoint || StringComparer.Ordinal.Equals(normalizedPath, _entryPoint), mediaType);
            _totalBytes = checked(_totalBytes - previousLength + bytes.Length);
        }
    }

    private static string NormalizePath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > MaximumPathLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"An asset path cannot exceed {MaximumPathLength} characters.");
        }

        return AssetPath.Normalize(value);
    }

    private static void ValidateMediaType(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > MaximumMediaTypeLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"An asset media type cannot exceed {MaximumMediaTypeLength} characters.");
        }

        if (value != value.Trim() || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("An asset media type must be a single normalized value.", nameof(value));
        }
    }

    /// <inheritdoc />
    public AssetManifest Manifest
    {
        get
        {
            lock (_assets)
            {
                return new AssetManifest(_assets.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                    new AssetDescriptor(
                        pair.Key,
                        _metadata[pair.Key].MediaType,
                        pair.Value.LongLength,
                        Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
                        _metadata[pair.Key].IsEntryPoint)));
            }
        }
    }

    /// <inheritdoc />
    public ValueTask ValidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Manifest;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGet(relativePath, out byte[]? bytes)) throw new FileNotFoundException("The requested asset is not declared by the test manifest.", relativePath);
        return ValueTask.FromResult<Stream>(new MemoryStream(bytes!, writable: false));
    }
    /// <summary>Reads an independent asset snapshot.</summary>
    public bool TryGet(string path, out byte[]? bytes)
    {
        lock (_assets)
        {
            if (_assets.TryGetValue(path, out byte[]? found))
            {
                bytes = (byte[])found.Clone();
                return true;
            }
        }
        bytes = null;
        return false;
    }
}
