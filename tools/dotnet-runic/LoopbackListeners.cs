using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Runic.Application.Tool;

/// <summary>
/// Listeners on one port of both loopback addresses, 127.0.0.1 and <c>[::1]</c> (DEV-SEC-80). A browser or debugger
/// that connects by a name, such as <c>&lt;label&gt;.localhost</c> or <c>localhost</c>, may try <c>[::1]</c> first
/// (Chromium does), and any local user can listen there without privilege: a listener on 127.0.0.1 alone would let
/// that user receive what the client sends. So the port counts as dev's only when both addresses are bound; when
/// either is taken, the port is taken. A system without IPv6 has no <c>[::1]</c> for anyone to take, and then
/// 127.0.0.1 alone is enough.
/// </summary>
internal sealed class LoopbackListeners : IDisposable
{
    // Tries for a free port on both addresses when the system picks it (port 0): the one it picks on 127.0.0.1 may be in
    // use on [::1].
    private const int FreePortAttempts = 16;

    private readonly List<TcpListener> _listeners;

    private LoopbackListeners(List<TcpListener> listeners) => _listeners = listeners;

    /// <summary>The listeners: 127.0.0.1, then <c>[::1]</c> unless the system has no IPv6 loopback.</summary>
    internal IReadOnlyList<TcpListener> Listeners => _listeners;

    internal int Port => ((IPEndPoint)_listeners[0].LocalEndpoint).Port;

    /// <summary>
    /// Listens on 127.0.0.1:<paramref name="port"/> and <c>[::1]</c>:<paramref name="port"/>, or on a port free on both
    /// when <paramref name="port"/> is 0; null when the port is taken on either address.
    /// </summary>
    internal static LoopbackListeners? TryStart(int port)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        for (int attempt = 0; attempt < (port == 0 ? FreePortAttempts : 1); attempt++)
        {
            TcpListener? ipv4 = TryListen(IPAddress.Loopback, port, out _);
            if (ipv4 is null) return null;
            int bound = ((IPEndPoint)ipv4.LocalEndpoint).Port;
            TcpListener? ipv6 = TryListen(IPAddress.IPv6Loopback, bound, out bool missing);
            if (ipv6 is not null) return new([ipv4, ipv6]);
            if (missing) return new([ipv4]);
            ipv4.Stop();
            ipv4.Dispose();
        }
        return null;
    }

    /// <summary>
    /// Whether the error says the system has no such address, as a system without IPv6 has no <c>[::1]</c>; anything
    /// else, such as the port being in use, means another program may hold it.
    /// </summary>
    internal static bool IsMissingAddress(IPAddress address, SocketException error) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 &&
        error.SocketErrorCode is SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported;

    private static TcpListener? TryListen(IPAddress address, int port, out bool missing)
    {
        missing = false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
        {
            missing = true;
            return null;
        }
        var listener = new TcpListener(address, port);
        try
        {
            listener.Start();
            return listener;
        }
        catch (SocketException error)
        {
            missing = IsMissingAddress(address, error);
            listener.Dispose();
            return null;
        }
    }

    /// <summary>Stops accepting on every address.</summary>
    internal void Stop()
    {
        foreach (TcpListener listener in _listeners) listener.Stop();
    }

    public void Dispose()
    {
        foreach (TcpListener listener in _listeners) listener.Dispose();
    }
}
