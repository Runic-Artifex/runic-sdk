using System.Collections.Immutable;

namespace Runic.Platform.Administration.Windows.Dns;

/// <summary>A DNS record type (an IANA RR TYPE number) for a resolver query.</summary>
/// <remarks>This set is open: the named values are the types whose answers are typed as <see cref="DnsRecordData"/>;
/// cast other numbers, for example <c>(DnsRecordType)6</c> for SOA, whose answers are
/// <see cref="DnsRecordData.Unsupported"/>.</remarks>
public enum DnsRecordType
{
    /// <summary>IPv4 address (1).</summary>
    A = 1,
    /// <summary>Name server (2).</summary>
    Ns = 2,
    /// <summary>Canonical name (5).</summary>
    CName = 5,
    /// <summary>Pointer, normally for reverse lookup (12).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "PTR is the DNS record mnemonic, as in DnsRecordData.Ptr.")]
    Ptr = 12,
    /// <summary>Mail exchange (15).</summary>
    Mx = 15,
    /// <summary>Text (16).</summary>
    Txt = 16,
    /// <summary>IPv6 address (28).</summary>
    Aaaa = 28,
    /// <summary>Service location (33).</summary>
    Srv = 33
}

/// <summary>The response code of a DNS answer (RCODE).</summary>
/// <remarks>This set is open: codes without a name here are passed through unchanged.</remarks>
public enum DnsResponseCode
{
    /// <summary>The query succeeded; the answer may still have no records of the requested type.</summary>
    NoError = 0,
    /// <summary>The server could not interpret the query (FORMERR).</summary>
    FormatError = 1,
    /// <summary>The server failed to process the query (SERVFAIL).</summary>
    ServerFailure = 2,
    /// <summary>The name does not exist (NXDOMAIN).</summary>
    NameError = 3,
    /// <summary>The server does not support the query (NOTIMP).</summary>
    NotImplemented = 4,
    /// <summary>The server refused the query (REFUSED).</summary>
    Refused = 5
}

/// <summary>A record of the answer section.</summary>
/// <param name="Name">The owner name, without a trailing dot. A CNAME chain returns each step with its own owner.</param>
/// <param name="Type">The record type, which can differ from the queried type (a CNAME for an A query).</param>
/// <param name="TimeToLive">The TTL in whole seconds.</param>
/// <param name="Data">The typed data. TXT strings are quoted and separated by spaces, with <c>"</c> and <c>\</c>
/// escaped by a backslash; types without a typed form are <see cref="DnsRecordData.Unsupported"/> with the native class
/// <c>TYPE</c> and the number, and empty text.</param>
public sealed record DnsAnswer(string Name, DnsRecordType Type, TimeSpan TimeToLive, DnsRecordData Data);

/// <summary>The response of a DNS server to one query.</summary>
/// <param name="ResponseCode">The response code. <see cref="DnsResponseCode.NameError"/> and the other error codes are
/// results, not exceptions.</param>
/// <param name="Answers">The answer section; empty for an error code or a name without records of the type.</param>
public sealed record DnsResolution(DnsResponseCode ResponseCode, ImmutableArray<DnsAnswer> Answers);
