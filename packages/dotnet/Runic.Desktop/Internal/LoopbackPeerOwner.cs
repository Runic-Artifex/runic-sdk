using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// Finds which user owns the client end of a loopback TCP connection on Linux (SEC-01). Every user can read
// /proc/net/tcp and /proc/net/tcp6, which list each socket of the network namespace with its local and remote
// address and its owner's UID; the client's socket is the one whose local address is the server's remote
// endpoint and whose remote address is the server's local endpoint. Null when the owner is unknown: another
// system, no /proc, or a connection that has already closed.
internal static partial class LoopbackPeerOwner
{
    internal static uint CurrentUser => GetEffectiveUserId();

    internal static uint? Find(IPAddress clientAddress, int clientPort, IPAddress serverAddress, int serverPort)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        // A dual-mode client socket lists an IPv4 connection in tcp6, as a mapped address.
        uint? owner = null;
        foreach (var table in (ReadOnlySpan<string>)["/proc/net/tcp", "/proc/net/tcp6"])
        {
            try
            {
                using var reader = new StreamReader(table);
                if (Find(reader, clientAddress, clientPort, serverAddress, serverPort) is not { } found)
                {
                    continue;
                }
                if (owner is { } earlier && earlier != found)
                {
                    return null;
                }
                owner = found;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
        return owner;
    }

    // Reads one table. Rows that do not parse are skipped; two different owners for the same connection are
    // reported as unknown.
    internal static uint? Find(TextReader table, IPAddress clientAddress, int clientPort, IPAddress serverAddress, int serverPort)
    {
        uint? owner = null;
        clientAddress = Normalize(clientAddress);
        serverAddress = Normalize(serverAddress);
        _ = table.ReadLine(); // The header.
        while (table.ReadLine() is { } line)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // sl local_address rem_address st tx_queue:rx_queue tr:tm->when retrnsmt uid ...
            if (fields.Length < 8 ||
                !TryParseEndpoint(fields[1], out var localAddress, out var localPort) ||
                !TryParseEndpoint(fields[2], out var remoteAddress, out var remotePort) ||
                !uint.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
            {
                continue;
            }
            if (localPort != clientPort || remotePort != serverPort ||
                !localAddress.Equals(clientAddress) || !remoteAddress.Equals(serverAddress))
            {
                continue;
            }
            if (owner is { } found && found != uid)
            {
                return null;
            }
            owner = uid;
        }
        return owner;
    }

    // The kernel prints each 32-bit word of the address in host byte order and the port in hexadecimal.
    private static bool TryParseEndpoint(string text, out IPAddress address, out int port)
    {
        address = IPAddress.None;
        port = 0;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon is not (8 or 32) ||
            !int.TryParse(text.AsSpan(colon + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out port))
        {
            return false;
        }
        var bytes = new byte[colon / 2];
        for (var word = 0; word < colon / 8; word++)
        {
            if (!uint.TryParse(text.AsSpan(word * 8, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }
            BitConverter.TryWriteBytes(bytes.AsSpan(word * 4, 4), value);
        }
        address = Normalize(new IPAddress(bytes));
        return true;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}
