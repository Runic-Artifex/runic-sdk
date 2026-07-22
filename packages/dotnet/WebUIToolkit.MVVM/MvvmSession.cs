using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WebUIToolkit.MVVM;

internal sealed class MvvmSession : IMvvmSession
{
    private static readonly MvvmFault ClosedFault = new(MvvmFaultCodes.SessionClosed, "The session is closed.");
    private readonly IMvvmBindingAdapter _adapter;
    private readonly object[] _ownedResources;
    private readonly MvvmLimits _limits;
    private readonly byte[] _capabilityBytes;
    private readonly Func<MvvmSession, ValueTask> _onClosed;
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<MvvmRequestId, PendingRequest> _pending = new();
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _revision;
    private long _acknowledgedRevision = -1;
    private int _pendingCount;
    private int _disposeState;

    internal MvvmSession(
        MvvmSessionId id,
        MvvmContract contract,
        string capabilityToken,
        MvvmSessionActivation activation,
        MvvmLimits limits,
        Func<MvvmSession, ValueTask> onClosed)
    {
        Id = id;
        Contract = contract;
        CapabilityToken = capabilityToken;
        _capabilityBytes = Encoding.ASCII.GetBytes(capabilityToken);
        _adapter = activation.Adapter;
        _ownedResources = activation.OwnedResources.ToArray();
        _limits = limits;
        _onClosed = onClosed;
    }

    public MvvmSessionId Id { get; }

    public MvvmContract Contract { get; }

    public string CapabilityToken { get; }

    public bool Authorizes(string capabilityToken)
    {
        if (capabilityToken is null)
        {
            return false;
        }

        byte[] candidate = Encoding.UTF8.GetBytes(capabilityToken);
        return CryptographicOperations.FixedTimeEquals(candidate, _capabilityBytes);
    }

    public long Revision => Interlocked.Read(ref _revision);

    public long? AcknowledgedRevision
    {
        get
        {
            long revision = Interlocked.Read(ref _acknowledgedRevision);
            return revision < 0 ? null : revision;
        }
    }

    public async ValueTask<MvvmResponse> DispatchAsync(MvvmRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Volatile.Read(ref _disposeState) != 0)
        {
            return MvvmResponse.Rejected(request.RequestId, Revision, ClosedFault);
        }

        if (request is MvvmCancelRequest cancellation)
        {
            return await CancelAsync(cancellation).ConfigureAwait(false);
        }

        if (Interlocked.Increment(ref _pendingCount) > _limits.MaxPendingRequests)
        {
            Interlocked.Decrement(ref _pendingCount);
            return MvvmResponse.Rejected(
                request.RequestId,
                Revision,
                MvvmFaultCodes.LimitExceeded,
                "The session pending-request limit was exceeded.");
        }

        TimeSpan timeout = request is MvvmMutationRequest { Kind: MvvmMutationKind.ExecuteCommand }
            ? _limits.MaxCommandDuration
            : Timeout.InfiniteTimeSpan;
        using var pending = new PendingRequest(timeout, cancellationToken, _shutdown.Token);
        if (!_pending.TryAdd(request.RequestId, pending))
        {
            Interlocked.Decrement(ref _pendingCount);
            return MvvmResponse.Rejected(
                request.RequestId,
                Revision,
                MvvmFaultCodes.RequestInvalid,
                "The request identifier is already in flight.");
        }

        try
        {
            await _dispatchGate.WaitAsync(pending.Token).ConfigureAwait(false);
            try
            {
                MvvmResponse response = request switch
                {
                    MvvmMutationRequest mutation => await MutateAsync(mutation, pending, pending.Token).ConfigureAwait(false),
                    MvvmSnapshotRequest snapshot => await SnapshotAsync(snapshot, pending, pending.Token).ConfigureAwait(false),
                    MvvmAcknowledgeRequest acknowledgement => Acknowledge(acknowledgement, pending),
                    _ => MvvmResponse.Rejected(
                        request.RequestId,
                        Revision,
                        MvvmFaultCodes.RequestInvalid,
                        "The request kind is invalid."),
                };
                return Publish(pending, response);
            }
            finally
            {
                _dispatchGate.Release();
            }
        }
        catch (OperationCanceledException) when (pending.Token.IsCancellationRequested)
        {
            pending.Complete();
            return Publish(pending, CancellationResponse(request.RequestId, pending));
        }
        catch (Exception)
        {
            CancellationCause earlyCause = pending.Complete();
            if (earlyCause != CancellationCause.Completed)
            {
                return Publish(pending, CancellationResponse(request.RequestId, pending));
            }

            return Publish(pending, MvvmResponse.Rejected(
                request.RequestId,
                Revision,
                MvvmFaultCodes.RequestInvalid,
                "The request could not be completed."));
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
            Interlocked.Decrement(ref _pendingCount);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        Exception? firstFailure = null;
        try
        {
            _shutdown.Cancel();

            await _dispatchGate.WaitAsync().ConfigureAwait(false);
            try
            {
                try
                {
                    await _adapter.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    firstFailure = exception;
                }

                for (int index = _ownedResources.Length - 1; index >= 0; index--)
                {
                    try
                    {
                        switch (_ownedResources[index])
                        {
                            case IAsyncDisposable asyncDisposable:
                                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                                break;
                            case IDisposable disposable:
                                disposable.Dispose();
                                break;
                        }
                    }
                    catch (Exception exception)
                    {
                        firstFailure ??= exception;
                    }
                }
            }
            finally
            {
                _dispatchGate.Release();
            }
        }
        finally
        {
            try
            {
                await _onClosed(this).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
            }

            if (firstFailure is null)
            {
                _disposeCompletion.TrySetResult();
            }
            else
            {
                _disposeCompletion.TrySetException(firstFailure);
            }
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
        }
    }

    private async ValueTask<MvvmResponse> CancelAsync(MvvmCancelRequest request)
    {
        if (request.TargetRequestId == request.RequestId)
        {
            return MvvmResponse.Rejected(
                request.RequestId,
                Revision,
                MvvmFaultCodes.RequestInvalid,
                "A cancellation request cannot target itself.");
        }

        if (!_pending.TryGetValue(request.TargetRequestId, out PendingRequest? target))
        {
            return MvvmResponse.Success(request.RequestId, Revision, cancellationAccepted: false);
        }

        bool accepted = target.Cancel();
        long revision = accepted ? Revision : await target.Publication.ConfigureAwait(false);
        return MvvmResponse.Success(request.RequestId, revision, cancellationAccepted: accepted);
    }

    private async ValueTask<MvvmResponse> MutateAsync(
        MvvmMutationRequest request,
        PendingRequest pending,
        CancellationToken cancellationToken)
    {
        long revision = Revision;
        if (request.BaseRevision != revision)
        {
            CancellationCause earlyCause = pending.Complete();
            if (earlyCause != CancellationCause.Completed)
            {
                return CancellationResponse(request.RequestId, pending);
            }

            return MvvmResponse.Rejected(
                request.RequestId,
                revision,
                MvvmFaultCodes.RevisionStale,
                "The mutation is based on a stale revision; request a snapshot.");
        }

        if (!JsonWithinLimits(request.Payload) || Encoding.UTF8.GetByteCount(request.Payload.GetRawText()) > _limits.MaxPayloadBytes)
        {
            CancellationCause earlyCause = pending.Complete();
            if (earlyCause != CancellationCause.Completed)
            {
                return CancellationResponse(request.RequestId, pending);
            }

            return MvvmResponse.Rejected(
                request.RequestId,
                revision,
                MvvmFaultCodes.LimitExceeded,
                "The request payload limit was exceeded.");
        }

        if (revision == long.MaxValue)
        {
            CancellationCause earlyCause = pending.Complete();
            if (earlyCause != CancellationCause.Completed)
            {
                return CancellationResponse(request.RequestId, pending);
            }

            return MvvmResponse.Rejected(
                request.RequestId,
                revision,
                MvvmFaultCodes.LimitExceeded,
                "The session revision limit was exceeded.");
        }

        MvvmBindingResult result = await _adapter.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        CancellationCause cause = pending.Complete();

        bool resultExceedsLimit = result.Patches.Count > _limits.MaxPatchOperations || PayloadTooLarge(result.Payload, result.Patches);
        long nextRevision = revision;
        if (result.Committed)
        {
            nextRevision = checked(revision + 1);
            Interlocked.Exchange(ref _revision, nextRevision);
        }

        MvvmFault? terminalFault = cause == CancellationCause.Completed ? null : FaultForCause(cause);
        if (terminalFault is null && resultExceedsLimit)
        {
            terminalFault = new MvvmFault(MvvmFaultCodes.LimitExceeded, "The adapter result exceeded a configured limit.");
        }

        if (terminalFault is null && !result.Succeeded)
        {
            terminalFault = SafeAdapterFault(result.Fault);
        }

        if (terminalFault is not null)
        {
            IReadOnlyList<MvvmPatch> patches = resultExceedsLimit ? [] : result.Patches;
            return MvvmResponse.Rejected(request.RequestId, nextRevision, terminalFault, null, patches);
        }

        return MvvmResponse.Success(request.RequestId, nextRevision, result.Payload, result.Patches);
    }

    private async ValueTask<MvvmResponse> SnapshotAsync(
        MvvmSnapshotRequest request,
        PendingRequest pending,
        CancellationToken cancellationToken)
    {
        MvvmSnapshot snapshot = await _adapter.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        CancellationCause cause = pending.Complete();
        if (cause != CancellationCause.Completed)
        {
            return CancellationResponse(request.RequestId, pending);
        }

        if (!JsonWithinLimits(snapshot.State) ||
            SnapshotMemberLimitExceeded(snapshot.State) ||
            Encoding.UTF8.GetByteCount(snapshot.State.GetRawText()) > _limits.MaxPayloadBytes)
        {
            return MvvmResponse.Rejected(
                request.RequestId,
                Revision,
                MvvmFaultCodes.LimitExceeded,
                "The snapshot payload limit was exceeded.");
        }

        return MvvmResponse.Success(request.RequestId, Revision, snapshot.State);
    }

    private MvvmResponse Acknowledge(MvvmAcknowledgeRequest request, PendingRequest pending)
    {
        CancellationCause cause = pending.Complete();
        if (cause != CancellationCause.Completed)
        {
            return CancellationResponse(request.RequestId, pending);
        }

        long current = Revision;
        if (request.Revision > current)
        {
            return MvvmResponse.Rejected(
                request.RequestId,
                current,
                MvvmFaultCodes.RequestInvalid,
                "A future revision cannot be acknowledged.");
        }

        long observed = Interlocked.Read(ref _acknowledgedRevision);
        while (request.Revision > observed)
        {
            long exchanged = Interlocked.CompareExchange(ref _acknowledgedRevision, request.Revision, observed);
            if (exchanged == observed)
            {
                break;
            }

            observed = exchanged;
        }

        return MvvmResponse.Success(request.RequestId, current);
    }

    private MvvmResponse CancellationResponse(MvvmRequestId requestId, PendingRequest pending)
    {
        MvvmFault fault = FaultForCause(pending.Cause) ??
            new MvvmFault(MvvmFaultCodes.RequestInvalid, "The request could not be completed.");
        return MvvmResponse.Rejected(requestId, Revision, fault);
    }

    private static MvvmFault? FaultForCause(CancellationCause cause) => cause switch
    {
        CancellationCause.Caller or CancellationCause.Explicit =>
            new MvvmFault(MvvmFaultCodes.RequestCancelled, "The request was cancelled."),
        CancellationCause.Timeout =>
            new MvvmFault(MvvmFaultCodes.RequestTimeout, "The request timed out."),
        CancellationCause.Shutdown => ClosedFault,
        _ => null,
    };

    private static MvvmFault SafeAdapterFault(MvvmFault? fault) => fault?.Code switch
    {
        MvvmFaultCodes.MemberUnknown => new MvvmFault(MvvmFaultCodes.MemberUnknown, "The requested member is unknown."),
        MvvmFaultCodes.RevisionStale => new MvvmFault(MvvmFaultCodes.RevisionStale, "The mutation is based on a stale revision."),
        MvvmFaultCodes.LimitExceeded => new MvvmFault(MvvmFaultCodes.LimitExceeded, "A configured limit was exceeded."),
        MvvmFaultCodes.RequestCancelled => new MvvmFault(MvvmFaultCodes.RequestCancelled, "The request was cancelled."),
        MvvmFaultCodes.RequestTimeout => new MvvmFault(MvvmFaultCodes.RequestTimeout, "The request timed out."),
        MvvmFaultCodes.SessionClosed => ClosedFault,
        MvvmFaultCodes.ProtocolUnsupported => new MvvmFault(MvvmFaultCodes.ProtocolUnsupported, "The requested protocol is unsupported."),
        _ => new MvvmFault(MvvmFaultCodes.RequestInvalid, "The adapter rejected the request."),
    };

    private static MvvmResponse Publish(PendingRequest pending, MvvmResponse response)
    {
        pending.Publish(response.Revision);
        return response;
    }

    private bool PayloadTooLarge(JsonElement? payload, IReadOnlyList<MvvmPatch> patches)
    {
        if (payload is not null && !JsonWithinLimits(payload.Value))
        {
            return true;
        }

        long byteCount = payload is null ? 0 : Encoding.UTF8.GetByteCount(payload.Value.GetRawText());
        int collectionItemCount = 0;
        foreach (MvvmPatch patch in patches)
        {
            switch (patch)
            {
                case MvvmPropertyPatch property:
                    if (!JsonWithinLimits(property.Value))
                    {
                        return true;
                    }

                    byteCount += Encoding.UTF8.GetByteCount(property.Value.GetRawText());
                    break;
                case MvvmCollectionPatch collection:
                    collectionItemCount += collection.Items.Count;
                    foreach (JsonElement item in collection.Items)
                    {
                        if (!JsonWithinLimits(item))
                        {
                            return true;
                        }

                        byteCount += Encoding.UTF8.GetByteCount(item.GetRawText());
                    }

                    break;
                case MvvmValidationPatch validation:
                    foreach (string error in validation.Errors)
                    {
                        byteCount += Encoding.UTF8.GetByteCount(error);
                    }

                    break;
            }

            if (byteCount > _limits.MaxPayloadBytes || collectionItemCount > _limits.MaxCollectionItems)
            {
                return true;
            }
        }

        return false;
    }

    private bool JsonWithinLimits(JsonElement element, int depth = 1)
    {
        if (depth > _limits.MaxJsonDepth)
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return Encoding.UTF8.GetByteCount(element.GetString() ?? string.Empty) <= _limits.MaxStringBytes;
            case JsonValueKind.Array:
                if (element.GetArrayLength() > _limits.MaxArrayItems)
                {
                    return false;
                }

                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (!JsonWithinLimits(item, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Object:
                int propertyCount = 0;
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    propertyCount++;
                    if (propertyCount > _limits.MaxObjectProperties ||
                        Encoding.UTF8.GetByteCount(property.Name) > MvvmLimits.MaximumPropertyNameBytes ||
                        !JsonWithinLimits(property.Value, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Undefined:
                return false;
            default:
                return true;
        }
    }

    private bool SnapshotMemberLimitExceeded(JsonElement state) =>
        state.ValueKind == JsonValueKind.Object &&
        state.TryGetProperty("members", out JsonElement members) &&
        members.ValueKind == JsonValueKind.Array &&
        members.GetArrayLength() > _limits.MaxSnapshotMembers;

    private sealed class PendingRequest : IDisposable
    {
        private readonly CancellationTokenSource _combined = new();
        private readonly CancellationTokenSource _timeout = new();
        private readonly CancellationTokenRegistration _callerRegistration;
        private readonly CancellationTokenRegistration _shutdownRegistration;
        private readonly CancellationTokenRegistration _timeoutRegistration;
        private readonly object _terminalGate = new();
        private readonly TaskCompletionSource<long> _publication = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cause;

        internal PendingRequest(TimeSpan timeout, CancellationToken caller, CancellationToken shutdown)
        {
            _callerRegistration = caller.Register(static state => ((PendingRequest)state!).Signal(CancellationCause.Caller), this);
            _shutdownRegistration = shutdown.Register(static state => ((PendingRequest)state!).Signal(CancellationCause.Shutdown), this);
            _timeoutRegistration = _timeout.Token.Register(static state => ((PendingRequest)state!).Signal(CancellationCause.Timeout), this);
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                _timeout.CancelAfter(timeout);
            }
        }

        internal CancellationToken Token => _combined.Token;

        internal CancellationCause Cause => (CancellationCause)Volatile.Read(ref _cause);

        internal Task<long> Publication => _publication.Task;

        internal bool Cancel() => Signal(CancellationCause.Explicit);

        internal CancellationCause Complete()
        {
            lock (_terminalGate)
            {
                if (_cause == (int)CancellationCause.None)
                {
                    _cause = (int)CancellationCause.Completed;
                }

                return (CancellationCause)_cause;
            }
        }

        internal void Publish(long revision) => _publication.TrySetResult(revision);

        public void Dispose()
        {
            _timeoutRegistration.Dispose();
            _shutdownRegistration.Dispose();
            _callerRegistration.Dispose();
            _timeout.Dispose();
            // The combined source may still be completing isolated consumer callbacks.
            // It is left for collection with this short-lived pending-request object.
        }

        private bool Signal(CancellationCause cause)
        {
            bool won;
            lock (_terminalGate)
            {
                if (_cause != (int)CancellationCause.None)
                {
                    return false;
                }

                _cause = (int)cause;
                won = true;
            }

            _ = CancelSafelyAsync(_combined);
            return won;
        }

        private static async Task CancelSafelyAsync(CancellationTokenSource source)
        {
            try
            {
                await source.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Consumer cancellation callbacks are not allowed to escape the protocol boundary.
            }
        }
    }

    private enum CancellationCause
    {
        None,
        Caller,
        Explicit,
        Timeout,
        Shutdown,
        Completed,
    }
}
