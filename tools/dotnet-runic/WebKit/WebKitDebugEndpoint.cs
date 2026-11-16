using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.WebKit;

/// <summary>
/// The CDP adapter for the Window's WebKitGTK pages under <c>dotnet runic dev</c> on Linux (D066.39): a
/// <see cref="CdpServer"/> on loopback, and a connection to the Window's WebKit remote inspector server that exists only
/// while a debugger is connected, since WebKit serves one client and GNOME Web may want <c>inspector://</c> otherwise.
/// When the Window restarts (<c>dotnet watch</c>), the connection closes with it; while debuggers stay connected, the
/// endpoint reconnects to the new Window's server and announces its pages. On Linux it talks only to an inspector port
/// whose listener belongs to the user who runs dev, so another user who took the port first does not receive the
/// debugger's traffic. When another client takes WebKit's single client slot (<see cref="InspectorConnection"/>), the
/// endpoint abandons its connection, the debuggers learn that their pages detached, and it connects again only for a
/// debugger that attaches after that.
/// </summary>
internal sealed class WebKitDebugEndpoint : IAsyncDisposable
{
    private static readonly TimeSpan IdleDisconnect = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromMilliseconds(500);
    // A connection to the inspector server that answers three probes while it is there is a client that did not take the
    // slot, or the Window's own (its web process connects when it starts).
    private const int VettingProbes = 3;
    private readonly string _inspectorHost;
    private readonly int _inspectorPort;
    private readonly LoopbackSockets? _sockets;
    private readonly Action<string> _report;
    private readonly HashSet<uint?> _reportedListeners = [];
    // This endpoint's own connections to the inspector, also closed ones that TIME_WAIT still lists.
    private readonly HashSet<int> _ownPorts = [];
    // The debuggers that were connected when another client took the slot: they do not reconnect the endpoint.
    private readonly HashSet<CdpClient> _replacedClients = [];
    private readonly SemaphoreSlim _connecting = new(1, 1);
    private readonly object _gate = new();
    private readonly HashSet<CdpClient> _clients = [];
    private readonly CancellationTokenSource _stop = new();
    private InspectorConnection? _connection;
    private CdpServer? _server;
    private Task _reconnecting = Task.CompletedTask;
    private long _idleGeneration;
    private bool _replaced;

    /// <summary>
    /// <paramref name="sockets"/> tells who owns a loopback socket (<see cref="LoopbackSockets.Current"/>; null: no check);
    /// <paramref name="report"/> receives the <c>[dev]</c> lines for refused connections.
    /// </summary>
    internal WebKitDebugEndpoint(string inspectorHost, int inspectorPort, LoopbackSockets? sockets, Action<string> report)
    {
        _inspectorHost = inspectorHost;
        _inspectorPort = inspectorPort;
        _sockets = sockets;
        _report = report;
    }

    /// <summary>How often an attached connection checks for other clients of the inspector server.</summary>
    internal TimeSpan WatchInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a probe waits for the Window's answer before the connection counts as replaced.</summary>
    internal TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The line dev prints when another client took the Window's WebKit inspector from the adapter.</summary>
    internal static string ReplacedLine(string inspector) =>
        $"[dev] Debugging: another client took over the Window's WebKit inspector at {inspector}, so the JavaScript debugger was " +
        "detached. Attach it again to continue; dev then takes the inspector back.";

    /// <summary>The CDP port, once <see cref="Start"/> succeeded.</summary>
    internal int? Port => _server?.Port;

    /// <summary>The generation of the open connection to a Window's inspector, or null.</summary>
    internal long? ConnectedGeneration
    {
        get
        {
            lock (_gate) return _connection is { IsClosed: false } open ? open.Generation : null;
        }
    }

    /// <summary>The pages of the current connection, without connecting.</summary>
    internal IReadOnlyList<InspectorTarget> KnownPages
    {
        get
        {
            InspectorConnection? connection;
            lock (_gate) connection = _connection;
            return connection is { IsClosed: false } ? connection.Targets.Where(static target => target.IsPage).ToList() : [];
        }
    }

    /// <summary>Listens for debuggers on 127.0.0.1:<paramref name="port"/>; false when the port is taken.</summary>
    internal bool Start(int port, IReadOnlySet<string>? allowedOrigins = null)
    {
        _server = CdpServer.TryStart(port, this, allowedOrigins, _sockets, _report);
        return _server is not null;
    }

    /// <summary>
    /// The Window's pages; connects to its inspector if needed. Empty while no Window runs, and for a debugger that was
    /// connected when another client took the inspector: a request from any other one reconnects.
    /// </summary>
    internal async Task<IReadOnlyList<InspectorTarget>> ListPagesAsync(CancellationToken cancellationToken, CdpClient? requester = null)
    {
        lock (_gate)
        {
            if (_replaced && requester is not null && _replacedClients.Contains(requester)) return [];
            ResumeAfterReplacement();
        }
        InspectorConnection? connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        ScheduleIdleDisconnect();
        if (connection is null) return [];
        // The server sends its list right after the setup; a Window that is still loading may list nothing yet.
        for (int attempt = 0; attempt < 10 && !connection.Targets.Any(static target => target.IsPage); attempt++)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return connection.Targets.Where(static target => target.IsPage).ToList();
    }

    internal async Task<(WebKitPage Page, WebKitProtocol Protocol)> OpenPageAsync(InspectorTarget target, CancellationToken cancellationToken)
    {
        InspectorConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The Window's WebKit inspector is not available.");
        var page = new WebKitPage(connection, target);
        await page.OpenAsync(cancellationToken).ConfigureAwait(false);
        return (page, connection.Protocol);
    }

    private async Task<InspectorConnection?> ConnectAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_connection is { IsClosed: false } open) return open;
        }
        await _connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_connection is { IsClosed: false } open) return open;
            }
            var connection = new InspectorConnection();
            try
            {
                await connection.ConnectAsync(_inspectorHost, _inspectorPort, VerifyInspectorListener, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is SocketException or IOException or TimeoutException)
            {
                // No Window yet, it does not answer, or another user's process listens.
                await connection.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            connection.TargetsChanged += () => _ = NotifyTargetsChangedAsync();
            connection.Closed += OnConnectionClosed;
            HashSet<int> known = [.. ReadPeers()];
            lock (_gate)
            {
                _connection = connection;
                _ownPorts.Add(connection.LocalPort);
            }
            _ = Task.Run(() => WatchAsync(connection, known), CancellationToken.None);
            return connection;
        }
        finally
        {
            _connecting.Release();
        }
    }

    /// <summary>
    /// Watches an attached connection for another client taking WebKit's slot, which WebKit does not tell this one. When
    /// a connection to the inspector server appears that was not there when this one connected, a page of this connection
    /// is probed (<see cref="WebKitPage.ProbeAsync"/>) until the newcomer leaves the table or proved harmless; no answer
    /// then means it took the slot. Probing only then keeps a Window stopped at a .NET breakpoint, which answers nothing,
    /// from counting as replaced.
    /// </summary>
    private async Task WatchAsync(InspectorConnection connection, HashSet<int> known)
    {
        var answered = new Dictionary<int, int>();
        try
        {
            while (!connection.IsClosed)
            {
                await Task.Delay(WatchInterval, _stop.Token).ConfigureAwait(false);
                IReadOnlyList<WebKitPage> pages = connection.Pages;
                if (pages.Count == 0) continue;
                WebKitPage page = pages[0];
                HashSet<int> peers = ReadPeers();
                // A port that left the table may come back for another client.
                known.IntersectWith(peers);
                List<int> newcomers;
                lock (_gate) newcomers = [.. peers.Where(peer => !known.Contains(peer) && !_ownPorts.Contains(peer))];
                if (newcomers.Count == 0) continue;
                CdpTrace.Write("adapter", $"inspector connection {connection.Generation}: other clients on ports {string.Join(", ", newcomers)}; probing");
                if (await page.ProbeAsync(ProbeTimeout, _stop.Token).ConfigureAwait(false))
                {
                    foreach (int peer in newcomers)
                    {
                        answered[peer] = answered.GetValueOrDefault(peer) + 1;
                        if (answered[peer] >= VettingProbes) known.Add(peer);
                    }
                    continue;
                }
                if (connection.IsClosed) return;
                await ReplacedAsync(connection).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Another client holds WebKit's slot: the connection is abandoned without another message, the debuggers' sessions on
    // it end (CdpClient.PageGone) and the endpoint stays disconnected until a debugger attaches again.
    private async Task ReplacedAsync(InspectorConnection connection)
    {
        lock (_gate)
        {
            if (_connection != connection) return;
            _connection = null;
            _replaced = true;
            _replacedClients.UnionWith(_clients);
        }
        connection.Closed -= OnConnectionClosed;
        CdpTrace.Write("adapter", $"inspector connection {connection.Generation} was replaced by another client");
        connection.Abandon();
        await connection.DisposeAsync().ConfigureAwait(false);
        await NotifyTargetsChangedAsync().ConfigureAwait(false);
        // Last: a console that nobody reads blocks the write.
        _report(ReplacedLine($"{_inspectorHost}:{_inspectorPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
    }

    // A debugger attaches after a replacement: the endpoint may connect again. Called under the lock.
    private void ResumeAfterReplacement()
    {
        _replaced = false;
        _replacedClients.Clear();
    }

    // The ports at the other end of the inspector server's connections; none without the TCP tables.
    private HashSet<int> ReadPeers() =>
        _sockets is not null && IPAddress.TryParse(_inspectorHost, out IPAddress? address) ? _sockets.Peers(address, _inspectorPort) : new HashSet<int>();

    // Checked once connected and before anything is sent, so the port cannot change hands between the check and the
    // connection: a listener of another user would receive the debugger's breakpoints, evaluations and sources.
    private void VerifyInspectorListener()
    {
        if (_sockets is null) return;
        IReadOnlyList<uint> owners = IPAddress.TryParse(_inspectorHost, out IPAddress? address) ? _sockets.ListenerOwners(address, _inspectorPort) : [];
        if (owners.Count > 0 && owners.All(owner => owner == _sockets.User)) return;
        uint? foreign = owners.Cast<uint?>().FirstOrDefault(owner => owner != _sockets.User);
        bool first;
        lock (_reportedListeners) first = _reportedListeners.Add(foreign);
        if (first) _report(WebKitDebugging.ForeignInspectorHint(_inspectorHost, _inspectorPort, foreign));
        throw new IOException($"The inspector port {_inspectorHost}:{_inspectorPort} does not belong to this user.");
    }

    private void OnConnectionClosed()
    {
        _ = NotifyTargetsChangedAsync();
        KeepConnecting();
    }

    // The Window restarted or is restarting: connect to the new one while debuggers wait for its pages.
    private async Task ReconnectAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (_clients.Count == 0 || _replaced) return;
                }
                await Task.Delay(ReconnectInterval, _stop.Token).ConfigureAwait(false);
                CdpTrace.Write("adapter", "connecting to the Window's inspector");
                if (await ConnectAsync(_stop.Token).ConfigureAwait(false) is not null)
                {
                    await NotifyTargetsChangedAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task NotifyTargetsChangedAsync()
    {
        CdpClient[] clients;
        lock (_gate) clients = [.. _clients];
        foreach (CdpClient client in clients) await client.TargetsChangedAsync().ConfigureAwait(false);
    }

    internal void ClientConnected(CdpClient client)
    {
        lock (_gate)
        {
            _clients.Add(client);
            _idleGeneration++;
            ResumeAfterReplacement();
        }
        KeepConnecting();
    }

    // While debuggers are connected and no Window's inspector is, keep trying: the Window may still be starting.
    private void KeepConnecting()
    {
        lock (_gate)
        {
            CdpTrace.Write("adapter", $"keep connecting: clients {_clients.Count}, reconnecting {!_reconnecting.IsCompleted}, connected {_connection is { IsClosed: false }}");
            if (_clients.Count == 0 || _replaced || _stop.IsCancellationRequested || !_reconnecting.IsCompleted) return;
            if (_connection is { IsClosed: false }) return;
            _reconnecting = Task.Run(ReconnectAsync);
        }
    }

    internal void ClientDisconnected(CdpClient client)
    {
        lock (_gate)
        {
            _clients.Remove(client);
            _replacedClients.Remove(client);
        }
        ScheduleIdleDisconnect();
    }

    // Without debuggers the connection is released after a short while, so GNOME Web can open inspector:// again.
    private void ScheduleIdleDisconnect()
    {
        long generation;
        lock (_gate)
        {
            if (_clients.Count > 0) return;
            generation = ++_idleGeneration;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(IdleDisconnect, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            InspectorConnection? idle = null;
            lock (_gate)
            {
                if (_clients.Count == 0 && _idleGeneration == generation)
                {
                    idle = _connection;
                    _connection = null;
                }
            }
            if (idle is not null)
            {
                idle.Closed -= OnConnectionClosed;
                await idle.DisposeAsync().ConfigureAwait(false);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
        InspectorConnection? connection;
        lock (_gate)
        {
            connection = _connection;
            _connection = null;
        }
        if (connection is not null)
        {
            connection.Closed -= OnConnectionClosed;
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        try
        {
            await _reconnecting.ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
        }
    }
}
