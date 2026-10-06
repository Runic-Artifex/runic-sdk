using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Runic.Application.Testing.Tests")]

namespace Runic.Application.Views;

// A window-owned candidate for the next operation protocol. It deliberately
// remains internal until the generator, host adapters, and MVVM integrations
// agree on an author-facing shape.
internal sealed class BridgeOperationRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly Dictionary<string, Entry> _operations = new(StringComparer.Ordinal);
    private readonly Queue<Entry> _terminals = new();
    private readonly Dictionary<string, BridgeOperationRequest?> _expiredIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _expiredOrder = new();
    private readonly HashSet<BridgeAwaitedExecution> _awaited = [];
    private readonly CancellationTokenSource _ownerShutdown;
    private readonly int _maximumOperations;
    private readonly int _maximumRetainedTerminals;
    private readonly int _maximumRetainedExpiredIds;
    private readonly int _maximumRetainedResultBytes;
    private readonly int _maximumRunningStreamBytes;
    private int _retainedBytes;
    private int _reservedRunningStreamBytes;
    private bool _closing;
    private bool _disposed;

    // `ownerId` is diagnostic identity for the window/session that owns the
    // work. Caller/component detachment is intentionally not represented here.
    internal BridgeOperationRegistry(
        string ownerId,
        int maximumOperations = 64,
        int maximumRetainedTerminals = 32,
        int maximumRetainedExpiredIds = 128,
        int maximumRetainedResultBytes = 262_144,
        int maximumRunningStreamBytes = 262_144,
        ILogger? logger = null,
        CancellationToken ownerShutdown = default)
    {
        _logger = logger ?? TraceFallbackLogger.Instance;
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("An owner identity is required.", nameof(ownerId));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOperations, 1);
        if (maximumRetainedTerminals < 0 || maximumRetainedTerminals > maximumOperations)
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedTerminals));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetainedExpiredIds);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetainedResultBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRunningStreamBytes);

        OwnerId = ownerId;
        _maximumOperations = maximumOperations;
        _maximumRetainedTerminals = maximumRetainedTerminals;
        _maximumRetainedExpiredIds = maximumRetainedExpiredIds;
        _maximumRetainedResultBytes = maximumRetainedResultBytes;
        _maximumRunningStreamBytes = maximumRunningStreamBytes;
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
        return AcceptCore(requestId, request: null, canStart: null, stream: null, async (_, cancellationToken) =>
        {
            await work(cancellationToken).ConfigureAwait(false);
            return BridgeOperationResult.None;
        });
    }

    // A typed request reserves the request identity before availability is
    // evaluated. Consequently an idempotent retry observes prior work even
    // when current CanExecute has changed, while a changed member or input is
    // rejected instead of being misidentified as the original request.
    internal BridgeOperationAdmission Accept(
        BridgeOperationRequest request,
        Func<bool> canStart,
        Func<CancellationToken, Task<BridgeOperationResult>> work)
    {
        ArgumentNullException.ThrowIfNull(canStart);
        ArgumentNullException.ThrowIfNull(work);
        return AcceptCore(request.Identity.RegistryKey, request, canStart, stream: null,
            (_, cancellationToken) => work(cancellationToken));
    }

    // A stream belongs to the admitted entry before work starts. Reads can
    // replay published values while the command is still running; returning a
    // stream only from a terminal task would make incremental delivery
    // impossible.
    internal BridgeOperationAdmission Accept(
        BridgeOperationRequest request,
        Func<bool> canStart,
        BridgeOperationStream stream,
        Func<BridgeOperationExecution, CancellationToken, Task<BridgeOperationResult>> work)
    {
        ArgumentNullException.ThrowIfNull(canStart);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(work);
        return AcceptCore(request.Identity.RegistryKey, request, canStart, stream, work);
    }

    private BridgeOperationAdmission AcceptCore(
        string requestId,
        BridgeOperationRequest? request,
        Func<bool>? canStart,
        BridgeOperationStream? stream,
        Func<BridgeOperationExecution, CancellationToken, Task<BridgeOperationResult>> work)
    {

        Entry accepted;
        lock (_gate)
        {
            if (_disposed || _closing)
                return new(requestId, BridgeOperationAdmissionKind.Rejected, BridgeOperationStatusKind.Unknown,
                    _disposed ? "owner-disposed" : "owner-closing");

            if (_operations.TryGetValue(requestId, out var existing))
            {
                if (!existing.Matches(request))
                    return new(requestId, BridgeOperationAdmissionKind.Rejected, BridgeOperationStatusKind.Unknown,
                        "identity-conflict");
                return new(requestId, BridgeOperationAdmissionKind.Duplicate, existing.Status, null,
                    existing.Status is BridgeOperationStatusKind.Running ? null : existing.Snapshot());
            }

            if (_expiredIds.TryGetValue(requestId, out var expiredRequest))
                return !Matches(expiredRequest, request)
                    ? new(requestId, BridgeOperationAdmissionKind.Rejected, BridgeOperationStatusKind.Unknown, "identity-conflict")
                    : new(requestId, BridgeOperationAdmissionKind.Expired, BridgeOperationStatusKind.Expired, "expired");

            TrimTerminalEntries(preferCapacity: true);
            if (_operations.Count >= _maximumOperations)
                return new(requestId, BridgeOperationAdmissionKind.Rejected, BridgeOperationStatusKind.Unknown, "capacity");

            if (canStart is not null && !canStart())
                return new(requestId, BridgeOperationAdmissionKind.Rejected, BridgeOperationStatusKind.Unknown, "unavailable");

            // A stream can retain values while work is still running. Reserve
            // its declared maximum before execution so concurrent producers
            // cannot collectively exceed the window's running-memory bound.
            if (stream is not null &&
                (stream.MaximumBytes > _maximumRunningStreamBytes ||
                 _reservedRunningStreamBytes > _maximumRunningStreamBytes - stream.MaximumBytes))
                return new(requestId, BridgeOperationAdmissionKind.Rejected, BridgeOperationStatusKind.Unknown, "stream-capacity");

            accepted = new Entry(requestId, request, stream,
                stream?.MaximumBytes ?? 0, _ownerShutdown.Token);
            _reservedRunningStreamBytes += accepted.ReservedRunningStreamBytes;
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
    internal ValueTask<BridgeOperationStatus> WaitForTerminalAsync(
        string requestId, string? member = null, CancellationToken observerCancellation = default)
    {
        Task<BridgeOperationStatus>? terminal;
        lock (_gate)
        {
            if (!_operations.TryGetValue(requestId, out var entry) || !entry.MatchesMember(member))
                return ValueTask.FromResult(StatusForMissing(requestId, member));
            terminal = entry.Terminal.Task;
        }
        return new(terminal.WaitAsync(observerCancellation));
    }

    // Preserve the pre-member internal call shape for host integrations.
    internal ValueTask<BridgeOperationStatus> WaitForTerminalAsync(
        string requestId, CancellationToken observerCancellation) =>
        WaitForTerminalAsync(requestId, member: null, observerCancellation: observerCancellation);

    internal BridgeOperationStatus Lookup(string requestId, string? member = null)
    {
        lock (_gate)
            return _operations.TryGetValue(requestId, out var entry) && entry.MatchesMember(member)
                ? entry.Snapshot()
                : StatusForMissing(requestId, member);
    }

    internal BridgeOperationStreamLookup ReadStream(string requestId, long cursor, string? member = null)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(requestId, out var entry) || !entry.MatchesMember(member))
                return new(StatusForMissing(requestId, member), null);
            return new(entry.Snapshot(), entry.Stream?.ReadAfter(cursor) ?? entry.Result.OperationStream?.ReadAfter(cursor));
        }
    }

    // This is a request to the operation's cancellation token, not a terminal
    // state transition. A successful return still wins over a late request.
    internal bool RequestCancellation(string requestId, string? member = null)
    {
        CancellationTokenSource? cancellation = null;
        lock (_gate)
        {
            if (_operations.TryGetValue(requestId, out var entry) && entry.MatchesMember(member) &&
                entry.Status is BridgeOperationStatusKind.Running)
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

    private async Task Run(Entry entry, Func<BridgeOperationExecution, CancellationToken, Task<BridgeOperationResult>> work)
    {
        BridgeOperationStatusKind terminal;
        string? failure = null;
        BridgeFailureDetail? failureDetail = null;
        BridgeOperationResult result = BridgeOperationResult.None;
        try
        {
            result = await work(new BridgeOperationExecution(entry.Stream), entry.Cancellation.Token).ConfigureAwait(false);
            if (entry.Stream is not null)
            {
                entry.Stream.Complete();
                result = BridgeOperationResult.Stream(entry.Stream);
                if (entry.Stream.Failure is { } streamFailure)
                    result = result.WithoutValue(streamFailure);
            }
            else if (result.Kind is BridgeOperationResultKind.Stream)
            {
                result.OperationStream?.Complete();
                if (result.OperationStream?.Failure is { } streamFailure)
                    result = result.WithoutValue(streamFailure);
            }
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
            // The bounded message is the wire contract. Exception detail joins
            // it only when BridgeDiagnostics allows local failure detail.
            ViewsLog.OperationFailed(_logger, error, entry.Request?.Member ?? "(unnamed)", BridgeTelemetry.ErrorType(error));
            failure = "The operation failed.";
            failureDetail = BridgeDiagnostics.Capture(error);
        }

        // A cancelled or failed producer must not keep a live stream writer
        // after its operation has reached a terminal state.
        entry.Stream?.Complete();
        result.OperationStream?.Complete();

        lock (_gate)
        {
            _reservedRunningStreamBytes -= entry.ReservedRunningStreamBytes;
            if (result.Kind is BridgeOperationResultKind.Value)
            {
                var bytes = result.EncodedByteCount;
                if (bytes > _maximumRetainedResultBytes)
                    result = result.WithoutValue(BridgeOperationDeliveryFailure.ResultTooLarge(_maximumRetainedResultBytes));
                else
                {
                    TrimRetention(bytes);
                    if (_retainedBytes > _maximumRetainedResultBytes - bytes)
                        result = result.WithoutValue(BridgeOperationDeliveryFailure.ResultTooLarge(_maximumRetainedResultBytes));
                    else
                    {
                        entry.RetainedResultBytes = bytes;
                        _retainedBytes += bytes;
                    }
                }
            }
            var retainedStream = entry.Stream ?? result.OperationStream;
            if (retainedStream is not null)
            {
                var bytes = retainedStream.RetainedByteCount;
                if (bytes > _maximumRetainedResultBytes)
                {
                    var delivery = BridgeOperationDeliveryFailure.StreamRetentionTooLarge(_maximumRetainedResultBytes);
                    retainedStream.DiscardRetention(delivery);
                    if (result.OperationStream is not null)
                        result = result.WithoutValue(delivery);
                }
                else
                {
                    TrimRetention(bytes);
                    if (_retainedBytes > _maximumRetainedResultBytes - bytes)
                    {
                        var delivery = BridgeOperationDeliveryFailure.StreamRetentionTooLarge(_maximumRetainedResultBytes);
                        retainedStream.DiscardRetention(delivery);
                        if (result.OperationStream is not null)
                            result = result.WithoutValue(delivery);
                    }
                    else
                    {
                        entry.RetainedStreamBytes = bytes;
                        _retainedBytes += bytes;
                    }
                }
            }
            entry.Status = terminal;
            entry.Failure = failure;
            entry.FailureDetail = failureDetail;
            entry.Result = result;
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
            EvictTerminalEntry(_terminals.Dequeue());
        }
    }

    // Values and replayed stream items are both recovered through the same
    // terminal history, so they compete for one byte budget and evict using
    // the same oldest-terminal policy.
    private void TrimRetention(int requiredBytes)
    {
        while (_retainedBytes > _maximumRetainedResultBytes - requiredBytes && _terminals.Count > 0)
            EvictTerminalEntry(_terminals.Dequeue());
    }

    private void EvictTerminalEntry(Entry candidate)
    {
        if (_operations.TryGetValue(candidate.RequestId, out var current) && ReferenceEquals(candidate, current) &&
            candidate.Status is not BridgeOperationStatusKind.Running)
        {
            _operations.Remove(candidate.RequestId);
            _retainedBytes -= candidate.RetainedResultBytes + candidate.RetainedStreamBytes;
            RetainExpiredId(candidate.RequestId, candidate.Request);
        }
    }

    // Tombstones reject an immediate same-ID replay after terminal eviction.
    // They are finite reconnect assistance, never durable exactly-once state.
    private void RetainExpiredId(string requestId, BridgeOperationRequest? request)
    {
        if (_maximumRetainedExpiredIds == 0) return;
        if (_expiredIds.TryAdd(requestId, request)) _expiredOrder.Enqueue(requestId);
        while (_expiredOrder.Count > _maximumRetainedExpiredIds)
            _expiredIds.Remove(_expiredOrder.Dequeue());
    }

    private BridgeOperationStatus StatusForMissing(string requestId, string? member = null) =>
        _expiredIds.TryGetValue(requestId, out var request) && MatchesMember(request, member)
            ? BridgeOperationStatus.Expired(requestId)
            : BridgeOperationStatus.Unknown(requestId);

    // Awaited command routes reply with their terminal result directly rather
    // than through a recoverable operation identity. They are still window
    // work: close waits for them and owner cancellation reaches them. Returns
    // null once the owner is closing.
    internal BridgeAwaitedExecution? TryBeginAwaited(CancellationToken callerCancellation)
    {
        lock (_gate)
        {
            if (_disposed || _closing) return null;
            var execution = new BridgeAwaitedExecution(this,
                CancellationTokenSource.CreateLinkedTokenSource(_ownerShutdown.Token, callerCancellation));
            _awaited.Add(execution);
            return execution;
        }
    }

    private void CompleteAwaited(BridgeAwaitedExecution execution)
    {
        lock (_gate) _awaited.Remove(execution);
    }

    // Graceful owner shutdown is deliberately separate from Dispose. It
    // prevents a new admission and waits up to the timeout for accepted work.
    // Work still running after the timeout is asked to cancel, while the host
    // keeps its DI scope alive until it observes the terminal results.
    internal async ValueTask<BridgeOperationCloseResult> BeginCloseAsync(
        TimeSpan timeout, CancellationToken callerCancellation = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        Task[] terminals;
        lock (_gate)
        {
            if (_disposed) return BridgeOperationCloseResult.Completed;
            _closing = true;
            terminals = _operations.Values
                .Where(entry => entry.Status is BridgeOperationStatusKind.Running)
                .Select(entry => (Task)entry.Terminal.Task)
                .Concat(_awaited.Select(execution => execution.Completion))
                .ToArray();
        }

        if (terminals.Length == 0) return BridgeOperationCloseResult.Completed;
        var all = Task.WhenAll(terminals);
        try
        {
            await all.WaitAsync(timeout, callerCancellation).ConfigureAwait(false);
            return BridgeOperationCloseResult.Completed;
        }
        catch (TimeoutException)
        {
            int remaining;
            lock (_gate)
                remaining = _awaited.Count + _operations.Values.Count(
                    entry => entry.Status is BridgeOperationStatusKind.Running);
            RequestOwnerCancellation();
            // Terminal outcomes are reported to their callers; this only observes the end.
            var settled = all.ContinueWith(static _ => { }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new(Drained: false, RemainingRunningOperations: remaining, settled);
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
            // observed through each entry's task.
            ViewsLog.OperationCancellationCallbackFailed(_logger, error, BridgeTelemetry.ErrorType(error));
        }
    }

    internal sealed class BridgeAwaitedExecution : IDisposable
    {
        private readonly BridgeOperationRegistry _owner;
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        internal BridgeAwaitedExecution(BridgeOperationRegistry owner, CancellationTokenSource cancellation)
        {
            _owner = owner;
            _cancellation = cancellation;
            Token = cancellation.Token;
        }

        public CancellationToken Token { get; }
        internal Task Completion => _completion.Task;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.CompleteAwaited(this);
            _cancellation.Dispose();
            _completion.TrySetResult();
        }
    }

    private sealed class Entry
    {
        private int _cancellationDisposed;

        public Entry(string requestId, BridgeOperationRequest? request, BridgeOperationStream? stream,
            int reservedRunningStreamBytes, CancellationToken ownerShutdown)
        {
            RequestId = requestId;
            Request = request;
            Stream = stream;
            ReservedRunningStreamBytes = reservedRunningStreamBytes;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ownerShutdown);
        }

        public string RequestId { get; }
        public BridgeOperationRequest? Request { get; }
        public BridgeOperationStream? Stream { get; }
        public CancellationTokenSource Cancellation { get; }
        public BridgeOperationStatusKind Status { get; set; } = BridgeOperationStatusKind.Running;
        public string? Failure { get; set; }
        public BridgeFailureDetail? FailureDetail { get; set; }
        public BridgeOperationResult Result { get; set; } = BridgeOperationResult.None;
        public int RetainedResultBytes { get; set; }
        public int RetainedStreamBytes { get; set; }
        public int ReservedRunningStreamBytes { get; }
        public TaskCompletionSource<BridgeOperationStatus> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Matches(BridgeOperationRequest? request) => BridgeOperationRegistry.Matches(Request, request);
        public bool MatchesMember(string? member) => BridgeOperationRegistry.MatchesMember(Request, member);
        public BridgeOperationStatus Snapshot() => new(RequestId, Status, Failure, Result, FailureDetail);
        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref _cancellationDisposed, 1) == 0)
                Cancellation.Dispose();
        }
    }

    private static bool Matches(BridgeOperationRequest? first, BridgeOperationRequest? second) =>
        first is null && second is null ||
        first is { } firstValue && second is { } secondValue &&
        firstValue.Member == secondValue.Member && firstValue.InputDigest == secondValue.InputDigest;

    private static bool MatchesMember(BridgeOperationRequest? request, string? member) =>
        member is null || request is { } value && value.Member == member;
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
    string? Failure,
    BridgeOperationResult? Result = null,
    BridgeFailureDetail? FailureDetail = null)
{
    public static BridgeOperationStatus Unknown(string requestId) => new(requestId, BridgeOperationStatusKind.Unknown, null);
    public static BridgeOperationStatus Expired(string requestId) => new(requestId, BridgeOperationStatusKind.Expired, null);
}

internal sealed record BridgeOperationCloseResult(bool Drained, int RemainingRunningOperations, Task Remaining)
{
    internal static BridgeOperationCloseResult Completed { get; } = new(true, 0, Task.CompletedTask);
}
internal sealed record BridgeOperationStreamLookup(BridgeOperationStatus Status, BridgeOperationStreamRead? Stream);

// Supplied only to the streaming admission overload. Command descriptors can
// publish each encoded item as work progresses, while the registry retains the
// bounded cursor/replay history under the operation identity.
/// <summary>The execution context of a streaming operation.</summary>
/// <param name="stream">The stream that receives the operation's results.</param>
public sealed class BridgeOperationExecution(BridgeOperationStream? stream)
{
    /// <summary>The stream that receives the operation's results, if any.</summary>
    public BridgeOperationStream? Stream { get; } = stream;
}
