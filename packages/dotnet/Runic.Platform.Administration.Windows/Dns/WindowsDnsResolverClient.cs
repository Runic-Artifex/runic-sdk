using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.Dns;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.Dns;

/// <summary>DNS resolver queries sent to one chosen DNS server, not the computer's configured resolvers
/// (<c>DnsQueryEx</c> with a server list).</summary>
/// <remarks>Queries go over the network only: the resolver cache, the hosts file, LLMNR and NetBIOS are not used, and
/// the name is not extended with DNS suffixes. Windows chooses UDP or TCP and its retry timeouts; a server that does not
/// answer is <see cref="AdministrationErrorCategory.Unavailable"/> (native 1460). The query runs off the caller's
/// thread; cancellation before it starts prevents it, and once it starts its actual result is returned. Requires
/// Windows x64.</remarks>
public sealed class WindowsDnsResolverClient
{
    private readonly IPAddress _server;

    /// <summary>Creates a client that queries <paramref name="server"/> on port 53, for example a domain controller.</summary>
    public WindowsDnsResolverClient(IPAddress server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (server.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentException("The server must be an IPv4 or IPv6 address.", nameof(server));
        Automation.RequireX64();
        _server = server;
    }

    /// <summary>Queries the server for records of one type. Name errors and other response codes are results.</summary>
    public Task<DnsResolution> QueryAsync(string name, DnsRecordType type, CancellationToken cancellationToken = default)
    {
        NativeError.Text(name, nameof(name));
        if ((int)type is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(type));
        return NativeWork.RunAsync(() => Query(name, type), cancellationToken);
    }

    /// <summary>Queries the server for the PTR records of an address (reverse lookup).</summary>
    public Task<DnsResolution> ReverseLookupAsync(IPAddress address, CancellationToken cancellationToken = default) =>
        QueryAsync(ReverseLookupName(address), DnsRecordType.Ptr, cancellationToken);

    /// <summary>The reverse lookup name of an address: <c>4.3.2.1.in-addr.arpa</c> for 1.2.3.4, and the reversed
    /// nibbles under <c>ip6.arpa</c> for an IPv6 address.</summary>
    /// <remarks>An IPv4-mapped IPv6 address uses its IPv4 name.</remarks>
    public static string ReverseLookupName(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        var text = new StringBuilder();
        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                for (var i = bytes.Length - 1; i >= 0; i--) text.Append(bytes[i].ToString(CultureInfo.InvariantCulture)).Append('.');
                return text.Append("in-addr.arpa").ToString();
            case AddressFamily.InterNetworkV6:
                for (var i = bytes.Length - 1; i >= 0; i--)
                    text.Append("0123456789abcdef"[bytes[i] & 0xf]).Append('.').Append("0123456789abcdef"[bytes[i] >> 4]).Append('.');
                return text.Append("ip6.arpa").ToString();
            default:
                throw new ArgumentException("The address must be IPv4 or IPv6.", nameof(address));
        }
    }

    // DNS_QUERY_WIRE_ONLY (no cache, hosts file or local names), DNS_QUERY_NO_MULTICAST, DNS_QUERY_TREAT_AS_FQDN,
    // DNS_QUERY_NO_NETBT.
    private const ulong Options = 0x100 | 0x800 | 0x1000 | 0x80;

    private unsafe DnsResolution Query(string name, DnsRecordType type)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException("Windows 10 or later is required.");
        DNS_ADDR_ARRAY servers = default;
        servers.MaxCount = 1;
        servers.AddrCount = 1;
        servers.Family = (ushort)(_server.AddressFamily == AddressFamily.InterNetwork ? 2 : 23);
        WriteSocketAddress(_server, new Span<byte>(&servers.AddrArray.e0.MaxSa, 32));
        fixed (char* queryName = name)
        {
            var request = new DNS_QUERY_REQUEST
            {
                Version = 1,
                QueryName = new PCWSTR(queryName),
                QueryType = (ushort)type,
                QueryOptions = Options,
                pDnsServerList = &servers
            };
            var result = new DNS_QUERY_RESULT { Version = 1 };
            var status = PInvoke.DnsQueryEx(&request, &result, null);
            var records = (DNS_RECORDW*)result.pQueryRecords;
            try { return Resolution(status, records); }
            finally { if (records != null) PInvoke.DnsFree(records, DNS_FREE_TYPE.DnsFreeRecordList); }
        }
    }

    /// <summary>Writes a SOCKADDR_IN or SOCKADDR_IN6 for port 53.</summary>
    internal static void WriteSocketAddress(IPAddress address, Span<byte> target)
    {
        target.Clear();
        var family = address.AddressFamily == AddressFamily.InterNetwork ? (ushort)2 : (ushort)23;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(target, family);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(target[2..], 53);
        if (family == 2) address.TryWriteBytes(target[4..8], out _);
        else
        {
            // sin6_flowinfo (4 bytes) precedes the address; sin6_scope_id follows it.
            address.TryWriteBytes(target[8..24], out _);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(target[24..], unchecked((uint)address.ScopeId));
        }
    }

    /// <summary>Maps a DnsQueryEx status and its record list to a resolution. DNS_ERROR_RCODE_* (9000 plus the RCODE)
    /// are response codes and DNS_INFO_NO_RECORDS (9501) an empty answer; other statuses are failures.</summary>
    internal static unsafe DnsResolution Resolution(int status, DNS_RECORDW* records)
    {
        if (status is 0) return new(DnsResponseCode.NoError, Answers(records));
        if (status is 9501) return new(DnsResponseCode.NoError, []);
        if (status is > 9000 and <= 9023) return new((DnsResponseCode)(status - 9000), []);
        throw NativeError.Win32("Query DNS server", status);
    }

    internal static unsafe ImmutableArray<DnsAnswer> Answers(DNS_RECORDW* record)
    {
        var answers = ImmutableArray.CreateBuilder<DnsAnswer>();
        for (; record != null; record = record->pNext)
        {
            // DNS_RECORD_FLAGS.Section: 1 is the answer section.
            if ((record->Flags.DW & 3) != 1) continue;
            answers.Add(new(Name(record->pName), (DnsRecordType)record->wType, TimeSpan.FromSeconds(record->dwTtl), Data(record)));
        }
        return answers.ToImmutable();
    }

    private static unsafe DnsRecordData Data(DNS_RECORDW* record)
    {
        ref var data = ref record->Data;
        return (DnsRecordType)record->wType switch
        {
            DnsRecordType.A => new DnsRecordData.A(new IPAddress(data.A.IpAddress)),
            DnsRecordType.Aaaa => new DnsRecordData.Aaaa(new IPAddress(new ReadOnlySpan<byte>(&record->Data.AAAA.Ip6Address, 16))),
            DnsRecordType.CName => new DnsRecordData.CName(Name(data.CNAME.pNameHost)),
            DnsRecordType.Ptr => new DnsRecordData.Ptr(Name(data.PTR.pNameHost)),
            DnsRecordType.Ns => new DnsRecordData.Ns(Name(data.NS.pNameHost)),
            DnsRecordType.Mx => new DnsRecordData.Mx(data.MX.wPreference, Name(data.MX.pNameExchange)),
            DnsRecordType.Srv => new DnsRecordData.Srv(data.SRV.wPriority, data.SRV.wWeight, data.SRV.wPort, Name(data.SRV.pNameTarget)),
            DnsRecordType.Txt => new DnsRecordData.Txt(Text(ref data.TXT)),
            _ => new DnsRecordData.Unsupported("TYPE" + record->wType.ToString(CultureInfo.InvariantCulture), "")
        };
    }

    private static unsafe string Text(ref DNS_TXT_DATAW text)
    {
        var result = new StringBuilder();
        var strings = text.pStringArray.AsSpan(checked((int)text.dwStringCount));
        foreach (var value in strings)
        {
            if (result.Length != 0) result.Append(' ');
            result.Append('"').Append(Name(value).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
        }
        return result.ToString();
    }

    private static unsafe string Name(PWSTR value) => value.Value == null ? "" : value.ToString();
}
