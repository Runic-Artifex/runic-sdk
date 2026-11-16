using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;

namespace Runic.Application.Tool.WebKit;

/// <summary>
/// Which user owns a TCP socket on this machine, from Linux's <c>/proc/net/tcp</c> and <c>/proc/net/tcp6</c>. Loopback
/// TCP admits every local user, so the debugging endpoints use it to accept only the user who runs <c>dotnet runic
/// dev</c>: the CDP adapter checks the socket at the other end of each connection, and the adapter checks the listener of
/// WebKit's inspector port before it sends anything there.
/// </summary>
internal sealed partial class LoopbackSockets
{
    private const string Ipv4Table = "/proc/net/tcp";
    private const string Ipv6Table = "/proc/net/tcp6";

    /// <summary><c>TCP_LISTEN</c> in the tables' <c>st</c> column.</summary>
    internal const int ListenState = 0x0A;

    /// <summary><c>TCP_TIME_WAIT</c>: such a socket has no owner any more, and the tables show uid 0 for it.</summary>
    internal const int TimeWaitState = 0x06;

    private readonly Func<string, string?> _readTable;

    internal LoopbackSockets(Func<string, string?> readTable, uint user)
    {
        _readTable = readTable;
        User = user;
    }

    /// <summary>This process's sockets on Linux; null elsewhere, where dev opens no WebKit debugging endpoint.</summary>
    internal static LoopbackSockets? Current { get; } = OperatingSystem.IsLinux() ? new(ReadTable, GetEffectiveUserId()) : null;

    /// <summary>The effective uid of the user who runs dev.</summary>
    internal uint User { get; }

    internal readonly record struct Entry(IPEndPoint Local, IPEndPoint Remote, int State, uint Uid);

    /// <summary>
    /// The owner of the socket at the other end of a connection whose ends are <paramref name="local"/> and
    /// <paramref name="remote"/>, or null when it is not listed (the peer closed already, or the tables cannot be read).
    /// </summary>
    internal uint? PeerOwner(IPEndPoint local, IPEndPoint remote)
    {
        IPEndPoint peerLocal = Normalize(remote);
        IPEndPoint peerRemote = Normalize(local);
        foreach (Entry entry in Read())
        {
            if (entry.State != TimeWaitState && entry.State != ListenState && entry.Local.Equals(peerLocal) && entry.Remote.Equals(peerRemote))
            {
                return entry.Uid;
            }
        }
        return null;
    }

    /// <summary>
    /// The owners of the listeners that a connection to <paramref name="address"/>:<paramref name="port"/> can reach: those
    /// bound to that address or to a wildcard address. Empty when nothing listens or the tables cannot be read.
    /// </summary>
    internal IReadOnlyList<uint> ListenerOwners(IPAddress address, int port)
    {
        IPAddress target = Normalize(address);
        return Read()
            .Where(entry => entry.State == ListenState && entry.Local.Port == port &&
                (entry.Local.Address.Equals(target) || entry.Local.Address.Equals(IPAddress.Any) || entry.Local.Address.Equals(IPAddress.IPv6Any)))
            .Select(static entry => entry.Uid)
            .Distinct()
            .ToList();
    }

    /// <summary>A listener of another user on that address and port, if any.</summary>
    internal uint? ForeignListener(IPAddress address, int port) =>
        ListenerOwners(address, port).Cast<uint?>().FirstOrDefault(owner => owner != User);

    private IEnumerable<Entry> Read() => Parse(_readTable(Ipv4Table)).Concat(Parse(_readTable(Ipv6Table)));

    /// <summary>
    /// The rows of a <c>/proc/net/tcp</c> or <c>/proc/net/tcp6</c> table. Addresses are hexadecimal 32-bit words in the
    /// kernel's byte order, ports hexadecimal; IPv4-mapped IPv6 addresses become IPv4. Rows that do not parse are skipped.
    /// </summary>
    internal static IReadOnlyList<Entry> Parse(string? table)
    {
        var entries = new List<Entry>();
        if (string.IsNullOrEmpty(table)) return entries;
        foreach (string line in table.Split('\n'))
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 8 || !fields[0].EndsWith(':')) continue;
            if (TryParseEndpoint(fields[1], out IPEndPoint? local) && TryParseEndpoint(fields[2], out IPEndPoint? remote) &&
                int.TryParse(fields[3], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int state) &&
                uint.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out uint uid))
            {
                entries.Add(new Entry(local, remote, state, uid));
            }
        }
        return entries;
    }

    private static bool TryParseEndpoint(string text, out IPEndPoint endpoint)
    {
        endpoint = null!;
        int colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon is not (8 or 32) || !int.TryParse(text.AsSpan(colon + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int port)) return false;
        var bytes = new byte[colon / 2];
        for (int word = 0; word < bytes.Length / 4; word++)
        {
            if (!uint.TryParse(text.AsSpan(word * 8, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value)) return false;
            // The kernel prints each 32-bit word of the address as it lies in memory, read as a native integer.
            MemoryMarshal.Write(bytes.AsSpan(word * 4), in value);
        }
        endpoint = new IPEndPoint(Normalize(new IPAddress(bytes)), port);
        return true;
    }

    private static IPEndPoint Normalize(IPEndPoint endpoint) => new(Normalize(endpoint.Address), endpoint.Port);

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string? ReadTable(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}
