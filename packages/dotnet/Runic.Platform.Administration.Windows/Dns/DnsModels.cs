namespace Runic.Platform.Administration.Windows.Dns;

/// <summary>DNS zone metadata read from a specific Windows DNS server.</summary>
public sealed record DnsZoneSnapshot(string Name, bool ActiveDirectoryIntegrated);

/// <summary>Typed DNS record data. Unsupported native types retain their original textual representation.</summary>
public abstract record DnsRecordData
{
    private DnsRecordData() { }
    /// <summary>IPv4 address record.</summary>
    public sealed record A(string Address) : DnsRecordData;
    /// <summary>IPv6 address record.</summary>
    public sealed record Aaaa(string Address) : DnsRecordData;
    /// <summary>Canonical name record.</summary>
    public sealed record CName(string Target) : DnsRecordData;
    /// <summary>Reverse lookup pointer.</summary>
    public sealed record ReverseLookup(string Target) : DnsRecordData;
    /// <summary>Mail exchange record.</summary>
    public sealed record Mx(ushort Preference, string Exchange) : DnsRecordData;
    /// <summary>DNS provider descriptive text, preserving its native quoted-string representation.</summary>
    public sealed record Txt(string DescriptiveText) : DnsRecordData;
    /// <summary>Service location record.</summary>
    public sealed record Srv(ushort Priority, ushort Weight, ushort Port, string Target) : DnsRecordData;
    /// <summary>Name-server record.</summary>
    public sealed record Ns(string Host) : DnsRecordData;
    /// <summary>An unsupported native record; inspection preserves text, while mutations reject it.</summary>
    public sealed record Unsupported(string NativeClass, string TextRepresentation) : DnsRecordData;
}

/// <summary>Record identity within the client's server. TTL is configuration, not record identity.</summary>
public sealed record DnsRecordKey(string Zone, string OwnerName, DnsRecordData Data);
/// <summary>A DNS record with its native class and TTL.</summary>
public sealed record DnsRecordSnapshot(DnsRecordKey Key, uint TimeToLiveSeconds, uint RecordClass);
/// <summary>Creates a single record without replacing its record set.</summary>
public sealed record DnsRecordSpecification(DnsRecordKey Key, uint TimeToLiveSeconds = 3600);
/// <summary>Selected record edits. Data must retain the existing record type; null preserves a value.</summary>
public sealed record DnsRecordUpdate
{
    /// <summary>Replacement TTL.</summary>
    public uint? TimeToLiveSeconds { get; init; }
    /// <summary>Replacement record data of the same type.</summary>
    public DnsRecordData? Data { get; init; }
}
