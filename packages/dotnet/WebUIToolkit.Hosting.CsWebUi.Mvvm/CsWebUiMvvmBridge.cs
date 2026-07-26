using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using WebUIToolkit.Hosting.WebUi;
using WebUIToolkit.MVVM;

namespace WebUIToolkit.Hosting.CsWebUi.Mvvm;

/// <summary>
/// Connects one retained MVVM session to one CsWebUi window through a single binary binding.
/// </summary>
/// <remarks>
/// A successfully attached bridge owns the session. Dispose the bridge before disposing its
/// <see cref="WebUiWindow"/>. Protocol close also tears down the owned session deterministically.
/// </remarks>
public sealed class CsWebUiMvvmBridge : IAsyncDisposable
{
    private readonly ICsWebUiMvvmWindow _window;
    private readonly IMvvmSession _session;
    private readonly CsWebUiMvvmBridgeOptions _options;
    private readonly MvvmLimits _limits;
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private IDisposable? _binding;
    private MvvmWebUiTransport? _transport;
    private MvvmTransportRoute? _route;
    private byte[]? _acceptedHandshake;
    private string[] _capabilities = [];
    private Guid? _viewId;
    private ulong? _clientId;
    private ulong? _connectionId;
    private int _closed;
    private int _disposed;
    private int _sessionDisposed;

    private CsWebUiMvvmBridge(
        ICsWebUiMvvmWindow window,
        IMvvmSession session,
        CsWebUiMvvmBridgeOptions options)
    {
        _window = window;
        _session = session;
        _options = options;
        _limits = options.TransportOptions.CodecLimits;
        _binding = _window.Bind(options.BindingName, OnFrameAsync);
        _session.ProjectionChanged += OnProjectionChanged;
    }

    /// <summary>Gets the pinned native identity after the first valid handshake.</summary>
    public CsWebUiMvvmConnectionIdentity? ConnectionIdentity =>
        _clientId is ulong client && _connectionId is ulong connection
            ? new CsWebUiMvvmConnectionIdentity(client, connection)
            : null;

    /// <summary>Gets whether protocol close or bridge disposal has stopped this channel.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// Registers the bridge binding and transfers ownership of <paramref name="session"/>.
    /// </summary>
    public static CsWebUiMvvmBridge Attach(
        WebUiWindow window,
        IMvvmSession session,
        CsWebUiMvvmBridgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        return Attach(new CsWebUiMvvmWindow(window), session, options);
    }

    internal static CsWebUiMvvmBridge Attach(
        ICsWebUiMvvmWindow window,
        IMvvmSession session,
        CsWebUiMvvmBridgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(session);
        CsWebUiMvvmBridgeOptions selected = options ?? new CsWebUiMvvmBridgeOptions();
        selected.Validate();
        return new CsWebUiMvvmBridge(window, session, selected);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _closed, 1);
        _session.ProjectionChanged -= OnProjectionChanged;
        _binding?.Dispose();
        _binding = null;
        _shutdown.Cancel();
        await _dispatchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            _dispatchGate.Release();
            _shutdown.Dispose();
            _dispatchGate.Dispose();
        }
    }

    private async ValueTask OnFrameAsync(
        ICsWebUiMvvmEvent webUiEvent,
        CancellationToken cancellationToken)
    {
        if (IsClosed)
        {
            webUiEvent.CloseClient();
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        await _dispatchGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (IsClosed)
            {
                webUiEvent.CloseClient();
                return;
            }

            if (webUiEvent.ArgumentCount != 1)
            {
                webUiEvent.CloseClient();
                return;
            }

            byte[] frame = webUiEvent.GetBytes(0);
            if (!MvvmMessageCodec.TryDecodeClient(
                frame,
                out MvvmWireMessage? message,
                out _,
                _limits))
            {
                webUiEvent.CloseClient();
                return;
            }

            if (!AcceptConnection(webUiEvent, message!))
            {
                webUiEvent.CloseClient();
                return;
            }

            if (message!.Kind == "handshake")
            {
                HandleHandshake(webUiEvent, message, frame);
                return;
            }

            if (_acceptedHandshake is null)
            {
                SendPreSessionFault(
                    webUiEvent,
                    RequestId(message),
                    MvvmFaultCodes.RequestInvalid,
                    "A handshake is required before this request.");
                return;
            }

            if (message.Kind == "open")
            {
                await HandleOpenAsync(webUiEvent, message, linked.Token).ConfigureAwait(false);
                return;
            }

            if (_transport is null || _route is null || _viewId is null)
            {
                SendPreSessionFault(
                    webUiEvent,
                    RequestId(message),
                    MvvmFaultCodes.RequestInvalid,
                    "The MVVM session is not open.");
                return;
            }

            if (!HasRoute(message))
            {
                webUiEvent.CloseClient();
                return;
            }

            if (message.Kind == "close")
            {
                await HandleCloseAsync(webUiEvent, message).ConfigureAwait(false);
                return;
            }

            await HandleSessionRequestAsync(webUiEvent, message, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Window or bridge teardown owns the callback cancellation.
        }
        catch (Exception)
        {
            webUiEvent.CloseClient();
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    private bool AcceptConnection(ICsWebUiMvvmEvent webUiEvent, MvvmWireMessage message)
    {
        if (_clientId is null)
        {
            if (message.Kind != "handshake")
            {
                return false;
            }

            _clientId = webUiEvent.ClientId;
            _connectionId = webUiEvent.ConnectionId;
            return true;
        }

        if (_clientId != webUiEvent.ClientId)
        {
            return false;
        }

        if (_connectionId == webUiEvent.ConnectionId)
        {
            return true;
        }

        if (message.Kind != "handshake")
        {
            return false;
        }

        _connectionId = webUiEvent.ConnectionId;
        return true;
    }

    private void HandleHandshake(
        ICsWebUiMvvmEvent webUiEvent,
        MvvmWireMessage message,
        byte[] frame)
    {
        if (_transport is not null)
        {
            if (_route is not MvvmTransportRoute route ||
                !_transport.BeginReconnect(route) ||
                !_transport.TryAcceptHandshake(frame, out byte[]? canonical))
            {
                webUiEvent.CloseClient();
                return;
            }

            _acceptedHandshake = canonical;
        }
        else
        {
            _acceptedHandshake = MvvmMessageCodec.Encode(message, _limits);
        }

        _capabilities = message.Payload
            .GetProperty("capabilities")
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToArray();
        Send(
            webUiEvent,
            MvvmHostFrameEncoder.HandshakeResult(
                RequestId(message),
                _capabilities,
                _limits));
    }

    private async ValueTask HandleOpenAsync(
        ICsWebUiMvvmEvent webUiEvent,
        MvvmWireMessage message,
        CancellationToken cancellationToken)
    {
        Guid requestId = RequestId(message);
        if (_transport is not null ||
            !string.Equals(
                message.Document.GetProperty("contract").GetString(),
                _session.Contract.Value,
                StringComparison.Ordinal))
        {
            SendPreSessionFault(
                webUiEvent,
                requestId,
                MvvmFaultCodes.RequestInvalid,
                "The requested MVVM contract cannot be opened.");
            return;
        }

        Guid viewId = message.Document.GetProperty("view").GetGuid();
        var transport = new MvvmWebUiTransport(_session, viewId, _options.TransportOptions);
        if (!transport.TryAcceptHandshake(_acceptedHandshake!, out _))
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _closed, 1);
            webUiEvent.CloseClient();
            return;
        }

        var route = new MvvmTransportRoute(
            _session.Id,
            viewId,
            _session.CapabilityToken);
        _transport = transport;
        _route = route;
        _viewId = viewId;

        MvvmTransportDispatchResult result = await transport
            .RecoverFromSnapshotAsync(
                route,
                new MvvmRequestId(requestId),
                cancellationToken)
            .ConfigureAwait(false);
        _ = transport.DrainOutput();
        if (!result.WasDispatched ||
            result.Response is not { Succeeded: true, Payload: JsonElement snapshot })
        {
            SendPreSessionFault(
                webUiEvent,
                requestId,
                MvvmFaultCodes.RequestInvalid,
                "The initial MVVM snapshot could not be created.");
            return;
        }

        Send(
            webUiEvent,
            MvvmHostFrameEncoder.Opened(
                _session,
                viewId,
                requestId,
                snapshot,
                _limits));
    }

    private async ValueTask HandleSessionRequestAsync(
        ICsWebUiMvvmEvent webUiEvent,
        MvvmWireMessage message,
        CancellationToken cancellationToken)
    {
        MvvmRequest request = CreateRequest(message);
        MvvmTransportDispatchResult result = request is MvvmSnapshotRequest
            ? await _transport!.RecoverFromSnapshotAsync(
                _route!.Value,
                request.RequestId,
                cancellationToken).ConfigureAwait(false)
            : await _transport!.DispatchAsync(
                _route!.Value,
                request,
                cancellationToken).ConfigureAwait(false);

        if (!result.WasDispatched)
        {
            if (result.Rejection == MvvmTransportRejection.AuthenticationFailed)
            {
                webUiEvent.CloseClient();
                return;
            }

            SendTransportRejection(webUiEvent, request, result.Rejection);
            return;
        }

        IReadOnlyList<MvvmTransportFrame> output = _transport.DrainOutput();
        foreach (MvvmTransportFrame outputFrame in output)
        {
            if (outputFrame.Kind == MvvmTransportFrameKind.Patch)
            {
                Send(
                    webUiEvent,
                    MvvmHostFrameEncoder.Patch(
                        _session,
                        _viewId!.Value,
                        outputFrame.FromRevision,
                        outputFrame.Response,
                        _limits));
                continue;
            }

            SendTerminal(webUiEvent, request, outputFrame.Response);
        }
    }

    private async ValueTask HandleCloseAsync(
        ICsWebUiMvvmEvent webUiEvent,
        MvvmWireMessage message)
    {
        string reason = message.Payload.TryGetProperty("reason", out JsonElement value)
            ? value.GetString()!
            : "Client closed the MVVM session.";
        Send(
            webUiEvent,
            MvvmHostFrameEncoder.Closed(
                _session,
                _viewId!.Value,
                RequestId(message),
                reason,
                _limits));
        Interlocked.Exchange(ref _closed, 1);
        _binding?.Dispose();
        _binding = null;
        _shutdown.Cancel();
        await DisposeSessionAsync().ConfigureAwait(false);
    }

    private void SendTerminal(
        ICsWebUiMvvmEvent webUiEvent,
        MvvmRequest request,
        MvvmResponse response)
    {
        if (response.Fault is MvvmFault fault)
        {
            Send(
                webUiEvent,
                MvvmHostFrameEncoder.Fault(
                    _session,
                    _viewId,
                    request.RequestId.Value,
                    fault.Code,
                    fault.Message,
                    response.Revision,
                    _limits));
            return;
        }

        if (request is MvvmSnapshotRequest &&
            response.Payload is JsonElement snapshot)
        {
            Send(
                webUiEvent,
                MvvmHostFrameEncoder.Snapshot(
                    _session,
                    _viewId!.Value,
                    request.RequestId,
                    snapshot,
                    _limits));
            return;
        }

        Send(
            webUiEvent,
            MvvmHostFrameEncoder.Result(
                _session,
                _viewId!.Value,
                request,
                response,
                _limits));
    }

    private void SendTransportRejection(
        ICsWebUiMvvmEvent webUiEvent,
        MvvmRequest request,
        MvvmTransportRejection rejection)
    {
        (string Code, string Message, long? Revision) fault = rejection switch
        {
            MvvmTransportRejection.OutputLimitExceeded =>
                (MvvmFaultCodes.LimitExceeded, "The transport output limit was exceeded.", null),
            MvvmTransportRejection.SnapshotRequired =>
                (MvvmFaultCodes.RevisionStale, "An authoritative snapshot is required.", _session.Revision),
            MvvmTransportRejection.SessionClosed =>
                (MvvmFaultCodes.SessionClosed, "The MVVM session is closed.", null),
            _ => (MvvmFaultCodes.RequestInvalid, "The request is not valid for this connection.", null),
        };
        Send(
            webUiEvent,
            MvvmHostFrameEncoder.Fault(
                _session,
                _viewId,
                request.RequestId.Value,
                fault.Code,
                fault.Message,
                fault.Revision,
                _limits));
    }

    private void SendPreSessionFault(
        ICsWebUiMvvmEvent webUiEvent,
        Guid request,
        string code,
        string message) =>
        Send(
            webUiEvent,
            MvvmHostFrameEncoder.Fault(
                null,
                null,
                request,
                code,
                message,
                null,
                _limits));

    private void Send(ICsWebUiMvvmEvent webUiEvent, byte[] frame)
    {
        if (frame.Length > _limits.MaxPayloadBytes)
        {
            throw new InvalidOperationException("An encoded host frame exceeded the configured limit.");
        }

        webUiEvent.SendRaw(_options.ReceiveFunctionName, frame);
    }

    private void OnProjectionChanged(object? sender, MvvmProjectionChangedEventArgs eventArgs)
    {
        _ = PushProjectionChangedAsync(eventArgs);
    }

    private async Task PushProjectionChangedAsync(MvvmProjectionChangedEventArgs eventArgs)
    {
        try
        {
            await _dispatchGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                if (IsClosed || _transport is null || _viewId is not Guid view)
                {
                    return;
                }

                byte[] frame = MvvmHostFrameEncoder.Patch(
                    _session,
                    view,
                    eventArgs.FromRevision,
                    eventArgs.Response,
                    _limits);
                _window.SendRaw(_options.ReceiveFunctionName, frame);
            }
            finally
            {
                _dispatchGate.Release();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (IsClosed)
        {
        }
        catch (Exception)
        {
            // Unsolicited delivery is best-effort. The next client request can recover
            // through the normal revision/snapshot path.
        }
    }

    private bool HasRoute(MvvmWireMessage message) =>
        message.Document.GetProperty("session").GetGuid() == _session.Id.Value &&
        message.Document.GetProperty("view").GetGuid() == _viewId &&
        _session.Authorizes(message.Document.GetProperty("capability").GetString()!);

    private static Guid RequestId(MvvmWireMessage message) =>
        message.Document.GetProperty("request").GetGuid();

    private static MvvmRequest CreateRequest(MvvmWireMessage message)
    {
        var requestId = new MvvmRequestId(RequestId(message));
        JsonElement payload = message.Payload;
        return message.Kind switch
        {
            "setProperty" => new MvvmMutationRequest(
                requestId,
                MvvmMutationKind.SetProperty,
                message.Document.GetProperty("baseRevision").GetInt64(),
                payload.GetProperty("member").GetInt32(),
                payload.GetProperty("value")),
            "execute" => new MvvmMutationRequest(
                requestId,
                MvvmMutationKind.ExecuteCommand,
                message.Document.GetProperty("baseRevision").GetInt64(),
                payload.GetProperty("member").GetInt32(),
                payload.TryGetProperty("argument", out JsonElement argument)
                    ? argument
                    : NullJson()),
            "cancel" => new MvvmCancelRequest(
                requestId,
                new MvvmRequestId(payload.GetProperty("targetRequest").GetGuid())),
            "ack" => new MvvmAcknowledgeRequest(
                requestId,
                payload.GetProperty("revision").GetInt64()),
            "requestSnapshot" => new MvvmSnapshotRequest(requestId),
            _ => throw new InvalidOperationException("The client message is not dispatchable."),
        };
    }

    private static JsonElement NullJson()
    {
        using JsonDocument document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    private async ValueTask DisposeSessionAsync()
    {
        if (Interlocked.Exchange(ref _sessionDisposed, 1) != 0)
        {
            return;
        }

        MvvmWebUiTransport? transport = _transport;
        _transport = null;
        if (transport is not null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
