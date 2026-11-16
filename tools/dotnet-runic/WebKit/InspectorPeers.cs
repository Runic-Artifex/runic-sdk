using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;

namespace Runic.Application.Tool.WebKit;

/// <summary>
/// The TCP connections to the Window's WebKit inspector server, from Linux's <c>/proc/net/tcp</c> and
/// <c>/proc/net/tcp6</c>: the port at the other end of each connection to the server's address, open or recently closed
/// (TIME_WAIT keeps a closed one listed for about a minute). WebKit tells a client nothing when another client takes its
/// slot, so a connection that was not there before is how the adapter learns that it may have been replaced.
/// </summary>
internal static class InspectorPeers
{
    private const string Listen = "0A";

    /// <summary>The peer ports of <paramref name="host"/>:<paramref name="port"/>; empty where the tables cannot be read.</summary>
    internal static HashSet<int> Read(IPAddress host, int port)
    {
        var peers = new HashSet<int>();
        foreach (string table in (string[])["/proc/net/tcp", "/proc/net/tcp6"])
        {
            try
            {
                peers.UnionWith(Parse(File.ReadAllText(table), host, port));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
        return peers;
    }

    /// <summary>
    /// The peer ports in one table. Each line has the local and the remote address as hexadecimal <c>address:port</c>:
    /// the address as the kernel stores it (32-bit words in host byte order), the port as a number. A connection to the
    /// server is listed from both ends, so a peer is a port connected to the server's address and port, either way.
    /// </summary>
    internal static HashSet<int> Parse(string table, IPAddress host, int port)
    {
        var peers = new HashSet<int>();
        IPAddress server = Normalize(host);
        foreach (string line in table.Split('\n'))
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[3] == Listen) continue;
            if (!TryParseEndpoint(fields[1], out IPAddress? localAddress, out int localPort)
                || !TryParseEndpoint(fields[2], out IPAddress? remoteAddress, out int remotePort)) continue;
            if (localPort == port && localAddress.Equals(server)) peers.Add(remotePort);
            else if (remotePort == port && remoteAddress.Equals(server)) peers.Add(localPort);
        }
        return peers;
    }

    private static bool TryParseEndpoint(string text, out IPAddress address, out int port)
    {
        address = IPAddress.None;
        port = 0;
        int colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon is not (8 or 32) || !int.TryParse(text.AsSpan(colon + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out port))
            return false;
        var bytes = new byte[colon / 2];
        for (int word = 0; word < colon / 8; word++)
        {
            if (!uint.TryParse(text.AsSpan(word * 8, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value)) return false;
            // The kernel prints each word of the address as an integer in host byte order.
            BitConverter.GetBytes(value).CopyTo(bytes, word * 4);
        }
        address = Normalize(new IPAddress(bytes));
        return true;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
