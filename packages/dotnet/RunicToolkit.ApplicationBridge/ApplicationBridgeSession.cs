using System.Collections.Concurrent;
using System.Text.Json;

namespace RunicToolkit.ApplicationBridge;

/// <summary>
/// Owns one application session, its command ledger, revisions, event sequence,
/// and backend operations.
/// </summary>
public sealed class ApplicationBridgeSession : IAsyncDisposable, IBridgeEventPublisher, IBridgeOperationFactory
{
    private readonly IApplicationBridgeDispatcher _dispatcher;
    private readonly BridgeLimits _limits;
    private readonly SemaphoreSlim _admission;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<BridgeOperationId, CancellationTokenSource> _operations = new();
    private readonly HashSet<BridgeCommandId> _commandLedger = [];
    private readonly object _gate = new();
    private long _revision;
    private long _sequence;
    private int _disposed;

    /// <summary>Creates an isolated logical session.</summary>
    public ApplicationBridgeSession(IApplicationBridgeDispatcher dispatcher, BridgeLimits? limits = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _limits = limits ?? BridgeLimits.Default;
        _limits.Validate();
        _admission = new SemaphoreSlim(_limits.MaxPendingCommands, _limits.MaxPendingCommands);
        Id = BridgeSessionId.New();
    }

    /// <summary>Raised after a validated domain event receives its sequence and revision.</summary>
    public event EventHandler<BridgeHostEnvelope>? EventProduced;

    /// <summary>Gets this logical session identifier.</summary>
    public BridgeSessionId Id { get; }

    /// <summary>Gets the current authoritative revision.</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>Dispatches one validated envelope.</summary>
    public async ValueTask<BridgeHostEnvelope> DispatchAsync(
        BridgeClientEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ThrowIfDisposed();
        var commandId = new BridgeCommandId(envelope.CommandId);

        if (!string.Equals(envelope.Protocol, _dispatcher.ProtocolIdentity, StringComparison.Ordinal) ||
            envelope.Version != _dispatcher.ProtocolVersion)
        {
            return Error(commandId, "ProtocolVersionMismatch", "The Application Bridge protocol is incompatible.");
        }
        if (envelope.Kind != "initialize" && envelope.SessionId != Id.Value)
        {
            return Error(commandId, "CommandRejected", "The command belongs to a stale session.");
        }
        lock (_gate)
        {
            if (_commandLedger.Contains(commandId))
            {
                return Error(commandId, "CommandRejected", "The command identifier has already been processed.");
            }
            if (_commandLedger.Count >= _limits.MaxCommandLedgerEntries)
            {
                return Error(commandId, "CommandRejected", "The command ledger is full; establish a new application session.");
            }
            _commandLedger.Add(commandId);
        }
        if (envelope.ExpectedRevision is long expected && expected != Revision)
        {
            return Error(commandId, "StaleRevision", "The command was based on a stale application revision.", true);
        }

        if (!await _admission.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Error(commandId, "CommandRejected", "The pending command limit was exceeded.", true);
        }

        try
        {
            if (envelope.Kind == "cancelOperation")
            {
                return Cancel(envelope, commandId);
            }
            if (envelope.Kind is "uiReady" or "uiRendered")
            {
                return Receipt(commandId, EmptyTagged(envelope.Kind == "uiReady" ? "UiReadyAccepted" : "UiRenderedAccepted"));
            }

            var context = new BridgeCommandContext(
                Id,
                commandId,
                envelope.ExpectedRevision,
                Revision,
                this,
                this);
            BridgeDispatchResult result = await _dispatcher
                .DispatchAsync(envelope.Payload, context, cancellationToken)
                .ConfigureAwait(false);
            if (result.AdvancesRevision)
            {
                Interlocked.Increment(ref _revision);
            }
            string kind = envelope.Kind == "initialize" ? "snapshot" : "receipt";
            JsonElement payload = envelope.Kind == "initialize" &&
                result.Receipt.TryGetProperty("snapshot", out JsonElement snapshot)
                ? snapshot
                : result.Receipt;
            return Create(kind, commandId, payload, result.OperationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error(commandId, "OperationCancelled", "The command was cancelled.", true);
        }
        catch (Exception)
        {
            return Error(commandId, "CommandRejected", "The command handler failed.");
        }
        finally
        {
            _admission.Release();
        }
    }

    /// <inheritdoc />
    public BridgeOperationId Start(
        Func<BridgeOperationId, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();
        var id = BridgeOperationId.New();
        var owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        if (!_operations.TryAdd(id, owned))
        {
            owned.Dispose();
            throw new InvalidOperationException("The operation identifier could not be reserved.");
        }
        _ = RunOperationAsync(id, operation, owned);
        return id;
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(BridgeEventPayload eventPayload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (eventPayload.AdvancesRevision)
        {
            Interlocked.Increment(ref _revision);
        }
        EventProduced?.Invoke(this, Create("event", null, eventPayload.Payload, eventPayload.OperationId));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _shutdown.Cancel();
        foreach (CancellationTokenSource operation in _operations.Values)
        {
            operation.Cancel();
        }
        long deadline = Environment.TickCount64 + (long)_limits.ShutdownTimeout.TotalMilliseconds;
        while (!_operations.IsEmpty && Environment.TickCount64 < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
        foreach (CancellationTokenSource operation in _operations.Values)
        {
            operation.Dispose();
        }
        _operations.Clear();
        _admission.Dispose();
        _shutdown.Dispose();
    }

    private async Task RunOperationAsync(
        BridgeOperationId id,
        Func<BridgeOperationId, CancellationToken, ValueTask> operation,
        CancellationTokenSource cancellation)
    {
        try
        {
            await operation(id, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The application publishes its domain cancellation event at a safe point.
        }
        catch (Exception)
        {
            // Private diagnostics belong to the host. Public failure is emitted by the handler.
        }
        finally
        {
            if (_operations.TryRemove(id, out CancellationTokenSource? owned))
            {
                owned.Dispose();
            }
        }
    }

    private BridgeHostEnvelope Cancel(BridgeClientEnvelope envelope, BridgeCommandId commandId)
    {
        if (!envelope.Payload.TryGetProperty("operationId", out JsonElement value) ||
            !value.TryGetGuid(out Guid operationId))
        {
            return Error(commandId, "ProtocolDecodeError", "Cancellation requires an operation identifier.");
        }
        bool accepted = _operations.TryGetValue(new BridgeOperationId(operationId), out CancellationTokenSource? operation);
        operation?.Cancel();
        JsonElement payload = JsonSerializer.SerializeToElement(
            new BridgeCancellationReceipt(
                "OperationCancellationAccepted",
                operationId,
                accepted,
                Revision),
            ApplicationBridgeJsonContext.Default.BridgeCancellationReceipt);
        return Receipt(commandId, payload, new BridgeOperationId(operationId));
    }

    private BridgeHostEnvelope Error(BridgeCommandId commandId, string tag, string message, bool retryable = false) =>
        Create(
            "error",
            commandId,
            JsonSerializer.SerializeToElement(
                new BridgePublicError(tag, message, retryable),
                ApplicationBridgeJsonContext.Default.BridgePublicError));

    private BridgeHostEnvelope Receipt(
        BridgeCommandId commandId,
        JsonElement payload,
        BridgeOperationId? operationId = null) =>
        Create("receipt", commandId, payload, operationId);

    private BridgeHostEnvelope Create(
        string kind,
        BridgeCommandId? commandId,
        JsonElement payload,
        BridgeOperationId? operationId = null) =>
        new()
        {
            Protocol = _dispatcher.ProtocolIdentity,
            Version = _dispatcher.ProtocolVersion,
            Kind = kind,
            SessionId = Id.Value,
            Sequence = Interlocked.Increment(ref _sequence),
            Revision = Revision,
            CommandId = commandId?.Value,
            OperationId = operationId?.Value,
            Payload = payload.Clone(),
        };

    private static JsonElement EmptyTagged(string tag) =>
        JsonSerializer.SerializeToElement(
            new BridgeSignalReceipt(tag),
            ApplicationBridgeJsonContext.Default.BridgeSignalReceipt);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
