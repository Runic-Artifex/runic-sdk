using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using CsWebUi;
using RunicToolkit.ApplicationBridge;

namespace RunicToolkit.Hosting.CsWebUi.ApplicationBridge;

/// <summary>Owns one Application Bridge session on one fixed binary CsWebUi channel.</summary>
public sealed class CsWebUiApplicationBridge : IAsyncDisposable
{
    private readonly IApplicationBridgeWindow _window;
    private readonly ApplicationBridgeSession _session;
    private readonly CsWebUiApplicationBridgeOptions _options;
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _eventGate = new();
    private List<BridgeHostEnvelope>? _inFlightFrames;
    private bool _inFlightOverflow;
    private IDisposable? _binding;
    private ulong? _clientId;
    private ulong? _connectionId;
    private int _disposed;

    private CsWebUiApplicationBridge(
        IApplicationBridgeWindow window,
        ApplicationBridgeSession session,
        CsWebUiApplicationBridgeOptions options)
    {
        _window = window;
        _session = session;
        _options = options;
        _binding = window.Bind(options.BindingName, OnFrameAsync);
        session.EventProduced += OnEventProduced;
    }

    /// <summary>Gets the pinned native identity after the first valid initialization.</summary>
    public (ulong ClientId, ulong ConnectionId)? ConnectionIdentity =>
        _clientId is ulong client && _connectionId is ulong connection ? (client, connection) : null;

    /// <summary>Attaches and transfers ownership of the session to a native window.</summary>
    public static CsWebUiApplicationBridge Attach(
        WebUiWindow window,
        ApplicationBridgeSession session,
        CsWebUiApplicationBridgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        return Attach(new NativeApplicationBridgeWindow(window), session, options);
    }

    internal static CsWebUiApplicationBridge Attach(
        IApplicationBridgeWindow window,
        ApplicationBridgeSession session,
        CsWebUiApplicationBridgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(session);
        CsWebUiApplicationBridgeOptions selected = options ?? new();
        selected.Validate();
        return new(window, session, selected);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _session.EventProduced -= OnEventProduced;
        _binding?.Dispose();
        _binding = null;
        _shutdown.Cancel();
        await _dispatch.WaitAsync().ConfigureAwait(false);
        try
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _dispatch.Release();
            _dispatch.Dispose();
            _shutdown.Dispose();
        }
    }

    private async ValueTask<byte[]?> OnFrameAsync(
        IApplicationBridgeEvent webUiEvent,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0 || webUiEvent.ArgumentCount != 1)
        {
            webUiEvent.CloseClient();
            return null;
        }
        byte[] frame = webUiEvent.GetBytes(0);
        if (!ApplicationBridgeCodec.TryDecodeClient(frame, out BridgeClientEnvelope? envelope, _options.Limits) ||
            !AcceptIdentity(webUiEvent, envelope!))
        {
            webUiEvent.CloseClient();
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _dispatch.WaitAsync(linked.Token).ConfigureAwait(false);
        lock (_eventGate)
        {
            _inFlightFrames = [];
            _inFlightOverflow = false;
        }
        try
        {
            BridgeHostEnvelope response = await _session.DispatchAsync(envelope!, linked.Token).ConfigureAwait(false);
            return CompleteResponseBatch(response);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Native callback or bridge teardown owns cancellation.
            return null;
        }
        catch (Exception)
        {
            webUiEvent.CloseClient();
            return null;
        }
        finally
        {
            lock (_eventGate)
            {
                _inFlightFrames = null;
                _inFlightOverflow = false;
            }
            _dispatch.Release();
        }
    }

    private bool AcceptIdentity(IApplicationBridgeEvent webUiEvent, BridgeClientEnvelope envelope)
    {
        if (_clientId is null)
        {
            if (envelope.Kind != "initialize") return false;
            _clientId = webUiEvent.ClientId;
            _connectionId = webUiEvent.ConnectionId;
            return true;
        }
        if (_clientId != webUiEvent.ClientId) return false;
        if (_connectionId == webUiEvent.ConnectionId) return true;
        if (envelope.Kind != "initialize") return false;
        _connectionId = webUiEvent.ConnectionId;
        return true;
    }

    private void OnEventProduced(object? sender, BridgeHostEnvelope message)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_eventGate)
        {
            if (_inFlightFrames is not null)
            {
                if (_inFlightFrames.Count >= _options.Limits.MaxCollectionItems - 1)
                    _inFlightOverflow = true;
                else
                    _inFlightFrames.Add(message);
                return;
            }
        }
        _window.SendRaw(_options.ReceiverName, ApplicationBridgeCodec.EncodeHost(message, _options.Limits));
    }

    private byte[] CompleteResponseBatch(BridgeHostEnvelope response)
    {
        List<BridgeHostEnvelope> frames;
        lock (_eventGate)
        {
            if (_inFlightOverflow)
                throw new InvalidOperationException("The correlated host frame batch exceeded its configured item limit.");
            frames = _inFlightFrames ?? [];
            frames.Add(response);
            frames.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
            _inFlightFrames = null;
        }
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray();
            foreach (BridgeHostEnvelope frame in frames)
            {
                writer.WriteRawValue(
                    ApplicationBridgeCodec.EncodeHost(frame, _options.Limits),
                    skipInputValidation: true);
                if (output.WrittenCount > _options.Limits.MaxFrameBytes)
                    throw new InvalidOperationException("The correlated host frame batch exceeded its configured byte limit.");
            }
            writer.WriteEndArray();
        }
        if (output.WrittenCount > _options.Limits.MaxFrameBytes)
            throw new InvalidOperationException("The correlated host frame batch exceeded its configured byte limit.");
        return output.WrittenSpan.ToArray();
    }
}
