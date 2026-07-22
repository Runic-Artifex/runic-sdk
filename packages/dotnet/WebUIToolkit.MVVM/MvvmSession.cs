using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WebUIToolkit.MVVM;

internal sealed class MvvmSession : IMvvmSession
{
    private static readonly MvvmFault ClosedFault = new(MvvmFaultCodes.SessionClosed, "The session is closed.");
    private readonly IMvvmBindingAdapter _adapter;
    private readonly MvvmBindingVocabulary? _vocabulary;
    private readonly object[] _ownedResources;
    private readonly MvvmLimits _limits;
    private readonly byte[] _capabilityBytes;
    private readonly Func<MvvmSession, ValueTask> _onClosed;
    private readonly object _admissionGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<MvvmRequestId, PendingRequest> _pending = new();
    private readonly HashSet<MvvmRequestId> _seenRequestIds = [];
    private readonly LinkedList<QueuedRequest> _dispatchQueue = [];
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly Task _dispatchLoop;
    private readonly TaskCompletionSource _requestsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _abandonedConsumerTask;
    private MvvmRequestId? _ledgerTerminalRequestId;
    private long _revision;
    private long _acknowledgedRevision = -1;
    private int _pendingCount;
    private int _pendingCancellationCount;
    private bool _queueCompleted;
    private int _poisoned;
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
        if (activation.Adapter is IMvvmBindingVocabularyProvider provider)
        {
            _vocabulary = provider.Vocabulary ??
                throw new InvalidOperationException("The adapter vocabulary is unavailable.");
        }
        _ownedResources = activation.OwnedResources.ToArray();
        _limits = limits;
        _onClosed = onClosed;
        _dispatchLoop = RunDispatchLoopAsync();
    }

    public MvvmSessionId Id { get; }

    public MvvmContract Contract { get; }

    public string CapabilityToken { get; }

    public bool Authorizes(string capabilityToken)
    {
        if (capabilityToken is null || capabilityToken.Length != _capabilityBytes.Length)
        {
            return false;
        }

        Span<byte> candidate = stackalloc byte[43];
        for (int index = 0; index < capabilityToken.Length; index++)
        {
            char character = capabilityToken[index];
            bool isBase64Url = character is >= 'A' and <= 'Z' or
                >= 'a' and <= 'z' or
                >= '0' and <= '9' or '-' or '_';
            if (!isBase64Url)
            {
                return false;
            }

            candidate[index] = (byte)character;
        }

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

        string requestKind = RequestKind(request);
        using MvvmActivity activity = MvvmTelemetry.StartRequest(requestKind);
        long startedTimestamp = MvvmTelemetry.RequestAdmitted();
        MvvmResponse response;
        try
        {
            response = request is MvvmCancelRequest cancellation
                ? await DispatchCancellationAsync(cancellation).ConfigureAwait(false)
                : await DispatchQueuedAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            response = MvvmResponse.Rejected(
                request.RequestId,
                Revision,
                MvvmFaultCodes.RequestInvalid,
                "The request could not be completed.");
        }

        CloseAfterLedgerTerminal(request.RequestId);
        string outcome = response.Succeeded ? "success" : "fault";
        MvvmTelemetry.RequestCompleted(
            activity,
            startedTimestamp,
            requestKind,
            outcome,
            response.Fault?.Code);
        string? capacityLimit = BackpressureLimit(response);
        if (capacityLimit is not null)
        {
            MvvmTelemetry.BackpressureRejected(capacityLimit, requestKind);
        }

        return response;
    }

    private async ValueTask<MvvmResponse> DispatchQueuedAsync(MvvmRequest request, CancellationToken cancellationToken)
    {
        TimeSpan timeout = request is MvvmMutationRequest { Kind: MvvmMutationKind.ExecuteCommand }
            ? _limits.MaxCommandDuration
            : Timeout.InfiniteTimeSpan;
        QueuedRequest queued;
        bool cancellationPending;
        lock (_admissionGate)
        {
            if (_disposeState != 0 || _poisoned != 0)
            {
                return MvvmResponse.Rejected(request.RequestId, Revision, ClosedFault);
            }

            if (_seenRequestIds.Contains(request.RequestId))
            {
                return MvvmResponse.Rejected(
                    request.RequestId,
                    Revision,
                    MvvmFaultCodes.RequestInvalid,
                    "The request identifier has already been processed.");
            }

            if (_seenRequestIds.Count >= MvvmLimits.MaximumRequestLedgerEntries)
            {
                return MvvmResponse.Rejected(
                    request.RequestId,
                    Revision,
                    MvvmFaultCodes.LimitExceeded,
                    "The session request ledger limit was exceeded; open a new session.");
            }

            _seenRequestIds.Add(request.RequestId);
            if (_seenRequestIds.Count == MvvmLimits.MaximumRequestLedgerEntries)
            {
                _ledgerTerminalRequestId = request.RequestId;
            }

            if (_pendingCount >= _limits.MaxPendingRequests)
            {
                return MvvmResponse.Rejected(
                    request.RequestId,
                    Revision,
                    MvvmFaultCodes.LimitExceeded,
                    "The session pending-request limit was exceeded.");
            }

            var pending = new PendingRequest(timeout, cancellationToken, _shutdown.Token);
            queued = new QueuedRequest(request, pending);
            if (!_pending.TryAdd(request.RequestId, pending))
            {
                pending.Dispose();
                return MvvmResponse.Rejected(
                    request.RequestId,
                    Revision,
                    MvvmFaultCodes.RequestInvalid,
                    "The request identifier has already been processed.");
            }

            _pendingCount++;
            queued.Node = _dispatchQueue.AddLast(queued);
            cancellationPending = pending.SetCancellationCallback(queued.CancelBeforeStart);
            _queueSignal.Release();
        }

        if (cancellationPending)
        {
            queued.CancelBeforeStart();
        }

        return await queued.Completion.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        Exception? firstFailure = null;
        Task? deferredCleanup = null;
        long teardownStarted = Stopwatch.GetTimestamp();
        try
        {
            lock (_admissionGate)
            {
                _queueCompleted = true;
                if (_pendingCount == 0)
                {
                    _requestsDrained.TrySetResult();
                }
            }

            _queueSignal.Release();
            _shutdown.Cancel();
            await _dispatchLoop.ConfigureAwait(false);
            await _requestsDrained.Task.ConfigureAwait(false);
            bool consumerQuiesced = true;
            Task? abandonedConsumer = Volatile.Read(ref _abandonedConsumerTask);
            if (abandonedConsumer is not null)
            {
                try
                {
                    consumerQuiesced = await AwaitWithinShutdownAsync(abandonedConsumer, teardownStarted).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Consumer exceptions are observed but never escape the protocol boundary.
                }
            }

            try
            {
                if (consumerQuiesced)
                {
                    bool continueDisposal = true;
                    try
                    {
                        Task adapterDisposal = StartAdapterDisposal();
                        continueDisposal = await AwaitWithinShutdownAsync(adapterDisposal, teardownStarted).ConfigureAwait(false);
                        if (!continueDisposal)
                        {
                            deferredCleanup = ScheduleDeferredCleanup(
                                adapterDisposal,
                                disposeAdapter: false,
                                _ownedResources.Length - 1);
                        }
                    }
                    catch (Exception exception)
                    {
                        firstFailure = exception;
                    }

                    for (int index = _ownedResources.Length - 1; continueDisposal && index >= 0; index--)
                    {
                        try
                        {
                            Task resourceDisposal = StartResourceDisposal(_ownedResources[index]);
                            continueDisposal = await AwaitWithinShutdownAsync(resourceDisposal, teardownStarted).ConfigureAwait(false);
                            if (!continueDisposal)
                            {
                                deferredCleanup = ScheduleDeferredCleanup(
                                    resourceDisposal,
                                    disposeAdapter: false,
                                    index - 1);
                            }
                        }
                        catch (Exception exception)
                        {
                            firstFailure ??= exception;
                        }
                    }
                }
                else if (abandonedConsumer is not null)
                {
                    deferredCleanup = ScheduleDeferredCleanup(
                        abandonedConsumer,
                        disposeAdapter: true,
                        _ownedResources.Length - 1);
                }
            }
            finally
            {
                _queueSignal.Dispose();
                _shutdown.Dispose();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(_capabilityBytes);
            if (deferredCleanup is null)
            {
                try
                {
                    Task closedCallback = Task.Run(async () =>
                        await _onClosed(this).ConfigureAwait(false));
                    _ = await AwaitWithinShutdownAsync(closedCallback, teardownStarted).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    firstFailure ??= exception;
                }
            }
            else
            {
                _ = CloseAfterDeferredCleanupAsync(deferredCleanup);
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

    private async ValueTask<MvvmResponse> DispatchCancellationAsync(MvvmCancelRequest request)
    {
        PendingRequest? target;
        lock (_admissionGate)
        {
            if (_disposeState != 0 || _poisoned != 0)
            {
                return MvvmResponse.Rejected(request.RequestId, Revision, ClosedFault);
            }

            if (_seenRequestIds.Contains(request.RequestId))
            {
                return MvvmResponse.Rejected(
                    request.RequestId,
                    Revision,
                    MvvmFaultCodes.RequestInvalid,
                    "The request identifier has already been processed.");
            }

            if (_seenRequestIds.Count >= MvvmLimits.MaximumRequestLedgerEntries)
            {
                return MvvmResponse.Rejected(
                    request.RequestId,
                    Revision,
                    MvvmFaultCodes.LimitExceeded,
                    "The session request ledger limit was exceeded; open a new session.");
            }

            _seenRequestIds.Add(request.RequestId);
            if (_seenRequestIds.Count == MvvmLimits.MaximumRequestLedgerEntries)
            {
                _ledgerTerminalRequestId = request.RequestId;
            }
            if (request.TargetRequestId == request.RequestId)
            {
                return MvvmResponse.Rejected(
                    request.RequestId,
                    Revision,
                    MvvmFaultCodes.RequestInvalid,
                    "A cancellation request cannot target itself.");
            }

            _pending.TryGetValue(request.TargetRequestId, out target);
            if (target is not null)
            {
                if (_pendingCancellationCount >= _limits.MaxPendingRequests)
                {
                    return MvvmResponse.Rejected(
                        request.RequestId,
                        Revision,
                        MvvmFaultCodes.LimitExceeded,
                        "The session cancellation-control limit was exceeded.");
                }

                _pendingCancellationCount++;
            }
        }

        if (target is null)
        {
            return MvvmResponse.Success(request.RequestId, Revision, cancellationAccepted: false);
        }

        try
        {
            bool accepted = target.Cancel();
            long revision = await target.Publication.ConfigureAwait(false);
            return MvvmResponse.Success(request.RequestId, revision, cancellationAccepted: accepted);
        }
        finally
        {
            lock (_admissionGate)
            {
                _pendingCancellationCount--;
            }
        }
    }

    private async Task RunDispatchLoopAsync()
    {
        while (true)
        {
            await _queueSignal.WaitAsync().ConfigureAwait(false);
            QueuedRequest? queued;
            lock (_admissionGate)
            {
                LinkedListNode<QueuedRequest>? node = _dispatchQueue.First;
                if (node is null)
                {
                    if (_queueCompleted)
                    {
                        return;
                    }

                    continue;
                }

                _dispatchQueue.Remove(node);
                queued = node.Value;
                queued.Node = null;
            }

            if (!queued.TryStart())
            {
                if (queued.IsCancelledBeforeStart)
                {
                    queued.Pending.Complete();
                    MvvmResponse cancelled = CancellationResponse(queued.Request.RequestId, queued.Pending);
                    Publish(queued.Pending, cancelled);
                    CompleteQueuedRequest(queued);
                    queued.Completion.TrySetResult(cancelled);
                }

                continue;
            }

            if (queued.Pending.Cause is not CancellationCause.None)
            {
                queued.Pending.Complete();
                MvvmResponse cancelled = CancellationResponse(queued.Request.RequestId, queued.Pending);
                Publish(queued.Pending, cancelled);
                CompleteQueuedRequest(queued);
                queued.Completion.TrySetResult(cancelled);
                continue;
            }

            MvvmResponse response;
            try
            {
                response = queued.Request switch
                {
                    MvvmMutationRequest mutation =>
                        await MutateAsync(mutation, queued.Pending, queued.Pending.Token).ConfigureAwait(false),
                    MvvmSnapshotRequest snapshot =>
                        await SnapshotAsync(snapshot, queued.Pending, queued.Pending.Token).ConfigureAwait(false),
                    MvvmAcknowledgeRequest acknowledgement => Acknowledge(acknowledgement, queued.Pending),
                    _ => MvvmResponse.Rejected(
                        queued.Request.RequestId,
                        Revision,
                        MvvmFaultCodes.RequestInvalid,
                        "The request kind is invalid."),
                };
            }
            catch (OperationCanceledException) when (queued.Pending.Token.IsCancellationRequested)
            {
                queued.Pending.Complete();
                response = CancellationResponse(queued.Request.RequestId, queued.Pending);
            }
            catch (Exception)
            {
                CancellationCause earlyCause = queued.Pending.Complete();
                response = earlyCause == CancellationCause.Completed
                    ? MvvmResponse.Rejected(
                        queued.Request.RequestId,
                        Revision,
                        MvvmFaultCodes.RequestInvalid,
                        "The request could not be completed.")
                    : CancellationResponse(queued.Request.RequestId, queued.Pending);
            }

            Publish(queued.Pending, response);
            CompleteQueuedRequest(queued);
            queued.Completion.TrySetResult(response);
        }
    }

    private void CompleteQueuedRequest(QueuedRequest queued)
    {
        lock (_admissionGate)
        {
            if (queued.Node is not null)
            {
                _dispatchQueue.Remove(queued.Node);
                queued.Node = null;
            }

            _pending.TryRemove(queued.Request.RequestId, out _);
            _pendingCount--;
            if (_disposeState != 0 && _pendingCount == 0)
            {
                _requestsDrained.TrySetResult();
            }
        }

        queued.Pending.Dispose();
    }

    private async ValueTask<T?> InvokeConsumerAsync<T>(Func<ValueTask<T>> operation, PendingRequest pending)
        where T : class
    {
        Task<T> consumerTask;
        try
        {
            consumerTask = Task.Run(async () =>
                await operation().ConfigureAwait(false));
        }
        catch
        {
            pending.Complete();
            throw;
        }

        _ = consumerTask.ContinueWith(
            static (_, state) => ((PendingRequest)state!).Complete(),
            pending,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        CancellationCause cause = await pending.Terminal.ConfigureAwait(false);
        if (cause == CancellationCause.Completed)
        {
            return await consumerTask.ConfigureAwait(false);
        }

        Volatile.Write(ref _abandonedConsumerTask, consumerTask);
        PoisonSession();
        ObserveLateConsumerTask(consumerTask);
        return null;
    }

    private async ValueTask<bool> AwaitWithinShutdownAsync(Task task, long teardownStarted)
    {
        TimeSpan remaining = _limits.MaxShutdownDuration - Stopwatch.GetElapsedTime(teardownStarted);
        if (remaining <= TimeSpan.Zero)
        {
            ObserveLateConsumerTask(task);
            return false;
        }

        Task winner = await Task.WhenAny(task, Task.Delay(remaining)).ConfigureAwait(false);
        if (!ReferenceEquals(winner, task))
        {
            ObserveLateConsumerTask(task);
            return false;
        }

        await task.ConfigureAwait(false);
        return true;
    }

    private Task StartAdapterDisposal() => Task.Run(async () =>
        await _adapter.DisposeAsync().ConfigureAwait(false));

    private static Task StartResourceDisposal(object resource) => resource switch
    {
        IAsyncDisposable asyncDisposable => Task.Run(async () =>
            await asyncDisposable.DisposeAsync().ConfigureAwait(false)),
        IDisposable disposable => Task.Run(disposable.Dispose),
        _ => Task.CompletedTask,
    };

    private Task ScheduleDeferredCleanup(Task predecessor, bool disposeAdapter, int nextResourceIndex)
    {
        return Task.Run(async () =>
        {
            await ObserveCompletionAsync(predecessor).ConfigureAwait(false);
            if (disposeAdapter)
            {
                await ObserveCompletionAsync(StartAdapterDisposal()).ConfigureAwait(false);
            }

            for (int index = nextResourceIndex; index >= 0; index--)
            {
                await ObserveCompletionAsync(StartResourceDisposal(_ownedResources[index])).ConfigureAwait(false);
            }
        });
    }

    private async Task CloseAfterDeferredCleanupAsync(Task deferredCleanup)
    {
        await ObserveCompletionAsync(deferredCleanup).ConfigureAwait(false);
        await ObserveCompletionAsync(Task.Run(async () =>
            await _onClosed(this).ConfigureAwait(false))).ConfigureAwait(false);
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Deferred teardown is best-effort; failures are observed and containment
            // continues in dependency order.
        }
    }

    private void PoisonSession()
    {
        if (Interlocked.Exchange(ref _poisoned, 1) != 0)
        {
            return;
        }

        lock (_admissionGate)
        {
            _queueCompleted = true;
        }

        _queueSignal.Release();
        _shutdown.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Automatic fail-closed teardown is observed through the shared disposal task.
            }
        });
    }

    private void CloseAfterLedgerTerminal(MvvmRequestId requestId)
    {
        bool closesSession;
        lock (_admissionGate)
        {
            closesSession = _ledgerTerminalRequestId == requestId;
            if (closesSession)
            {
                _ledgerTerminalRequestId = null;
            }
        }

        if (closesSession)
        {
            PoisonSession();
        }
    }

    private static void ObserveLateConsumerTask(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

        MvvmBindingResult? result = await InvokeConsumerAsync(
            () => _adapter.DispatchAsync(request, cancellationToken),
            pending).ConfigureAwait(false);
        CancellationCause cause = pending.Cause;
        if (result is null)
        {
            return CancellationResponse(request.RequestId, pending);
        }

        if (result.Committed && !ValidatePatches(result.Patches))
        {
            PoisonSession();
            return MvvmResponse.Rejected(
                request.RequestId,
                revision,
                MvvmFaultCodes.RequestInvalid,
                "The adapter produced an invalid projection.");
        }

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
        MvvmSnapshot? snapshot = await InvokeConsumerAsync(
            () => _adapter.SnapshotAsync(cancellationToken),
            pending).ConfigureAwait(false);
        CancellationCause cause = pending.Cause;
        if (cause != CancellationCause.Completed)
        {
            return CancellationResponse(request.RequestId, pending);
        }

        if (snapshot is null)
        {
            return MvvmResponse.Rejected(request.RequestId, Revision, ClosedFault);
        }

        if (!ValidateSnapshot(snapshot))
        {
            PoisonSession();
            return MvvmResponse.Rejected(
                request.RequestId,
                Revision,
                MvvmFaultCodes.RequestInvalid,
                "The adapter produced an invalid projection.");
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

    private bool ValidateSnapshot(MvvmSnapshot snapshot)
    {
        if (_vocabulary is null)
        {
            return true;
        }

        try
        {
            MvvmProjectionValidator.ValidateSnapshot(snapshot, _vocabulary);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool ValidatePatches(IReadOnlyList<MvvmPatch> patches)
    {
        if (_vocabulary is null)
        {
            return true;
        }

        try
        {
            MvvmProjectionValidator.ValidatePatches(patches, _vocabulary);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
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

    private static string RequestKind(MvvmRequest request) => request switch
    {
        MvvmMutationRequest { Kind: MvvmMutationKind.SetProperty } => "setProperty",
        MvvmMutationRequest { Kind: MvvmMutationKind.ExecuteCommand } => "execute",
        MvvmSnapshotRequest => "requestSnapshot",
        MvvmAcknowledgeRequest => "ack",
        MvvmCancelRequest => "cancel",
        _ => "invalid",
    };

    private static string? BackpressureLimit(MvvmResponse response) => response.Fault switch
    {
        { Code: MvvmFaultCodes.LimitExceeded, Message: "The session pending-request limit was exceeded." } => "requests",
        { Code: MvvmFaultCodes.LimitExceeded, Message: "The session cancellation-control limit was exceeded." } => "cancellation-control",
        { Code: MvvmFaultCodes.LimitExceeded, Message: "The session request ledger limit was exceeded; open a new session." } => "request-ledger",
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
                        Encoding.UTF8.GetByteCount(property.Name) > _limits.MaxPropertyNameBytes ||
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

    private sealed class QueuedRequest
    {
        private int _state;

        internal QueuedRequest(MvvmRequest request, PendingRequest pending)
        {
            Request = request;
            Pending = pending;
        }

        internal MvvmRequest Request { get; }

        internal PendingRequest Pending { get; }

        internal LinkedListNode<QueuedRequest>? Node { get; set; }

        internal TaskCompletionSource<MvvmResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool TryStart() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        internal bool IsCancelledBeforeStart => Volatile.Read(ref _state) == 2;

        internal void CancelBeforeStart() => Interlocked.CompareExchange(ref _state, 2, 0);
    }

    private sealed class PendingRequest : IDisposable
    {
        private readonly CancellationTokenSource _combined = new();
        private readonly CancellationTokenSource _timeout = new();
        private readonly CancellationTokenRegistration _callerRegistration;
        private readonly CancellationTokenRegistration _shutdownRegistration;
        private readonly CancellationTokenRegistration _timeoutRegistration;
        private readonly object _terminalGate = new();
        private readonly TaskCompletionSource<CancellationCause> _terminal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<long> _publication = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? _cancellationCallback;
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

        internal Task<CancellationCause> Terminal => _terminal.Task;

        internal bool Cancel() => Signal(CancellationCause.Explicit);

        internal bool SetCancellationCallback(Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            lock (_terminalGate)
            {
                _cancellationCallback = callback;
                return _cause is (int)CancellationCause.Caller or
                    (int)CancellationCause.Explicit or
                    (int)CancellationCause.Timeout or
                    (int)CancellationCause.Shutdown;
            }
        }

        internal CancellationCause Complete()
        {
            CancellationCause cause;
            lock (_terminalGate)
            {
                if (_cause == (int)CancellationCause.None)
                {
                    _cause = (int)CancellationCause.Completed;
                }

                cause = (CancellationCause)_cause;
            }

            _terminal.TrySetResult(cause);
            return cause;
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
            Action? cancellationCallback;
            lock (_terminalGate)
            {
                if (_cause != (int)CancellationCause.None)
                {
                    return false;
                }

                _cause = (int)cause;
                cancellationCallback = _cancellationCallback;
            }

            _terminal.TrySetResult(cause);
            try
            {
                cancellationCallback?.Invoke();
            }
            catch (Exception)
            {
                // Session-owned cancellation publication is fail-closed and must not
                // allow an observer callback to escape the protocol boundary.
            }

            _ = Task.Run(async () => await CancelSafelyAsync(_combined).ConfigureAwait(false));
            return true;
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
