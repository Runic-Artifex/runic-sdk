using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Runic.Desktop.Internal;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// Elevated-peer admission (host-protocol.md, Elevated-peer admission; D059.13): the lookup of a loopback peer's process,
// and every listener of a Desktop host that admits only the peers its admission allows, refusing the others before it
// reads their request. The whole suite also runs on the native server (-p:RunicDesktopServer=Native).
public sealed class ElevatedPeerAdmissionTests
{
    private static readonly IPEndPoint Peer = new(IPAddress.Loopback, 50_123);
    private static readonly IPEndPoint Listener = new(IPAddress.Loopback, 40_117);

    [Test]
    public void AConnectionsOwnerIsTheProcessOfItsClientsEnd()
    {
        // The server's end (local = listener) belongs to the guest itself and is not the peer.
        var table = Ipv4Table((Listener, Peer, 100), (Peer, Listener, 200), (new IPEndPoint(IPAddress.Loopback, 50_124), Listener, 300));
        Assert.Equal(200, WindowsLoopbackPeers.FindOwner(table, ipv6: false, Peer, Listener));
        Assert.Null(WindowsLoopbackPeers.FindOwner(table, ipv6: false, new IPEndPoint(IPAddress.Loopback, 1), Listener));
        // The same connection with two owners, or a TIME_WAIT row without one, is unknown.
        Assert.Null(WindowsLoopbackPeers.FindOwner(Ipv4Table((Peer, Listener, 200), (Peer, Listener, 201)), ipv6: false, Peer, Listener));
        Assert.Null(WindowsLoopbackPeers.FindOwner(Ipv4Table((Peer, Listener, 0)), ipv6: false, Peer, Listener));
        // A row count beyond the buffer reads as no table.
        var truncated = table.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(truncated, 9);
        Assert.Null(WindowsLoopbackPeers.FindOwner(truncated, ipv6: false, Peer, Listener));
    }

    [Test]
    public void IPv6AndIPv4MappedRowsAreFoundToo()
    {
        var peer6 = new IPEndPoint(IPAddress.IPv6Loopback, 50_200);
        var listener6 = new IPEndPoint(IPAddress.IPv6Loopback, 40_118);
        Assert.Equal(7, WindowsLoopbackPeers.FindOwner(Ipv6Table((peer6, listener6, 7)), ipv6: true, peer6, listener6));
        // A dual-mode socket lists an IPv4 connection with mapped addresses; the peer may be given either way.
        var mapped = Ipv6Table((new IPEndPoint(Peer.Address.MapToIPv6(), Peer.Port), new IPEndPoint(Listener.Address.MapToIPv6(), Listener.Port), 8));
        Assert.Equal(8, WindowsLoopbackPeers.FindOwner(mapped, ipv6: true, Peer, Listener));
        Assert.Equal(8, WindowsLoopbackPeers.FindOwner(mapped, ipv6: true, new IPEndPoint(Peer.Address.MapToIPv6(), Peer.Port), Listener));
    }

    [Test]
    public void APeerIsElevatedOnlyWhenTheProcessThatStillOwnsTheConnectionIsElevatedRunningAndOlder()
    {
        const long accepted = 1_000;
        var owner = 42;
        var process = new WindowsLoopbackPeers.PeerProcess(CreationTime: 900, Running: true, Elevated: true);
        WindowsLoopbackPeers.PeerProcess? Open(int id, Func<bool> stillOwner) => id == 42 && stillOwner() ? process : null;

        Assert.True(WindowsLoopbackPeers.IsElevated(() => owner, Open, accepted));
        // Not elevated, exited (an inherited socket outlives its creator), or created after the connection was accepted:
        // a process that took the id of an owner that exited.
        process = process with { Elevated = false };
        Assert.False(WindowsLoopbackPeers.IsElevated(() => owner, Open, accepted));
        process = process with { Elevated = true, Running = false };
        Assert.False(WindowsLoopbackPeers.IsElevated(() => owner, Open, accepted));
        process = process with { Running = true, CreationTime = 1_001 };
        Assert.False(WindowsLoopbackPeers.IsElevated(() => owner, Open, accepted));
        process = process with { CreationTime = 900 };

        // The connection changed owners while the process was read: the second lookup must name the same id.
        var lookups = 0;
        Assert.False(WindowsLoopbackPeers.IsElevated(() => ++lookups == 1 ? 42 : 43, Open, accepted));
        Assert.False(WindowsLoopbackPeers.IsElevated(static () => null, Open, accepted));
        Assert.False(WindowsLoopbackPeers.IsElevated(static () => 0, (_, _) => throw new InvalidOperationException("Opened process 0."), accepted));
        Assert.False(WindowsLoopbackPeers.IsElevated(() => owner, static (_, _) => null, accepted));
    }

    [Test]
    public void AdmissionRefusesOtherAddressesAndFailedChecksAndReportsEveryRefusal()
    {
        var refused = new List<IPEndPoint>();
        var seen = new List<(IPEndPoint, IPEndPoint)>();
        var admission = new LoopbackPeerAdmission((peer, listener) =>
        {
            seen.Add((peer, listener));
            return peer.Port != 1;
        }, refused.Add);

        Assert.True(admission.Admit(Peer, Listener));
        // IPv4-mapped addresses are checked as IPv4.
        Assert.True(admission.Admit(new IPEndPoint(Peer.Address.MapToIPv6(), Peer.Port), new IPEndPoint(Listener.Address.MapToIPv6(), Listener.Port)));
        Assert.Equal([(Peer, Listener), (Peer, Listener)], seen);
        Assert.False(admission.Admit(new IPEndPoint(IPAddress.Loopback, 1), Listener));
        Assert.False(admission.Admit(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 5), Listener));
        Assert.False(admission.Admit(null, 5, IPAddress.Loopback, 6));
        Assert.Equal(2, refused.Count);
        var throwing = new LoopbackPeerAdmission(static (_, _) => throw new IOException("No table."), refused.Add);
        Assert.False(throwing.Admit(Peer, Listener));
        Assert.Equal(3, refused.Count);
    }

    // The real lookup for a connection whose client is this process: elevated exactly when this process is, on Linux and
    // Windows; nothing is elevated on macOS, which has no lookup.
    [Test]
    public async Task TheRealLookupSeesWhetherThisProcessRunsElevated()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();
        var peer = (IPEndPoint)server.Client.RemoteEndPoint!;
        var local = (IPEndPoint)server.Client.LocalEndPoint!;
        Assert.Equal(ElevatedPeers.IsAvailable && Environment.IsPrivilegedProcess, ElevatedPeers.IsElevated(peer, local));
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), ElevatedPeers.IsAvailable);
        // The server's own end is not a client's.
        Assert.False(ElevatedPeers.IsElevated(new IPEndPoint(IPAddress.Loopback, 1), local));
    }

    // A Desktop host whose admission refuses a peer closes its connection without a response, on its page listener and on
    // the IPv6 listener of region frames; an admitted peer is served. A listener that started already keeps its peers.
    [Test]
    public async Task EveryListenerOfAHostRefusesPeersItsAdmissionRefusesBeforeReadingTheRequest()
    {
        var admit = false;
        var refused = 0;
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WaitForConnection = false, Security = DesktopSecurityPolicy.Default });
        host.PeerAdmission = new LoopbackPeerAdmission((_, _) => Volatile.Read(ref admit), _ => Interlocked.Increment(ref refused));
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<p>page</p>") });
        Assert.Throws<InvalidOperationException>(() => host.PeerAdmission = null);

        Assert.Null(await ResponseAsync(surface.Url));
        Assert.True(Volatile.Read(ref refused) >= 1);
        Volatile.Write(ref admit, true);
        Assert.NotNull(await ResponseAsync(surface.Url));

        var frame = await surface.BeginFrameLaunchAsync("http://127.0.0.1:38200", allowSameSite: true, measuresHeight: false, CancellationToken.None);
        if (frame.SameSite) return; // No IPv6 here: the frame shares the page's listener, checked above.
        Volatile.Write(ref admit, false);
        Assert.Null(await ResponseAsync(frame.Url));
        Volatile.Write(ref admit, true);
        Assert.StartsWith("HTTP/1.1 303", await ResponseAsync(frame.Url));
    }

    // The status line, or null when the server closed the connection without one.
    private static async Task<string?> ResponseAsync(Uri url)
    {
        using var client = new TcpClient(url.HostNameType == UriHostNameType.IPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
        var request = $"GET {url.PathAndQuery} HTTP/1.1\r\nHost: {url.Authority}\r\nConnection: close\r\n\r\n";
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
        try
        {
            // A refused peer's connection is closed at once, so the reset can reach the client during the connect itself
            // (macOS) as well as on the write or read.
            await client.ConnectAsync(IPAddress.Parse(url.DnsSafeHost), url.Port);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0) return null;
            var text = Encoding.ASCII.GetString(buffer, 0, read);
            return text[..Math.Max(0, text.IndexOf('\r'))];
        }
        catch (Exception error) when (error is IOException or SocketException) { return null; }
    }

    private static byte[] Ipv4Table(params (IPEndPoint Local, IPEndPoint Remote, uint Owner)[] rows)
    {
        var table = new byte[4 + rows.Length * 24];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = table.AsSpan(4 + index * 24, 24);
            BinaryPrimitives.WriteUInt32LittleEndian(row, 5); // ESTABLISHED
            rows[index].Local.Address.GetAddressBytes().CopyTo(row[4..]);
            WritePort(row[8..], rows[index].Local.Port);
            rows[index].Remote.Address.GetAddressBytes().CopyTo(row[12..]);
            WritePort(row[16..], rows[index].Remote.Port);
            BinaryPrimitives.WriteUInt32LittleEndian(row[20..], rows[index].Owner);
        }
        return table;
    }

    private static byte[] Ipv6Table(params (IPEndPoint Local, IPEndPoint Remote, uint Owner)[] rows)
    {
        var table = new byte[4 + rows.Length * 56];
        BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = table.AsSpan(4 + index * 56, 56);
            rows[index].Local.Address.GetAddressBytes().CopyTo(row);
            WritePort(row[20..], rows[index].Local.Port);
            rows[index].Remote.Address.GetAddressBytes().CopyTo(row[24..]);
            WritePort(row[44..], rows[index].Remote.Port);
            BinaryPrimitives.WriteUInt32LittleEndian(row[48..], 5);
            BinaryPrimitives.WriteUInt32LittleEndian(row[52..], rows[index].Owner);
        }
        return table;
    }

    // In network byte order in the low 16 bits of the DWORD.
    private static void WritePort(Span<byte> field, int port)
    {
        field[0] = (byte)(port >> 8);
        field[1] = (byte)port;
    }
}
