using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("BridgeOperationRegistryProbe")]
[assembly: InternalsVisibleTo("OperationAcceptanceProbe")]

namespace Runic.Application.Views;

// A window-owned candidate for the next operation protocol. It deliberately
// remains internal until the generator, host adapters, and MVVM integrations
// agree on an author-facing shape.
internal sealed class BridgeOperationRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _operations = new(StringComparer.Ordinal);
    private readonly Queue<Entry> _terminals = new();
    private readonly HashSet<string> _expiredIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _expiredOrder = new();
    private readonly CancellationTokenSource _ownerShutdown;
    private readonly int _maximumOperations;
    private readonly int _maximumRetainedTerminals;
    private readonly int _maximumRetainedExpiredIds;
    private bool _closing;
    private bool _disposed;

    // `ownerId` is diagnostic identity for the window/session that owns the
    // work. Caller/component detachment is intentionally not represented here.
    internal BridgeOperationRegistry(
        string ownerId,
        int maximumOperations = 64,
        int maximumRetainedTerminals = 32,
        int maximumRetainedExpiredIds = 128,
        CancellationToken ownerShutdown = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("An owner identity is required.", nameof(ownerId));
        if (maximumOperations < 1) throw new ArgumentOutOfRangeException(nameof(maximumOperations));
        if (maximumRetainedTerminals < 0 || maximumRetainedTerminals > maximumOperations)
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedTerminals));
        if (maximumRetainedExpiredIds < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedExpiredIds));

        OwnerId = ownerId;
        _maximumOperations = maximumOperations;
        _maximumRetainedTerminals = maximumRetainedTerminals;
        _maximumRetainedExpiredIds = maximumRetainedExpiredIds;
        _ownerShutdown = CancellationTokenSource.CreateLinkedTokenSource(ownerShutdown);
    }

    internal string OwnerId { get; }

    // Registration occurs before the delegate is invoked. A duplicate request
    // identity returns an observation of the original work and never invokes
    // the replacement delegate.
    internal BridgeOperationAdmission Accept(string requestId, Func<CancellationToken, Task> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(work);

        Entry accepted;
        lock (_gate)
        {
            if (_disposed || _closing)
                return new(requestId, BridgeOperationAdmissionKind.Rejected, BridgeOperationStatusKind.Unknown,
                    _disposed ? "owner-disposed" : "owner-closing");

            if (_operations.TryGetValue(requestId, out var existing))
                return new(requestId, BridgeOperationAdmissionKind.Duplicate, existing.Status, null,
                    existing.Status is BridgeOperationStatusKind.Running ? null : existing.Snapshot());

            if (_expiredIds.Contains(requestId))
                return new(requestId, BridgeOperationAdmissionKind.Expired, BridgeOperationStatusKind.Expired, "expired");

            TrimTerminalEntries(preferCapacity: true);
            if (_operations.Count >= _maximumOperations)
                return new(requestId, BridgeOperationAdmissionKind.Rejected, BridgeOperationStatusKind.Unknown, "capacity");

            accepted = new Entry(requestId, _ownerShutdown.Token);
            _operations.Add(requestId, accepted);
        }

        _ = Run(accepted, work);
        // Async work executes synchronously until its first incomplete await.
        // Return the actual result when it reached a terminal state already.
        lock (_gate)
            return new(requestId, BridgeOperationAdmissionKind.Accepted, accepted.Status, null,
                accepted.Status is BridgeOperationStatusKind.Running ? null : accepted.Snapshot());
    }

    // A caller may stop waiting with its own token without affecting work
    // owned by this registry. Another presentation can look it up later.
    internal ValueTask<BridgeOperationStatus> WaitForTerminalAsync(string requestId, CancellationToken observerCancellation = default)
    {
        Task<BridgeOperationStatus>? terminal;
        lock (_gate)
        {
            if (!_operations.TryGetValue(requestId, out var entry))
                return ValueTask.FromResult(StatusForMissing(requestId));
            terminal = entry.Terminal.Task;
        }
        return new(terminal.WaitAsync(observerCancellation));
    }

    internal BridgeOperationStatus Lookup(string requestId)
    {
        lock (_gate)
            return _operations.TryGetValue(requestId, out var entry)
                ? entry.Snapshot()
                : StatusForMissing(requestId);
    }

    // This is a request to the operation's cancellation token, not a terminal
    // state transition. A successful return still wins over a late request.
    internal bool RequestCancellation(string requestId)
    {
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            if (_operations.TryGetValue(requestId, out var entry) && entry.Status is BridgeOperationStatusKind.Running)
                cancellation = entry.Cancellation;
        }
        if (cancellation is null) return false;
        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task Run(Entry entry, Func<CancellationToken, Task> work)
    {
        BridgeOperationStatusKind terminal;
        string? failure = null;
        try
        {
            await work(entry.Cancellation.Token).ConfigureAwait(false);
            // Do not inspect IsCancellationRequested here. The work returned
            // successfully, so a cancellation request made before that return
            // cannot rewrite its terminal success.
            terminal = BridgeOperationStatusKind.Succeeded;
        }
        catch (OperationCanceledException)
        {
            terminal = BridgeOperationStatusKind.Cancelled;
        }
        catch (Exception error)
        {
            terminal = BridgeOperationStatusKind.Failed;
            // Command exception details are diagnostic data, never operation
            // wire data. The public envelope stays stable and bounded.
            _ = error;
            failure = "The operation failed.";
        }

        lock (_gate)
        {
            entry.Status = terminal;
            entry.Failure = failure;
            var snapshot = entry.Snapshot();
            entry.Terminal.TrySetResult(snapshot);
            _terminals.Enqueue(entry);
            TrimTerminalEntries(preferCapacity: false);
        }
        // Work has returned or thrown, so no operation code can still need the
        // linked source. Retained status records keep only their immutable
        // terminal snapshot, not an undisposed CancellationTokenSource.
        entry.DisposeCancellation();
    }

    // Terminal history is bounded. During admission, terminal entries may be
    // evicted earlier than their preferred retention count to make room. A
    // running entry is never an eviction candidate.
    private void TrimTerminalEntries(bool preferCapacity)
    {
        while (_terminals.Count > _maximumRetainedTerminals ||
               (preferCapacity && _operations.Count >= _maximumOperations && _terminals.Count > 0))
        {
            var candidate = _terminals.Dequeue();
            if (_operations.TryGetValue(candidate.RequestId, out var current) && ReferenceEquals(candidate, current) &&
                candidate.Status is not BridgeOperationStatusKind.Running)
            {
                _operations.Remove(candidate.RequestId);
                RetainExpiredId(candidate.RequestId);
            }
        }
    }

    // Tombstones reject an immediate same-ID replay after terminal eviction.
    // They are finite reconnect assistance, never durable exactly-once state.
    private void RetainExpiredId(string requestId)
    {
        if (_maximumRetainedExpiredIds == 0) return;
        if (_expiredIds.Add(requestId)) _expiredOrder.Enqueue(requestId);
        while (_expiredOrder.Count > _maximumRetainedExpiredIds)
            _expiredIds.Remove(_expiredOrder.Dequeue());
    }

    private BridgeOperationStatus StatusForMissing(string requestId) =>
        _expiredIds.Contains(requestId)
            ? BridgeOperationStatus.Expired(requestId)
            : BridgeOperationStatus.Unknown(requestId);

    // Graceful owner shutdown is deliberately separate from Dispose. It
    // prevents a new admission, asks all current work to cancel, and lets a
    // host keep its DI scope alive while it awaits terminal results.
    internal async ValueTask<BridgeOperationCloseResult> BeginCloseAsync(
        TimeSpan timeout, CancellationToken callerCancellation = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        Task[] terminals;
        lock (_gate)
        {
            if (_disposed) return new(Drained: true, RemainingRunningOperations: 0);
            _closing = true;
            terminals = _operations.Values
                .Where(entry => entry.Status is BridgeOperationStatusKind.Running)
                .Select(entry => entry.Terminal.Task)
                .ToArray();
        }

        RequestOwnerCancellation();
        if (terminals.Length == 0) return new(Drained: true, RemainingRunningOperations: 0);
        try
        {
            await Task.WhenAll(terminals).WaitAsync(timeout, callerCancellation).ConfigureAwait(false);
            return new(Drained: true, RemainingRunningOperations: 0);
        }
        catch (TimeoutException)
        {
            lock (_gate)
                return new(Drained: false, RemainingRunningOperations: _operations.Values.Count(
                    entry => entry.Status is BridgeOperationStatusKind.Running));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _closing = true;
        }
        RequestOwnerCancellation();
        _ownerShutdown.Dispose();
    }

    private void RequestOwnerCancellation()
    {
        try
        {
            _ownerShutdown.Cancel(throwOnFirstException: false);
        }
        catch (Exception error)
        {
            // User cancellation callbacks must not stop host shutdown. The
            // linked operation token was still signalled; terminal work is
            // observed below through each entry's task.
            _ = error;
        }
    }

    private sealed class Entry
    {
        private int _cancellationDisposed;

        public Entry(string requestId, CancellationToken ownerShutdown)
        {
            RequestId = requestId;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ownerShutdown);
        }

        public string RequestId { get; }
        public CancellationTokenSource Cancellation { get; }
        public BridgeOperationStatusKind Status { get; set; } = BridgeOperationStatusKind.Running;
        public string? Failure { get; set; }
        public TaskCompletionSource<BridgeOperationStatus> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BridgeOperationStatus Snapshot() => new(RequestId, Status, Failure);
        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref _cancellationDisposed, 1) == 0)
                Cancellation.Dispose();
        }
    }
}

internal enum BridgeOperationAdmissionKind { Accepted, Duplicate, Expired, Rejected }
internal enum BridgeOperationStatusKind { Unknown, Expired, Running, Succeeded, Failed, Cancelled }

internal sealed record BridgeOperationAdmission(
    string RequestId,
    BridgeOperationAdmissionKind Kind,
    BridgeOperationStatusKind Status,
    string? Reason,
    BridgeOperationStatus? Terminal = null);

internal sealed record BridgeOperationStatus(
    string RequestId,
    BridgeOperationStatusKind Kind,
    string? Failure)
{
    public static BridgeOperationStatus Unknown(string requestId) => new(requestId, BridgeOperationStatusKind.Unknown, null);
    public static BridgeOperationStatus Expired(string requestId) => new(requestId, BridgeOperationStatusKind.Expired, null);
}

internal sealed record BridgeOperationCloseResult(bool Drained, int RemainingRunningOperations);
