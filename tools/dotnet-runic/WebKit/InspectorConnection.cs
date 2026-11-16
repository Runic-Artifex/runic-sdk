using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.WebKit;

/// <summary>
/// A client of the Window's WebKit remote inspector server (<see cref="InspectorWire"/>). WebKit admits one client at a
/// time (<c>RemoteInspectorServer::setupInspectorClient</c>), so the adapter holds a connection only while a debugger is
/// attached, and GNOME Web can open <c>inspector://</c> otherwise.
/// </summary>
internal sealed class InspectorConnection : IAsyncDisposable
{
    private static long s_generations;
    private readonly long _generation = Interlocked.Increment(ref s_generations);
    private readonly TcpClient _client = new() { NoDelay = true };
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<ulong, IReadOnlyList<InspectorTarget>> _targets = [];
    private readonly TaskCompletionSource _described = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private NetworkStream? _stream;
    private Task _reading = Task.CompletedTask;
    private int _closed;

    /// <summary>A protocol message from a target's backend: connection, target, JSON text. Raised on the read loop.</summary>
    internal event Action<ulong, ulong, string>? MessageToFrontend;

    /// <summary>The target list changed. Raised on the read loop.</summary>
    internal event Action? TargetsChanged;

    /// <summary>The server closed the connection or it failed, for example because the Window exited. Raised once.</summary>
    internal event Action? Closed;

    internal WebKitProtocol Protocol { get; private set; } = WebKitProtocol.Unknown;

    /// <summary>Which connection of this dev session this is; the targets it lists carry it.</summary>
    internal long Generation => _generation;

    internal IReadOnlyList<InspectorTarget> Targets
    {
        get
        {
            lock (_gate) return _targets.Values.SelectMany(static list => list).ToList();
        }
    }

    internal bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// Connects and announces this client; completes once the server described its protocol. <paramref name="verifyServer"/>
    /// runs once the TCP connection is open and before anything is sent; it throws to refuse the server.
    /// </summary>
    internal async Task ConnectAsync(string host, int port, Action? verifyServer, CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        verifyServer?.Invoke();
        CdpTrace.Write("webkit", $"inspector connection {_generation} to {host}:{port}");
        _stream = _client.GetStream();
        _reading = Task.Run(() => ReadAsync(_stop.Token), CancellationToken.None);
        await SendAsync(InspectorWire.SetupInspectorClient(), cancellationToken).ConfigureAwait(false);
        // A second client gets no answer at all (WebKit ignores its setup), so the wait is bounded.
        await _described.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }

    internal Task SetupAsync(InspectorTarget target, CancellationToken cancellationToken) =>
        SendAsync(InspectorWire.Setup(target.ConnectionId, target.TargetId), cancellationToken);

    internal Task FrontendDidCloseAsync(InspectorTarget target, CancellationToken cancellationToken) =>
        SendAsync(InspectorWire.FrontendDidClose(target.ConnectionId, target.TargetId), cancellationToken);

    internal Task SendToBackendAsync(InspectorTarget target, string message, CancellationToken cancellationToken) =>
        SendAsync(InspectorWire.SendMessageToBackend(target.ConnectionId, target.TargetId, message), cancellationToken);

    private async Task SendAsync(byte[] frame, CancellationToken cancellationToken)
    {
        NetworkStream stream = _stream ?? throw new InvalidOperationException("The inspector connection is not open.");
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or SocketException)
        {
            Close();
            throw new IOException("The connection to the Window's WebKit inspector closed.", error);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        var header = new byte[InspectorWire.HeaderSize];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _stream!.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                var body = new byte[InspectorWire.ReadBodySize(header)];
                await _stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
                (string name, byte[] parameters) = InspectorWire.ReadBody(body);
                Dispatch(name, parameters);
            }
        }
        catch (Exception error) when (error is IOException or EndOfStreamException or ObjectDisposedException or SocketException
            or OperationCanceledException or InvalidOperationException)
        {
        }
        finally
        {
            Close();
        }
    }

    private void Dispatch(string name, byte[] parameters)
    {
        switch (name)
        {
            case "DidSetupInspectorClient":
                Protocol = WebKitProtocol.Parse(InspectorWire.ReadBackendCommands(parameters));
                _described.TrySetResult();
                break;
            case "SetTargetList":
                (ulong connectionId, IReadOnlyList<InspectorTarget> listed) = InspectorWire.ReadTargetList(parameters);
                IReadOnlyList<InspectorTarget> targets = [.. listed.Select(target => target with { Generation = _generation })];
                CdpTrace.Write("webkit", $"targets of connection {connectionId}: {string.Join(", ", targets.Select(static target => $"{target.PageId} {target.Type} {target.Url}"))}");
                lock (_gate)
                {
                    if (targets.Count == 0) _targets.Remove(connectionId);
                    else _targets[connectionId] = targets;
                }
                TargetsChanged?.Invoke();
                break;
            case "SendMessageToFrontend":
                (ulong connection, ulong target, string message) = InspectorWire.ReadMessageToFrontend(parameters);
                MessageToFrontend?.Invoke(connection, target, message);
                break;
        }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        CdpTrace.Write("webkit", $"inspector connection {_generation} closed");
        _described.TrySetException(new IOException("The Window's WebKit inspector closed the connection."));
        _stop.Cancel();
        _client.Dispose();
        Closed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        try
        {
            await _reading.ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
        }
        _stop.Dispose();
        _write.Dispose();
    }
}
