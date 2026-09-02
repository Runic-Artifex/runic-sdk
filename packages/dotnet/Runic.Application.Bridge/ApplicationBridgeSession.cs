using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;

namespace Runic.Application.Bridge;

/// <summary>Owns one application session, its revisions, event order, command ledger, and operations.</summary>
public sealed class ApplicationBridgeSession : IAsyncDisposable, IBridgeEventPublisher, IBridgeOperationFactory
{
    private readonly IApplicationBridgeDispatcher _dispatcher;
    private readonly BridgeLimits _limits;
    private readonly SemaphoreSlim _admission;
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly AsyncLocal<DispatchTransaction?> _currentDispatch = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<BridgeOperationId, OperationRegistration> _operations = new();
    private readonly HashSet<BridgeCommandId> _commandLedger = [];
    private readonly object _gate = new();
    private readonly Channel<PendingEvent> _events;
    private readonly Task _eventPump;
    private long _revision;
    private long _sequence;
    private long _connectionEpoch;
    private int _queuedEvents;
    private int _reservedEvents;
    private int _disposed;

    /// <summary>Creates an isolated logical application session.</summary>
    public ApplicationBridgeSession(IApplicationBridgeDispatcher dispatcher, BridgeLimits? limits = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ReportDevelopmentFingerprint(dispatcher.ManifestFingerprint);
        _limits = limits ?? BridgeLimits.Default;
        _limits.Validate();
        _admission = new SemaphoreSlim(_limits.MaxPendingCommands, _limits.MaxPendingCommands);
        _events = Channel.CreateUnbounded<PendingEvent>(new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
        Id = BridgeSessionId.New();
        _eventPump = PumpEventsAsync();
    }

    private static void ReportDevelopmentFingerprint(string fingerprint)
    {
        string? path = Environment.GetEnvironmentVariable("RUNIC_APPLICATION_BRIDGE_HOST_READY");
        if (string.IsNullOrWhiteSpace(path)) return;
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) return;
        string temporary = path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, fingerprint + "\n");
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException)
        {
            // Development coordination must not affect application semantics.
        }
        catch (UnauthorizedAccessException)
        {
            // Development coordination must not affect application semantics.
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Raised after a committed domain event is accepted into the event stream.</summary>
    public event EventHandler<BridgeHostEnvelope>? EventProduced;
    /// <summary>Gets the logical session identifier.</summary>
    public BridgeSessionId Id { get; }
    /// <summary>Gets the authoritative application revision.</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>Dispatches one validated client envelope.</summary>
    public async ValueTask<BridgeHostEnvelope> DispatchAsync(BridgeClientEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ThrowIfDisposed();
        var commandId = new BridgeCommandId(envelope.CommandId);
        bool isInitializeAtCurrentOrFutureEpoch = envelope.Kind == "initialize" && envelope.ConnectionEpoch >= Interlocked.Read(ref _connectionEpoch);
        bool dispatchGateHeld = false;
        bool admissionHeld = false;
        DispatchTransaction? transaction = null;
        BridgeEventPayload[] stagedEvents = [];
        int eventBudget = 0;
        bool eventBudgetConsumed = false;
        try
        {
            // Pending capacity is reserved before any ledger mutation. A
            // rejected caller therefore cannot poison a retryable command id.
            if (!await _admission.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return AdmissionError(commandId, "The pending command limit was exceeded.", connectionEpoch: isInitializeAtCurrentOrFutureEpoch ? envelope.ConnectionEpoch : null);
            admissionHeld = true;
            // A reconnect must follow every old handler and external publish.
            await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            dispatchGateHeld = true;
            if (!MatchesContract(envelope))
                return isInitializeAtCurrentOrFutureEpoch
                    ? AdmissionError(commandId, "The Application Bridge protocol is incompatible.", "ProtocolVersionMismatch", false, envelope.ConnectionEpoch)
                    : Error(commandId, "ProtocolVersionMismatch", "The Application Bridge protocol is incompatible.");

            Admission admission;
            lock (_gate)
            {
                if (envelope.Kind != "initialize" && envelope.SessionId != Id.Value)
                    return ErrorLocked(commandId, "CommandRejected", "The command belongs to a stale session.");
                if (_commandLedger.Contains(commandId))
                    return isInitializeAtCurrentOrFutureEpoch ? AdmissionError(commandId, "The command identifier has already been processed.", connectionEpoch: envelope.ConnectionEpoch) : ErrorLocked(commandId, "CommandRejected", "The command identifier has already been processed.");
                if (_commandLedger.Count >= _limits.MaxCommandLedgerEntries)
                    return isInitializeAtCurrentOrFutureEpoch ? AdmissionError(commandId, "The command ledger is full; establish a new application session.", connectionEpoch: envelope.ConnectionEpoch) : ErrorLocked(commandId, "CommandRejected", "The command ledger is full; establish a new application session.");
                if (envelope.Kind == "initialize" && envelope.ConnectionEpoch < _connectionEpoch)
                    return ErrorLocked(commandId, "CommandRejected", "The initialization request belongs to an older connection.", true);
                if (envelope.Kind != "initialize" && envelope.ConnectionEpoch != _connectionEpoch)
                    return ErrorLocked(commandId, "CommandRejected", "The command belongs to a stale connection.", true);
                long revision = _revision;
                if (envelope.ExpectedRevision is long expected && expected != revision)
                    return isInitializeAtCurrentOrFutureEpoch
                        ? AdmissionError(commandId, "The command was based on a stale application revision.", "StaleRevision", true, envelope.ConnectionEpoch)
                        : ErrorLocked(commandId, "StaleRevision", "The command was based on a stale application revision.", true);
                int budget = IsHandlerCommand(envelope.Kind) ? _limits.MaxPendingCommands : 0;
                if (_queuedEvents + _reservedEvents + budget > _limits.MaxPendingCommands)
                    return envelope.Kind == "initialize"
                        ? AdmissionError(commandId, "The event buffer is full; retry after queued events are processed.", connectionEpoch: envelope.ConnectionEpoch)
                        : ErrorLocked(commandId, "CommandRejected", "The event buffer is full; retry after queued events are processed.", true);
                _reservedEvents += budget;
                _commandLedger.Add(commandId);
                eventBudget = budget;
                admission = new(revision, envelope.Kind == "initialize" && envelope.ConnectionEpoch > _connectionEpoch, budget);
            }

            if (envelope.Kind == "cancelOperation") return Cancel(envelope, commandId);
            if (envelope.Kind is "uiReady" or "uiRendered")
                return Receipt(commandId, EmptyTagged(envelope.Kind == "uiReady" ? "UiReadyAccepted" : "UiRenderedAccepted"));

            BridgeDispatchResult result;
            transaction = new DispatchTransaction(eventBudget);
            try
            {
                _currentDispatch.Value = transaction;
                var context = new BridgeCommandContext(Id, commandId, envelope.Kind == "initialize", envelope.ExpectedRevision, admission.Revision, this, this);
                result = await _dispatcher.DispatchAsync(envelope.Payload, context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _currentDispatch.Value = null;
                stagedEvents = transaction.Seal();
            }

            string kind = envelope.Kind == "initialize" ? "snapshot" : "receipt";
            JsonElement payload = envelope.Kind == "initialize" && result.Receipt.TryGetProperty("snapshot", out JsonElement snapshot) ? snapshot : result.Receipt;
            if (result.Cancellable && result.OperationId is null)
                throw new InvalidOperationException("A cancellable dispatch result must identify its operation.");
            if (result.OperationId is BridgeOperationId operationId &&
                _operations.TryGetValue(operationId, out OperationRegistration? operation))
            {
                operation.SetCancellable(result.Cancellable);
            }
            BridgeHostEnvelope? committed = CommitTransaction(envelope, admission, stagedEvents, result.AdvancesRevision, kind, commandId, payload, result.OperationId);
            eventBudgetConsumed = committed is not null;
            if (committed is null)
                return isInitializeAtCurrentOrFutureEpoch ? AdmissionError(commandId, "The initialization could not be committed.", connectionEpoch: envelope.ConnectionEpoch) : Error(commandId, "StaleRevision", "The command completed after the application revision changed.", true);
            // Completion is bounded queue acceptance, never subscriber delivery.
            return committed;
        }
        catch (BridgeCommandFailureException exception)
        {
            try
            {
                JsonElement error = _dispatcher.ValidateError(exception.Error);
                return isInitializeAtCurrentOrFutureEpoch
                    ? AdmissionError(commandId, error, envelope.ConnectionEpoch)
                    : Error(commandId, error);
            }
            catch (Exception)
            {
                return isInitializeAtCurrentOrFutureEpoch
                    ? AdmissionError(commandId, "The command handler returned an undeclared error.", connectionEpoch: envelope.ConnectionEpoch)
                    : Error(commandId, "CommandRejected", "The command handler returned an undeclared error.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (isInitializeAtCurrentOrFutureEpoch) return AdmissionError(commandId, "The command was cancelled.", "OperationCancelled", true, envelope.ConnectionEpoch);
            if (!dispatchGateHeld) return AdmissionError(commandId, "The command was cancelled.", "OperationCancelled", false);
            return Error(commandId, "OperationCancelled", "The command was cancelled.", true);
        }
        catch (Exception)
        {
            return isInitializeAtCurrentOrFutureEpoch
                ? AdmissionError(commandId, "The command handler failed.", connectionEpoch: envelope.ConnectionEpoch)
                : Error(commandId, "CommandRejected", "The command handler failed.");
        }
        finally
        {
            if (eventBudget != 0 && !eventBudgetConsumed) ReleaseEventBudget(eventBudget);
            if (admissionHeld) _admission.Release();
            if (dispatchGateHeld) _dispatchGate.Release();
        }
    }

    /// <inheritdoc />
    public BridgeOperationId Start(Func<BridgeOperationId, CancellationToken, ValueTask> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();
        var id = BridgeOperationId.New();
        var owned = new OperationRegistration(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token));
        if (!_operations.TryAdd(id, owned)) { owned.Dispose(); throw new InvalidOperationException("The operation identifier could not be reserved."); }
        DispatchTransaction? current = _currentDispatch.Value;
        _currentDispatch.Value = null;
        try { _ = RunOperationAsync(id, operation, owned); }
        finally { _currentDispatch.Value = current; }
        return id;
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(BridgeEventPayload eventPayload, CancellationToken cancellationToken = default)
    {
        DispatchTransaction? transaction = _currentDispatch.Value;
        if (transaction is not null)
        {
            transaction.Stage(eventPayload);
            return ValueTask.CompletedTask;
        }
        return PublishExternalAsync(eventPayload, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        foreach (OperationRegistration operation in _operations.Values) operation.Cancel();
        long deadline = Environment.TickCount64 + (long)_limits.ShutdownTimeout.TotalMilliseconds;
        while (!_operations.IsEmpty && Environment.TickCount64 < deadline) await Task.Delay(10).ConfigureAwait(false);
        lock (_gate) _events.Writer.TryComplete();
        await _eventPump.ConfigureAwait(false);
        _admission.Dispose();
        _shutdown.Dispose();
    }

    private bool MatchesContract(BridgeClientEnvelope envelope) => string.Equals(envelope.Protocol, _dispatcher.ProtocolIdentity, StringComparison.Ordinal) && envelope.Version == _dispatcher.ProtocolVersion && string.Equals(envelope.ContractFingerprint, _dispatcher.ManifestFingerprint, StringComparison.Ordinal);
    private static bool IsHandlerCommand(string kind) => kind is "initialize" or "dispatch";

    // Sequence zero is reserved for local admission refusal: no session state,
    // ledger entry, or host sequence was consumed. Browser runtimes recognize
    // it only for a correlated error and leave authoritative state untouched.
    private BridgeHostEnvelope AdmissionError(BridgeCommandId commandId, string message, string tag = "CommandRejected", bool retryable = true, long? connectionEpoch = null) => new()
    {
        Protocol = _dispatcher.ProtocolIdentity,
        Version = _dispatcher.ProtocolVersion,
        ContractFingerprint = _dispatcher.ManifestFingerprint,
        ConnectionEpoch = connectionEpoch ?? Interlocked.Read(ref _connectionEpoch),
        Kind = "error",
        SessionId = Id.Value,
        Sequence = 0,
        Revision = Interlocked.Read(ref _revision),
        CommandId = commandId.Value,
        Payload = JsonSerializer.SerializeToElement(new BridgePublicError(tag, message, retryable), ApplicationBridgeJsonContext.Default.BridgePublicError),
    };

    private BridgeHostEnvelope AdmissionError(BridgeCommandId commandId, JsonElement payload, long connectionEpoch) => new()
    {
        Protocol = _dispatcher.ProtocolIdentity,
        Version = _dispatcher.ProtocolVersion,
        ContractFingerprint = _dispatcher.ManifestFingerprint,
        ConnectionEpoch = connectionEpoch,
        Kind = "error",
        SessionId = Id.Value,
        Sequence = 0,
        Revision = Interlocked.Read(ref _revision),
        CommandId = commandId.Value,
        Payload = payload.Clone(),
    };

    private BridgeHostEnvelope BuildEnvelope(string kind, BridgeCommandId? commandId, JsonElement payload, BridgeOperationId? operationId, long epoch, long sequence, long revision) => new()
    {
        Protocol = _dispatcher.ProtocolIdentity, Version = _dispatcher.ProtocolVersion, ContractFingerprint = _dispatcher.ManifestFingerprint, ConnectionEpoch = epoch,
        Kind = kind, SessionId = Id.Value, Sequence = sequence, Revision = revision, CommandId = commandId?.Value, OperationId = operationId?.Value, Payload = payload.Clone(),
    };

    private BridgeHostEnvelope? CommitTransaction(BridgeClientEnvelope envelope, Admission admission, BridgeEventPayload[] stagedEvents, bool advancesRevision, string kind, BridgeCommandId commandId, JsonElement payload, BridgeOperationId? operationId)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _revision != admission.Revision || _reservedEvents < admission.EventBudget)
                return null;
            if (admission.ResetsEpoch)
            {
                _connectionEpoch = envelope.ConnectionEpoch;
                _sequence = 0;
            }
            else if (envelope.ConnectionEpoch != _connectionEpoch) return null;

            long finalRevision = _revision + (advancesRevision ? 1 : 0);
            foreach (BridgeEventPayload eventPayload in stagedEvents)
                if (eventPayload.AdvancesRevision) finalRevision++;
            long firstSequence = _sequence + 1;
            long epoch = admission.ResetsEpoch ? envelope.ConnectionEpoch : _connectionEpoch;
            JsonElement responsePayload = payload.Clone();
            BridgeHostEnvelope response = BuildEnvelope(kind, commandId, responsePayload, operationId, epoch, firstSequence, finalRevision);
            var pending = new PendingEvent[stagedEvents.Length];
            for (int index = 0; index < stagedEvents.Length; index++)
            {
                BridgeEventPayload item = stagedEvents[index];
                pending[index] = new(BuildEnvelope("event", null, item.Payload, item.OperationId, epoch, firstSequence + index + 1, finalRevision));
            }
            _revision = finalRevision;
            _sequence = firstSequence + stagedEvents.Length;
            if (admission.ResetsEpoch) _connectionEpoch = epoch;
            _reservedEvents -= admission.EventBudget;
            foreach (PendingEvent item in pending)
            {
                if (!_events.Writer.TryWrite(item)) throw new InvalidOperationException("The Application Bridge event buffer is unavailable.");
                _queuedEvents++;
            }
            return response;
        }
    }

    private ValueTask PublishCoreAsync(BridgeEventPayload eventPayload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var stable = new BridgeEventPayload(eventPayload.Payload.Clone(), eventPayload.AdvancesRevision, eventPayload.OperationId);
        lock (_gate)
        {
            ThrowIfDisposed();
            ReserveEventSlotLocked();
            if (stable.AdvancesRevision) _revision++;
            EnqueueReservedLocked(new PendingEvent(CreateLocked("event", null, stable.Payload, stable.OperationId)));
        }
        return ValueTask.CompletedTask;
    }

    private async ValueTask PublishExternalAsync(BridgeEventPayload eventPayload, CancellationToken cancellationToken)
    {
        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await PublishCoreAsync(eventPayload, cancellationToken).ConfigureAwait(false); }
        finally { _dispatchGate.Release(); }
    }

    private void ReleaseEventBudget(int budget) { lock (_gate) _reservedEvents -= budget; }
    private void ReserveEventSlotLocked()
    {
        if (_queuedEvents + _reservedEvents >= _limits.MaxPendingCommands)
            throw new InvalidOperationException("The Application Bridge event buffer is full.");
        _reservedEvents++;
    }
    private void EnqueueReservedLocked(PendingEvent pending)
    {
        if (_reservedEvents <= 0 || !_events.Writer.TryWrite(pending))
            throw new InvalidOperationException("The Application Bridge event buffer is unavailable.");
        _reservedEvents--;
        _queuedEvents++;
    }

    private async Task PumpEventsAsync()
    {
        await foreach (PendingEvent pending in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            lock (_gate) _queuedEvents--;
            try { EventProduced?.Invoke(this, pending.Envelope); }
            catch { }
        }
    }

    private async Task RunOperationAsync(BridgeOperationId id, Func<BridgeOperationId, CancellationToken, ValueTask> operation, OperationRegistration registration)
    {
        CancellationToken cancellationToken = registration.Token;
        try { await operation(id, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { }
        finally { if (_operations.TryRemove(id, out OperationRegistration? owned)) owned.Dispose(); }
    }

    private BridgeHostEnvelope Cancel(BridgeClientEnvelope envelope, BridgeCommandId commandId)
    {
        if (!envelope.Payload.TryGetProperty("operationId", out JsonElement value) || !value.TryGetGuid(out Guid operationId)) return Error(commandId, "ProtocolDecodeError", "Cancellation requires an operation identifier.");
        bool accepted = _operations.TryGetValue(new BridgeOperationId(operationId), out OperationRegistration? operation)
            && operation.TryCancel();
        JsonElement payload = JsonSerializer.SerializeToElement(new BridgeCancellationReceipt("OperationCancellationAccepted", operationId, accepted, Revision), ApplicationBridgeJsonContext.Default.BridgeCancellationReceipt);
        return Receipt(commandId, payload, new BridgeOperationId(operationId));
    }

    private BridgeHostEnvelope Error(BridgeCommandId commandId, string tag, string message, bool retryable = false) { lock (_gate) return ErrorLocked(commandId, tag, message, retryable); }
    private BridgeHostEnvelope Error(BridgeCommandId commandId, JsonElement payload) => Create("error", commandId, payload);
    private BridgeHostEnvelope ErrorLocked(BridgeCommandId commandId, string tag, string message, bool retryable = false) => CreateLocked("error", commandId, JsonSerializer.SerializeToElement(new BridgePublicError(tag, message, retryable), ApplicationBridgeJsonContext.Default.BridgePublicError));
    private BridgeHostEnvelope Receipt(BridgeCommandId commandId, JsonElement payload, BridgeOperationId? operationId = null) => Create("receipt", commandId, payload, operationId);
    private BridgeHostEnvelope Create(string kind, BridgeCommandId? commandId, JsonElement payload, BridgeOperationId? operationId = null) { lock (_gate) return CreateLocked(kind, commandId, payload, operationId); }
    private BridgeHostEnvelope CreateLocked(string kind, BridgeCommandId? commandId, JsonElement payload, BridgeOperationId? operationId = null) => new()
    {
        Protocol = _dispatcher.ProtocolIdentity, Version = _dispatcher.ProtocolVersion, ContractFingerprint = _dispatcher.ManifestFingerprint, ConnectionEpoch = _connectionEpoch,
        Kind = kind, SessionId = Id.Value, Sequence = ++_sequence, Revision = _revision, CommandId = commandId?.Value, OperationId = operationId?.Value, Payload = payload.Clone(),
    };
    private static JsonElement EmptyTagged(string tag) => JsonSerializer.SerializeToElement(new BridgeSignalReceipt(tag), ApplicationBridgeJsonContext.Default.BridgeSignalReceipt);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class DispatchTransaction(int eventBudget)
    {
        private readonly object _gate = new();
        private readonly List<BridgeEventPayload> _events = [];
        private bool _sealed;
        public void Stage(BridgeEventPayload eventPayload)
        {
            lock (_gate)
            {
                if (_sealed) throw new InvalidOperationException("The command dispatch has already completed.");
                if (_events.Count >= eventBudget) throw new InvalidOperationException("The command exceeded its reserved event budget.");
                _events.Add(new(eventPayload.Payload.Clone(), eventPayload.AdvancesRevision, eventPayload.OperationId));
            }
        }
        public BridgeEventPayload[] Seal()
        {
            lock (_gate)
            {
                _sealed = true;
                return _events.ToArray();
            }
        }
    }

    private sealed class PendingEvent(BridgeHostEnvelope envelope) { public BridgeHostEnvelope Envelope { get; } = envelope; }
    private sealed class OperationRegistration(CancellationTokenSource source) : IDisposable
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _source = source;
        private bool _cancellable;
        public CancellationToken Token { get; } = source.Token;

        public void SetCancellable(bool cancellable)
        {
            lock (_gate) _cancellable = cancellable;
        }

        public bool TryCancel()
        {
            lock (_gate)
            {
                if (!_cancellable || _source is null) return false;
                _source.Cancel();
                return true;
            }
        }

        public void Cancel()
        {
            lock (_gate) _source?.Cancel();
        }

        public void Dispose()
        {
            CancellationTokenSource? owned;
            lock (_gate)
            {
                owned = _source;
                _source = null;
            }
            owned?.Dispose();
        }
    }
    private readonly record struct Admission(long Revision, bool ResetsEpoch, int EventBudget);
}
