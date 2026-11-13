using System.Net;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// Which loopback peers a listener serves: every listener of an elevated guest admits only peers that run elevated
// (host-protocol.md, Elevated-peer admission; D059.13). The servers ask before they read anything from a connection, and
// close a refused one without a response; refused reports it, such as to the guest's diagnostics. A check that throws
// refuses.
internal sealed class LoopbackPeerAdmission(Func<IPEndPoint, IPEndPoint, bool> check, Action<IPEndPoint>? refused = null)
{
    // The check of the elevated pair: the peer's process runs elevated (ElevatedPeers).
    internal static LoopbackPeerAdmission Elevated(Action<IPEndPoint>? refused = null) => new(ElevatedPeers.IsElevated, refused);

    // peer is the client's end of the connection, listener the end the server accepted it on.
    internal bool Admit(IPEndPoint peer, IPEndPoint listener)
    {
        peer = Normalize(peer);
        listener = Normalize(listener);
        bool admitted;
        try { admitted = IPAddress.IsLoopback(peer.Address) && check(peer, listener); }
        catch (Exception error) when (error is not OutOfMemoryException) { admitted = false; }
        if (!admitted)
        {
            try { refused?.Invoke(peer); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        return admitted;
    }

    internal bool Admit(IPAddress? peerAddress, int peerPort, IPAddress? listenerAddress, int listenerPort) =>
        peerAddress is not null && listenerAddress is not null &&
        Admit(new IPEndPoint(peerAddress, peerPort), new IPEndPoint(listenerAddress, listenerPort));

    // An IPv4 connection to a dual-mode socket shows as an IPv4-mapped IPv6 address.
    internal static IPEndPoint Normalize(IPEndPoint endpoint) =>
        endpoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint;
}

// Whether the process at the other end of a loopback TCP connection runs elevated, as the elevated pair defines it
// (host-protocol.md, Elevated host): on Windows an elevated token or an integrity level of High or above, on Linux the
// socket's owner is root. False whenever that cannot be shown, and on macOS, where Runic has no peer lookup.
internal static class ElevatedPeers
{
    internal static bool IsAvailable => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    internal static bool IsElevated(IPEndPoint peer, IPEndPoint listener)
    {
        if (OperatingSystem.IsLinux())
            return LoopbackPeerOwner.Find(peer.Address, peer.Port, listener.Address, listener.Port) == 0;
        if (OperatingSystem.IsWindows()) return WindowsLoopbackPeers.IsElevated(peer, listener);
        return false;
    }
}

// The Windows lookup: the owning process of the client's end of the connection (GetExtendedTcpTable with
// TCP_TABLE_OWNER_PID_CONNECTIONS), held open while its token is read (WindowsProcessTokens.Elevated) and while the
// table is read again, so the process that was read is the one that owns the connection: an id can be reused only once
// every handle to the old process is closed. The process must still run and must have been created before the
// connection was accepted, which refuses a process that took the id of an owner that exited. The table parsing and the
// decision run anywhere, for the tests.
internal static partial class WindowsLoopbackPeers
{
    private const int InterNetwork = 2;
    private const int InterNetworkV6 = 23;
    private const int TableOwnerPidConnections = 4; // TCP_TABLE_OWNER_PID_CONNECTIONS
    private const uint InsufficientBuffer = 122;
    private const int Ipv4RowSize = 24; // MIB_TCPROW_OWNER_PID
    private const int Ipv6RowSize = 56; // MIB_TCP6ROW_OWNER_PID

    internal readonly record struct PeerProcess(long CreationTime, bool Running, bool Elevated);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool IsElevated(IPEndPoint peer, IPEndPoint listener)
    {
        // A process created after this moment cannot own a connection accepted before it (PID reuse).
        var accepted = DateTime.UtcNow.ToFileTimeUtc();
        return IsElevated(() => Owner(peer, listener), HoldOpen, accepted);
    }

    // owner reads the connection's owning process id; holdOpen opens that process, reads it and, still holding it,
    // calls its second argument, which tells whether the connection still belongs to that id. null: unknown or failed.
    internal static bool IsElevated(Func<int?> owner, Func<int, Func<bool>, PeerProcess?> holdOpen, long acceptedAt)
    {
        if (owner() is not { } id || id <= 0) return false;
        return holdOpen(id, () => owner() == id) is { } process && process.Running && process.Elevated &&
            process.CreationTime <= acceptedAt;
    }

    // The owning process id of the client's end of the connection (local = peer, remote = listener) in either table; null
    // when there is none, or when both tables name different processes.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static int? Owner(IPEndPoint peer, IPEndPoint listener)
    {
        int? found = null;
        foreach (var family in (ReadOnlySpan<int>)[InterNetwork, InterNetworkV6])
        {
            if (Table(family) is not { } table) continue;
            if (FindOwner(table, family == InterNetworkV6, peer, listener) is not { } owner) continue;
            if (found is { } earlier && earlier != owner) return null;
            found = owner;
        }
        return found;
    }

    // Reads one table (MIB_TCPTABLE_OWNER_PID or MIB_TCP6TABLE_OWNER_PID): a row count, then rows whose ports are in
    // network byte order in the low 16 bits of a DWORD. Two rows of the connection with different owners read as unknown.
    internal static int? FindOwner(ReadOnlySpan<byte> table, bool ipv6, IPEndPoint peer, IPEndPoint listener)
    {
        if (table.Length < 4) return null;
        var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(table);
        var size = ipv6 ? Ipv6RowSize : Ipv4RowSize;
        if (count > (uint)((table.Length - 4) / size)) return null;
        peer = LoopbackPeerAdmission.Normalize(peer);
        listener = LoopbackPeerAdmission.Normalize(listener);
        int? owner = null;
        for (var index = 0; index < (int)count; index++)
        {
            var row = table.Slice(4 + index * size, size);
            IPAddress localAddress, remoteAddress;
            int localPort, remotePort;
            uint process;
            if (ipv6)
            {
                // ucLocalAddr[16], dwLocalScopeId, dwLocalPort, ucRemoteAddr[16], dwRemoteScopeId, dwRemotePort, dwState,
                // dwOwningPid.
                localAddress = Mapped(new IPAddress(row[..16]));
                localPort = Port(row[20..]);
                remoteAddress = Mapped(new IPAddress(row.Slice(24, 16)));
                remotePort = Port(row[44..]);
                process = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(row[52..]);
            }
            else
            {
                // dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort, dwOwningPid.
                localAddress = new IPAddress(row.Slice(4, 4));
                localPort = Port(row[8..]);
                remoteAddress = new IPAddress(row.Slice(12, 4));
                remotePort = Port(row[16..]);
                process = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(row[20..]);
            }
            if (localPort != peer.Port || remotePort != listener.Port ||
                !localAddress.Equals(peer.Address) || !remoteAddress.Equals(listener.Address))
                continue;
            if (process is 0 or > int.MaxValue) return null;
            if (owner is { } earlier && earlier != (int)process) return null;
            owner = (int)process;
        }
        return owner;

        static int Port(ReadOnlySpan<byte> value) => value[0] << 8 | value[1];
        static IPAddress Mapped(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[]? Table(int family)
    {
        uint size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = size == 0 ? null : new byte[size];
            uint result;
            unsafe
            {
                fixed (byte* data = buffer)
                    result = GetExtendedTcpTable(data, ref size, false, family, TableOwnerPidConnections, 0);
            }
            if (result == 0) return buffer;
            if (result != InsufficientBuffer || size is 0 or > 64 * 1024 * 1024) return null;
            // The table grew meanwhile: room for more rows.
            size += 16 * Ipv6RowSize;
        }
        return null;
    }

    // Opens the process, reads its creation time, whether it still runs and whether its token is elevated, and asks
    // stillOwner while the handle is open. null when it cannot be opened or read, or no longer owns the connection.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static PeerProcess? HoldOpen(int processId, Func<bool> stillOwner)
    {
        var process = OpenProcess(WindowsProcessTokens.QueryLimitedInformation, false, (uint)processId);
        if (process == 0) return null;
        try
        {
            if (!GetProcessTimes(process, out var created, out _, out _, out _)) return null;
            var running = GetExitCodeProcess(process, out var exitCode) && exitCode == StillActive;
            var elevated = WindowsProcessTokens.Elevated(process);
            return stillOwner() ? new PeerProcess(created, running, elevated) : null;
        }
        finally { _ = CloseHandle(process); }
    }

    private const uint StillActive = 259;

    [LibraryImport("iphlpapi")]
    private static unsafe partial uint GetExtendedTcpTable(byte* table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int family, int tableClass, uint reserved);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

// Whether a Windows process's token is elevated: TOKEN_ELEVATION.TokenIsElevated, or a mandatory label of High
// integrity or above (TOKEN_MANDATORY_LABEL: the last subauthority of its SID); false when neither can be read. Shared
// by the elevated pair's parent check (Runic.Application.Views.Desktop) and elevated-peer admission.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static partial class WindowsProcessTokens
{
    internal const uint QueryLimitedInformation = 0x1000; // PROCESS_QUERY_LIMITED_INFORMATION
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;
    private const int TokenIntegrityLevelClass = 25;
    private const uint HighIntegrity = 0x3000; // SECURITY_MANDATORY_HIGH_RID

    // The process's own token; false when it cannot be opened.
    internal static bool Elevated(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token)) return false;
        try { return TokenElevated(token); }
        finally { _ = CloseHandle(token); }
    }

    internal static unsafe bool TokenElevated(nint token)
    {
        uint elevation = 0;
        if (GetTokenInformation(token, TokenElevationClass, &elevation, sizeof(uint), out _) && elevation != 0) return true;
        _ = GetTokenInformation(token, TokenIntegrityLevelClass, null, 0, out var size);
        if (size is 0 or > 256) return false;
        var buffer = stackalloc byte[(int)size];
        if (!GetTokenInformation(token, TokenIntegrityLevelClass, buffer, size, out _)) return false;
        var sid = *(nint*)buffer;
        if (sid == 0 || !IsValidSid(sid)) return false;
        var count = *GetSidSubAuthorityCount(sid);
        return count > 0 && *GetSidSubAuthority(sid, (uint)(count - 1)) >= HighIntegrity;
    }

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(nint token, int informationClass, void* information, uint length, out uint returned);

    [LibraryImport("advapi32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsValidSid(nint sid);

    [LibraryImport("advapi32")]
    private static unsafe partial byte* GetSidSubAuthorityCount(nint sid);

    [LibraryImport("advapi32")]
    private static unsafe partial uint* GetSidSubAuthority(nint sid, uint index);

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
