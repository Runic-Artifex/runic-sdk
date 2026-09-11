using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.Dns;

/// <summary>Windows DNS server administration through its MicrosoftDNS provider.</summary>
/// <remarks>The server must have the DNS role/provider installed. Uses the caller's identity unless credentials are supplied.</remarks>
public sealed class WindowsDnsClient
{
    private readonly string _server;
    private readonly NetworkCredential? _credential;
    private readonly TimeSpan _timeout;

    /// <summary>Creates an explicit server client. Timeout bounds WMI query waits; it is not a hard cancellation deadline for server mutations.</summary>
    public WindowsDnsClient(string server, NetworkCredential? credential = null, TimeSpan? queryTimeout = null)
    {
        Automation.RequireX64();
        NativeError.Text(server, nameof(server));
        if (server.Contains('\\') || server.Contains('/')) throw new ArgumentException("Supply a server hostname.", nameof(server));
        _server = server;
        _credential = credential is null ? null : new(credential.UserName, credential.Password, credential.Domain);
        _timeout = queryTimeout ?? TimeSpan.FromSeconds(30);
        if (_timeout < TimeSpan.FromSeconds(1) || _timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(queryTimeout));
    }

    /// <summary>Enumerates zones and their AD integration status.</summary>
    public Task<ImmutableArray<DnsZoneSnapshot>> EnumerateZonesAsync(CancellationToken cancellationToken = default) =>
        Execute(connection => connection.Query("SELECT Name, DsIntegrated FROM MicrosoftDNS_Zone", ["Name", "DsIntegrated"], cancellationToken)
            .Select(row => new DnsZoneSnapshot(Text(row, "Name"), row["DsIntegrated"] is true)).ToImmutableArray(), cancellationToken);

    /// <summary>Finds a zone; null means the named zone is absent on this server.</summary>
    public Task<DnsZoneSnapshot?> FindZoneAsync(string name, CancellationToken cancellationToken = default)
    {
        NativeError.Text(name, nameof(name));
        return Execute(connection =>
        {
            var rows = connection.Query("SELECT Name, DsIntegrated FROM MicrosoftDNS_Zone WHERE Name='" + Escape(name) + "'", ["Name", "DsIntegrated"], cancellationToken);
            if (rows.Length > 1) throw NativeError.Win32("Find unique DNS zone", 183);
            return rows.IsEmpty ? null : new DnsZoneSnapshot(Text(rows[0], "Name"), rows[0]["DsIntegrated"] is true);
        }, cancellationToken);
    }

    /// <summary>Enumerates records, including unsupported native types as preserved textual records.</summary>
    public Task<ImmutableArray<DnsRecordSnapshot>> EnumerateRecordsAsync(string zone, string? ownerName = null, CancellationToken cancellationToken = default)
    {
        NativeError.Text(zone, nameof(zone));
        if (ownerName is not null) NativeError.Text(ownerName, nameof(ownerName));
        return Execute(connection => ReadRecords(connection, zone, ownerName, cancellationToken).Select(row => row.Snapshot).ToImmutableArray(), cancellationToken);
    }

    /// <summary>Finds a unique record by full record data, without assuming an owner name identifies a whole record set.</summary>
    public Task<DnsRecordSnapshot?> FindAsync(DnsRecordKey key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        return Execute(connection => Find(connection, key, cancellationToken)?.Snapshot, cancellationToken);
    }

    /// <summary>Creates one record. Existing identical data is a conflict; other records in the set are preserved.</summary>
    public Task CreateAsync(DnsRecordSpecification specification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ValidateKey(specification.Key);
        return Execute(connection =>
        {
            if (Find(connection, specification.Key, cancellationToken) is not null) throw NativeError.Win32("Create DNS record", 183);
            var values = Values(specification.Key.Data);
            values.Add("DnsServerName", _server);
            values.Add("ContainerName", specification.Key.Zone);
            values.Add("OwnerName", specification.Key.OwnerName);
            values.Add("RecordClass", 1u);
            if (!specification.UseServerDefaultTimeToLive) values.Add("TTL", specification.TimeToLiveSeconds);
            cancellationToken.ThrowIfCancellationRequested();
            connection.Invoke(ClassName(specification.Key.Data), "CreateInstanceFromPropertyData", values);
            return true;
        }, cancellationToken);
    }

    /// <summary>Modifies a single record in place. Record type and owner remain unchanged.</summary>
    public Task UpdateAsync(DnsRecordKey key, DnsRecordUpdate update, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(update);
        if (update.Data is not null && ClassName(key.Data) != ClassName(update.Data)) throw new ArgumentException("Record type cannot change in-place.", nameof(update));
        var replacement = update.Data ?? key.Data;
        _ = Values(replacement);
        return Execute(connection =>
        {
            var existing = Find(connection, key, cancellationToken) ?? throw NativeError.Win32("Update DNS record", 2);
            if (update.Data is not null && update.Data != key.Data && Find(connection, key with { Data = update.Data }, cancellationToken) is not null)
                throw NativeError.Win32("Update DNS record", 183);
            var values = Values(replacement);
            values.Add("TTL", update.TimeToLiveSeconds ?? existing.Snapshot.TimeToLiveSeconds);
            cancellationToken.ThrowIfCancellationRequested();
            connection.Invoke(existing.Path, "Modify", values);
            return true;
        }, cancellationToken);
    }

    /// <summary>Deletes exactly one matching record. Returns false when absent; never removes a complete record set implicitly.</summary>
    public Task<bool> DeleteAsync(DnsRecordKey key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        return Execute(connection =>
        {
            var existing = Find(connection, key, cancellationToken);
            if (existing is null) return false;
            cancellationToken.ThrowIfCancellationRequested();
            connection.Delete(existing.Path);
            return true;
        }, cancellationToken);
    }

    private Task<T> Execute<T>(Func<WmiConnection, T> action, CancellationToken cancellationToken) =>
        ComApartment.RunAsync(() =>
        {
            using var connection = new WmiConnection(_server, @"root\MicrosoftDNS", _credential, _timeout);
            cancellationToken.ThrowIfCancellationRequested();
            return action(connection);
        }, cancellationToken);

    private sealed record NativeRecord(string Path, DnsRecordSnapshot Snapshot);

    private static ImmutableArray<NativeRecord> ReadRecords(WmiConnection connection, string zone, string? ownerName, CancellationToken cancellationToken)
    {
        var query = "SELECT * FROM MicrosoftDNS_ResourceRecord WHERE ContainerName='" + Escape(zone) + "'";
        if (ownerName is not null) query += " AND OwnerName='" + Escape(ownerName) + "'";
        var rows = connection.Query(query, ["__PATH", "__CLASS", "ContainerName", "OwnerName", "TTL", "RecordClass", "TextRepresentation"], cancellationToken);
        return rows.Select(row =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var className = Text(row, "__CLASS");
            var path = Text(row, "__PATH");
            var data = ReadData(connection, className, path, Text(row, "TextRepresentation"));
            return new NativeRecord(path, new(new(Text(row, "ContainerName"), Text(row, "OwnerName"), data), Number(row, "TTL"), Number(row, "RecordClass")));
        }).ToImmutableArray();
    }

    private static NativeRecord? Find(WmiConnection connection, DnsRecordKey key, CancellationToken cancellationToken)
    {
        var matches = ReadRecords(connection, key.Zone, key.OwnerName, cancellationToken)
            .Where(record => DataEquals(record.Snapshot.Key.Data, key.Data)).Take(2).ToArray();
        if (matches.Length > 1) throw NativeError.Win32("Select unique DNS record", 183);
        return matches.SingleOrDefault();
    }

    private static bool DataEquals(DnsRecordData left, DnsRecordData right) => (left, right) switch
    {
        (DnsRecordData.A a, DnsRecordData.A b) => IPAddress.Parse(a.Address).Equals(IPAddress.Parse(b.Address)),
        (DnsRecordData.Aaaa a, DnsRecordData.Aaaa b) => IPAddress.Parse(a.Address).Equals(IPAddress.Parse(b.Address)),
        (DnsRecordData.CName a, DnsRecordData.CName b) => DnsNameEquals(a.Target, b.Target),
        (DnsRecordData.ReverseLookup a, DnsRecordData.ReverseLookup b) => DnsNameEquals(a.Target, b.Target),
        (DnsRecordData.Ns a, DnsRecordData.Ns b) => DnsNameEquals(a.Host, b.Host),
        (DnsRecordData.Mx a, DnsRecordData.Mx b) => a.Preference == b.Preference && DnsNameEquals(a.Exchange, b.Exchange),
        (DnsRecordData.Srv a, DnsRecordData.Srv b) => a.Priority == b.Priority && a.Weight == b.Weight && a.Port == b.Port && DnsNameEquals(a.Target, b.Target),
        _ => left == right
    };

    private static bool DnsNameEquals(string left, string right) => string.Equals(left.TrimEnd('.'), right.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static DnsRecordData ReadData(WmiConnection connection, string nativeClass, string path, string representation)
    {
        var fields = nativeClass.ToUpperInvariant() switch
        {
            "MICROSOFTDNS_ATYPE" => new[] { "IPAddress" }, "MICROSOFTDNS_AAAATYPE" => ["IPv6Address"],
            "MICROSOFTDNS_CNAMETYPE" => ["PrimaryName"], "MICROSOFTDNS_PTRTYPE" => ["PTRDomainName"],
            "MICROSOFTDNS_MXTYPE" => ["Preference", "MailExchange"], "MICROSOFTDNS_TXTTYPE" => ["DescriptiveText"],
            "MICROSOFTDNS_SRVTYPE" => ["Priority", "Weight", "Port", "SRVDomainName"], "MICROSOFTDNS_NSTYPE" => ["NSHostName"],
            _ => []
        };
        if (fields.Length == 0) return new DnsRecordData.Unsupported(nativeClass, representation);
        var values = connection.Read(path, fields);
        return nativeClass.ToUpperInvariant() switch
        {
            "MICROSOFTDNS_ATYPE" => new DnsRecordData.A(Text(values, "IPAddress")),
            "MICROSOFTDNS_AAAATYPE" => new DnsRecordData.Aaaa(Text(values, "IPv6Address")),
            "MICROSOFTDNS_CNAMETYPE" => new DnsRecordData.CName(Text(values, "PrimaryName")),
            "MICROSOFTDNS_PTRTYPE" => new DnsRecordData.ReverseLookup(Text(values, "PTRDomainName")),
            "MICROSOFTDNS_MXTYPE" => new DnsRecordData.Mx(SmallNumber(values, "Preference"), Text(values, "MailExchange")),
            "MICROSOFTDNS_TXTTYPE" => new DnsRecordData.Txt(Text(values, "DescriptiveText")),
            "MICROSOFTDNS_SRVTYPE" => new DnsRecordData.Srv(SmallNumber(values, "Priority"), SmallNumber(values, "Weight"), SmallNumber(values, "Port"), Text(values, "SRVDomainName")),
            "MICROSOFTDNS_NSTYPE" => new DnsRecordData.Ns(Text(values, "NSHostName")),
            _ => throw NativeError.Win32("Read DNS record type", 13)
        };
    }

    private static void ValidateKey(DnsRecordKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        NativeError.Text(key.Zone, nameof(key.Zone));
        NativeError.Text(key.OwnerName, nameof(key.OwnerName));
        _ = Values(key.Data);
    }

    private static string ClassName(DnsRecordData data) => data switch
    {
        DnsRecordData.A => "MicrosoftDNS_AType", DnsRecordData.Aaaa => "MicrosoftDNS_AAAAType",
        DnsRecordData.CName => "MicrosoftDNS_CNAMEType", DnsRecordData.ReverseLookup => "MicrosoftDNS_PTRType",
        DnsRecordData.Mx => "MicrosoftDNS_MXType", DnsRecordData.Txt => "MicrosoftDNS_TXTType",
        DnsRecordData.Srv => "MicrosoftDNS_SRVType", DnsRecordData.Ns => "MicrosoftDNS_NSType",
        _ => throw new ArgumentException("This record type does not support mutation.", nameof(data))
    };

    private static Dictionary<string, object> Values(DnsRecordData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _ = ClassName(data);
        Dictionary<string, object> result = data switch
        {
            DnsRecordData.A a => new() { ["IPAddress"] = Address(a.Address, AddressFamily.InterNetwork) },
            DnsRecordData.Aaaa a => new() { ["IPv6Address"] = Address(a.Address, AddressFamily.InterNetworkV6) },
            DnsRecordData.CName c => new() { ["PrimaryName"] = c.Target },
            DnsRecordData.ReverseLookup p => new() { ["PTRDomainName"] = p.Target },
            DnsRecordData.Mx m => new() { ["Preference"] = (uint)m.Preference, ["MailExchange"] = m.Exchange },
            DnsRecordData.Txt t => new() { ["DescriptiveText"] = t.DescriptiveText },
            DnsRecordData.Srv s => new() { ["Priority"] = (uint)s.Priority, ["Weight"] = (uint)s.Weight, ["Port"] = (uint)s.Port, ["SRVDomainName"] = s.Target },
            DnsRecordData.Ns n => new() { ["NSHostName"] = n.Host },
            _ => throw new ArgumentException("Unsupported DNS record.", nameof(data))
        };
        foreach (var value in result.Values.OfType<string>()) NativeError.Text(value, nameof(data), data is DnsRecordData.Txt);
        return result;
    }

    private static string Address(string value, AddressFamily family)
    {
        if (!IPAddress.TryParse(value, out var parsed) || parsed.AddressFamily != family) throw new ArgumentException("Address family does not match the record.", nameof(value));
        return parsed.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
    private static string Text(ImmutableDictionary<string, object?> row, string name) =>
        row[name] as string ?? throw NativeError.Win32("Read DNS text property", 13);
    private static ushort SmallNumber(ImmutableDictionary<string, object?> row, string name)
    {
        var value = Number(row, name);
        return value <= ushort.MaxValue ? (ushort)value : throw NativeError.Win32("Read DNS 16-bit property", 13);
    }

    private static uint Number(ImmutableDictionary<string, object?> row, string name) => row[name] switch
    {
        uint number => number, int number => unchecked((uint)number),
        ushort number => number, short number => unchecked((ushort)number),
        _ => throw NativeError.Win32("Read DNS numeric property", 13)
    };
}
