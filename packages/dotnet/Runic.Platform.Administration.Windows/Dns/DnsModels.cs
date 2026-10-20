using System.Net;

namespace Runic.Platform.Administration.Windows.Dns;

/// <summary>DNS zone metadata read from a specific Windows DNS server.</summary>
public sealed record DnsZoneSnapshot(string Name, bool ActiveDirectoryIntegrated);

/// <summary>Typed DNS record data. Unsupported native types retain their original textual representation.</summary>
public abstract record DnsRecordData
{
    private DnsRecordData() { }
    /// <summary>IPv4 address record. Writes reject an address that is not IPv4.</summary>
    public sealed record A(IPAddress Address) : DnsRecordData;
    /// <summary>IPv6 address record. Writes reject an address that is not IPv6.</summary>
    public sealed record Aaaa(IPAddress Address) : DnsRecordData;
    /// <summary>Canonical name record.</summary>
    public sealed record CName(string Target) : DnsRecordData;
    /// <summary>Pointer record, normally used for reverse lookup.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "PTR is the DNS record mnemonic, like A, Aaaa, CName and Mx.")]
    public sealed record Ptr(string Target) : DnsRecordData;
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
/// <summary>A DNS record with its TTL and native record class.</summary>
/// <param name="Key">The record identity.</param>
/// <param name="TimeToLive">The TTL, in whole seconds.</param>
/// <param name="NativeRecordClass">The native DNS class number; 1 is IN.</param>
public sealed record DnsRecordSnapshot(DnsRecordKey Key, TimeSpan TimeToLive, uint NativeRecordClass);
/// <summary>Creates a single record without replacing its record set.</summary>
public sealed record DnsRecordSpecification(DnsRecordKey Key)
{
    /// <summary>The TTL for the new record, one hour by default. Null omits it so the DNS server chooses the TTL.</summary>
    /// <remarks>Must be whole seconds from zero to <see cref="uint.MaxValue"/> seconds.</remarks>
    public TimeSpan? TimeToLive { get; init; } = TimeSpan.FromHours(1);
}
/// <summary>Selected record edits. Data must retain the existing record type; null preserves a value.</summary>
public sealed record DnsRecordUpdate
{
    /// <summary>Replacement TTL in whole seconds, from zero to <see cref="uint.MaxValue"/> seconds; null preserves the existing TTL.</summary>
    public TimeSpan? TimeToLive { get; init; }
    /// <summary>Replacement record data of the same type.</summary>
    public DnsRecordData? Data { get; init; }
}
