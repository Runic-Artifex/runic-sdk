// Shared by the test and smoke projects that set RunicTestPorts=true (eng/build/test-timeouts.targets).
#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

/// <summary>
/// Leases a loopback TCP port for a child process, a server started later, or a URL handed to another program.
/// </summary>
/// <remarks>
/// <para>
/// A port taken by binding port 0 and closing the listener is free only for a moment: any later bind to port 0 or
/// outgoing connection on the machine can receive it before the child binds it (#1257). A lease therefore picks a port
/// outside the operating system's ephemeral range, which neither <c>bind(0)</c> nor <c>connect()</c> hands out, at
/// random so that parallel tests do not collide, and holds a listener on it until <see cref="PortLease.Release"/>, so
/// no other lease, in this process or another, takes it meanwhile.
/// </para>
/// <para>
/// Release the lease immediately before the program that binds the port starts. A test whose child must not listen
/// (a disabled inspector) leases the port and keeps holding it, or releases it and checks the listener's owner.
/// </para>
/// </remarks>
internal static class TestPorts
{
    // Below the ephemeral range: privileged ports and the well-known services' low numbers stay out.
    private const int Lowest = 10_000;
    private const int Attempts = 200;

    /// <summary>Leases a port on <paramref name="address"/>, the IPv4 loopback address by default.</summary>
    public static PortLease Lease(IPAddress? address = null)
    {
        address ??= IPAddress.Loopback;
        var (low, high) = Candidates();
        SocketException? last = null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var port = RandomNumberGenerator.GetInt32(low, high + 1);
            // A port that a connection or TIME_WAIT still uses binds with SO_REUSEADDR, as .NET's listener sets it on
            // Linux, but not for a child that binds without it.
            if (SocketsUse(port)) continue;
            var listener = new TcpListener(address, port);
            // Windows lets a second socket bind a port that another holds without exclusive use.
            if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
            try
            {
                listener.Start();
                return new PortLease(listener, port);
            }
            // Taken, or reserved (Windows excludes ranges for Hyper-V and others with WSAEACCES).
            catch (SocketException exception)
            {
                listener.Stop();
                last = exception;
            }
        }
        throw new InvalidOperationException($"No port in {low}-{high} could be leased after {Attempts} attempts.", last);
    }

    // Linux: whether any TCP socket, in any state, has the port as its local port (/proc/net/tcp and tcp6).
    internal static bool SocketsUse(int port)
    {
        if (!OperatingSystem.IsLinux()) return false;
        foreach (var table in (string[])["/proc/net/tcp", "/proc/net/tcp6"])
        {
            string[] lines;
            try { lines = File.ReadAllLines(table); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            // sl local_address rem_address st ...: local_address is HEXADDRESS:HEXPORT.
            for (var index = 1; index < lines.Length; index++)
            {
                var fields = lines[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2) continue;
                var colon = fields[1].LastIndexOf(':');
                if (colon >= 0 && int.TryParse(fields[1].AsSpan(colon + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var local)
                    && local == port)
                    return true;
            }
        }
        return false;
    }

    /// <summary>The ports a lease picks from: the largest span outside the ephemeral range.</summary>
    internal static (int Low, int High) Candidates() => Candidates(EphemeralRange());

    internal static (int Low, int High) Candidates((int Low, int High) ephemeral)
    {
        var below = (Low: Lowest, High: ephemeral.Low - 1);
        var above = (Low: ephemeral.High + 1, High: IPEndPoint.MaxPort);
        var choice = below.High - below.Low >= above.High - above.Low ? below : above;
        if (choice.High - choice.Low < 1_000)
            throw new InvalidOperationException($"The ephemeral port range {ephemeral.Low}-{ephemeral.High} leaves no ports to lease.");
        return choice;
    }

    // Linux reads its range from /proc; Windows and macOS use the IANA dynamic range unless reconfigured, which the
    // runners and development machines are not.
    private static (int Low, int High) EphemeralRange()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var fields = File.ReadAllText("/proc/sys/net/ipv4/ip_local_port_range")
                    .Split((char[])['\t', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
                return (int.Parse(fields[0], CultureInfo.InvariantCulture), int.Parse(fields[1], CultureInfo.InvariantCulture));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException)
            {
            }
        }
        return (49_152, IPEndPoint.MaxPort);
    }
}

/// <summary>A port that <see cref="TestPorts.Lease"/> holds until <see cref="Release"/>.</summary>
internal sealed class PortLease : IDisposable
{
    private TcpListener? _listener;

    internal PortLease(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    /// <summary>The leased port.</summary>
    public int Port { get; }

    /// <summary>Whether the lease still holds the port.</summary>
    public bool IsHeld => _listener is not null;

    /// <summary>Stops holding the port: call it immediately before the program that binds it starts.</summary>
    /// <returns>The port.</returns>
    public int Release()
    {
        _listener?.Stop();
        _listener = null;
        return Port;
    }

    /// <inheritdoc />
    public void Dispose() => Release();

    /// <inheritdoc />
    public override string ToString() => Port.ToString(CultureInfo.InvariantCulture);
}
