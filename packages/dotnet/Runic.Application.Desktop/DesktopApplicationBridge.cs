using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Runic.Desktop;
using Runic.Application.Bridge;

namespace Runic.Application.Desktop;

/// <summary>Adapts one Application-owned bridge session to a Runic Desktop presentation surface.</summary>
public sealed class DesktopApplicationBridge : IAsyncDisposable
{
    private readonly ApplicationBridgeSession _bridgeSession;
    private readonly DesktopApplicationBridgeOptions _options;
    private readonly PresentationCapabilityRegistration _registration;
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _eventGate = new();
    private List<BridgeHostEnvelope>? _inFlightFrames;
    private PresentationSession? _presentationSession;
    private ulong? _presentationSessionId;
    private long _acceptedConnectionEpoch = -1;
    private int _disposed;

    private DesktopApplicationBridge(
        DesktopSurface surface,
        ApplicationBridgeSession bridgeSession,
        DesktopApplicationBridgeOptions options)
    {
        _bridgeSession = bridgeSession;
        _options = options;
        _registration = surface.RegisterCapability(DesktopApplicationBridgeOptions.Capability, OnFrameAsync);
        bridgeSession.EventProduced += OnEventProduced;
    }

    /// <summary>Gets the active presentation session after successful Application Bridge initialization.</summary>
    public ulong? PresentationSessionId => _presentationSessionId;

    /// <summary>Attaches and transfers ownership of an Application Bridge session to a Desktop surface.</summary>
    public static DesktopApplicationBridge Attach(
        DesktopSurface surface,
        ApplicationBridgeSession session,
        DesktopApplicationBridgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(session);
        DesktopApplicationBridgeOptions selected = options ?? new();
        selected.Validate();
        return new DesktopApplicationBridge(surface, session, selected);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _bridgeSession.EventProduced -= OnEventProduced;
        _registration.Dispose();
        _shutdown.Cancel();
        await _dispatch.WaitAsync().ConfigureAwait(false);
        await _send.WaitAsync().ConfigureAwait(false);
        try
        {
            await _bridgeSession.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
            _dispatch.Release();
            _send.Dispose();
            _dispatch.Dispose();
            _shutdown.Dispose();
        }
    }

    private async ValueTask<PresentationResult> OnFrameAsync(
        PresentationInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            invocation.Kind != PresentationEventKind.Invocation ||
            invocation.ArgumentCount != 1 ||
            !ApplicationBridgeCodec.TryDecodeClient(
                invocation.GetBytes().Span,
                out BridgeClientEnvelope? envelope,
                _options.Limits))
        {
            await invocation.CloseSessionAsync(cancellationToken).ConfigureAwait(false);
            return PresentationResult.None;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _dispatch.WaitAsync(linked.Token).ConfigureAwait(false);
        await _send.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!CanAccept(invocation.Session, envelope!))
            {
                await invocation.CloseSessionAsync(linked.Token).ConfigureAwait(false);
                return PresentationResult.None;
            }

            lock (_eventGate) _inFlightFrames = [];
            BridgeHostEnvelope response = await _bridgeSession.DispatchAsync(envelope!, linked.Token).ConfigureAwait(false);
            AcceptAfterInitialization(invocation.Session, envelope!, response);
            List<BridgeHostEnvelope> frames;
            lock (_eventGate)
            {
                frames = _inFlightFrames ?? [];
                frames.Add(response);
                frames.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
                _inFlightFrames = null;
            }
            foreach (BridgeHostEnvelope frame in frames)
            {
                await invocation.Session.SendAsync(
                    DesktopApplicationBridgeOptions.Receiver,
                    ApplicationBridgeCodec.EncodeHost(frame, _options.Limits),
                    linked.Token).ConfigureAwait(false);
            }
            return PresentationResult.None;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return PresentationResult.None;
        }
        catch
        {
            await invocation.CloseSessionAsync(CancellationToken.None).ConfigureAwait(false);
            return PresentationResult.None;
        }
        finally
        {
            lock (_eventGate) _inFlightFrames = null;
            _send.Release();
            _dispatch.Release();
        }
    }

    private bool CanAccept(PresentationSession session, BridgeClientEnvelope envelope)
    {
        if (_presentationSessionId is null) return envelope.Kind == "initialize";
        if (_presentationSessionId == session.Id)
        {
            return envelope.ConnectionEpoch == _acceptedConnectionEpoch ||
                envelope.Kind == "initialize" && envelope.ConnectionEpoch > _acceptedConnectionEpoch;
        }
        return envelope.Kind == "initialize" && envelope.ConnectionEpoch > _acceptedConnectionEpoch;
    }

    private void AcceptAfterInitialization(
        PresentationSession session,
        BridgeClientEnvelope envelope,
        BridgeHostEnvelope response)
    {
        if (envelope.Kind != "initialize" || response.Kind != "snapshot") return;
        _presentationSession = session;
        _presentationSessionId = session.Id;
        _acceptedConnectionEpoch = envelope.ConnectionEpoch;
    }

    private void OnEventProduced(object? sender, BridgeHostEnvelope message)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_eventGate)
        {
            if (_inFlightFrames is not null)
            {
                if (_inFlightFrames.Count >= _options.Limits.MaxCollectionItems - 1)
                    return;
                _inFlightFrames.Add(message);
                return;
            }
        }
        _ = SendEventAsync(message);
    }

    private async Task SendEventAsync(BridgeHostEnvelope message)
    {
        try
        {
            await _send.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                PresentationSession? session = Volatile.Read(ref _presentationSession);
                if (session is not null)
                {
                    await session.SendAsync(
                        DesktopApplicationBridgeOptions.Receiver,
                        ApplicationBridgeCodec.EncodeHost(message, _options.Limits),
                        _shutdown.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                _send.Release();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch
        {
            // The presentation session owns transport-failure diagnostics.
        }
    }
}
