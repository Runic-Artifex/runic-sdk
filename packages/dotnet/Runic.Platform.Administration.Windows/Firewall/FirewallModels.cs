using System.Collections.Immutable;
namespace Runic.Platform.Administration.Windows.Firewall;

/// <summary>Firewall rule direction.</summary>
public enum FirewallDirection
{
    /// <summary>Inbound traffic.</summary>
    Inbound = 1,
    /// <summary>Outbound traffic.</summary>
    Outbound = 2
}
/// <summary>Firewall rule action.</summary>
public enum FirewallAction
{
    /// <summary>Block matching traffic.</summary>
    Block = 0,
    /// <summary>Allow matching traffic.</summary>
    Allow = 1
}
/// <summary>Windows Firewall profile flags.</summary>
[Flags]
public enum FirewallProfiles
{
    /// <summary>Domain-authenticated networks.</summary>
    Domain = 1,
    /// <summary>Private networks.</summary>
    Private = 2,
    /// <summary>Public networks.</summary>
    Public = 4,
    /// <summary>All profiles, including future profiles.</summary>
    All = int.MaxValue
}
/// <summary>IP protocol of a firewall rule: an IANA protocol number, or <see cref="Any"/>.</summary>
/// <remarks>This set is open: a minor release may name more protocols. The named values are the protocols with protocol-specific settings; other IANA protocol
/// numbers from 0 to 255 are cast, for example <c>(FirewallProtocol)47</c> for GRE, and rules read from Windows can
/// return them.</remarks>
public enum FirewallProtocol
{
    /// <summary>ICMP for IPv4 (1). Allows <see cref="FirewallRuleSpecification.IcmpTypesAndCodes"/>.</summary>
    IcmpV4 = 1,
    /// <summary>TCP (6). Allows local and remote ports.</summary>
    Tcp = 6,
    /// <summary>UDP (17). Allows local and remote ports.</summary>
    Udp = 17,
    /// <summary>ICMP for IPv6 (58). Allows <see cref="FirewallRuleSpecification.IcmpTypesAndCodes"/>.</summary>
    IcmpV6 = 58,
    /// <summary>Any protocol (Windows Firewall's native value 256).</summary>
    Any = 256
}
/// <summary>Observed native identity fields, not a display label or an invented persistent GUID.</summary>
/// <remarks>Mutations reject duplicate native names even if other identity fields differ.</remarks>
public sealed record FirewallRuleIdentity(string Name, string Grouping, string ApplicationPath, string ServiceName, FirewallDirection Direction);

/// <summary>Creates a local firewall rule. Name is the native rule name; Description is explanatory display text.</summary>
/// <remarks>An inbound allow rule must name a program, service, local port or ICMP protocol. Profiles default to
/// Domain and Private; include Public explicitly.</remarks>
public sealed record FirewallRuleSpecification(string Name, FirewallDirection Direction, FirewallAction Action)
{
    /// <summary>Description.</summary>
    public string Description { get; init; } = "";
    /// <summary>Program path; empty applies to all programs.</summary>
    public string ApplicationPath { get; init; } = "";
    /// <summary>Service name; empty applies to all services.</summary>
    public string ServiceName { get; init; } = "";
    /// <summary>IP protocol; <see cref="FirewallProtocol.Any"/> by default. Other IANA numbers (0-255) are cast to <see cref="FirewallProtocol"/>.</summary>
    public FirewallProtocol Protocol { get; init; } = FirewallProtocol.Any;
    /// <summary>Local port expression; requires <see cref="FirewallProtocol.Tcp"/> or <see cref="FirewallProtocol.Udp"/>.</summary>
    public string LocalPorts { get; init; } = "";
    /// <summary>Remote port expression; requires <see cref="FirewallProtocol.Tcp"/> or <see cref="FirewallProtocol.Udp"/>.</summary>
    public string RemotePorts { get; init; } = "";
    /// <summary>Local address expression.</summary>
    public string LocalAddresses { get; init; } = "*";
    /// <summary>Remote address expression.</summary>
    public string RemoteAddresses { get; init; } = "*";
    /// <summary>ICMP type/code expression; requires <see cref="FirewallProtocol.IcmpV4"/> or <see cref="FirewallProtocol.IcmpV6"/>.</summary>
    public string IcmpTypesAndCodes { get; init; } = "";
    /// <summary>Native interface names; empty applies to all interfaces.</summary>
    public ImmutableArray<string> Interfaces { get; init; } = [];
    /// <summary>Native interface type expression, such as All, LAN, Wireless or RemoteAccess.</summary>
    public string InterfaceTypes { get; init; } = "All";
    /// <summary>Enabled state.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Rule grouping, independently of its name.</summary>
    public string Grouping { get; init; } = "";
    /// <summary>Applicable profiles; Public is not included by default.</summary>
    public FirewallProfiles Profiles { get; init; } = FirewallProfiles.Domain | FirewallProfiles.Private;
    /// <summary>Permit edge traversal.</summary>
    public bool EdgeTraversal { get; init; }
}

/// <summary>Selected edits; null leaves a property unchanged. Rule names are not renamed by updates.</summary>
public sealed record FirewallRuleUpdate
{
    /// <summary>Replacement description.</summary>
    public string? Description { get; init; }
    /// <summary>Replacement program path.</summary>
    public string? ApplicationPath { get; init; }
    /// <summary>Replacement service name.</summary>
    public string? ServiceName { get; init; }
    /// <summary>Replacement protocol.</summary>
    public FirewallProtocol? Protocol { get; init; }
    /// <summary>Replacement local ports.</summary>
    public string? LocalPorts { get; init; }
    /// <summary>Replacement remote ports.</summary>
    public string? RemotePorts { get; init; }
    /// <summary>Replacement local addresses.</summary>
    public string? LocalAddresses { get; init; }
    /// <summary>Replacement remote addresses.</summary>
    public string? RemoteAddresses { get; init; }
    /// <summary>Replacement ICMP types/codes.</summary>
    public string? IcmpTypesAndCodes { get; init; }
    /// <summary>Replacement interface names.</summary>
    public ImmutableArray<string>? Interfaces { get; init; }
    /// <summary>Replacement interface types.</summary>
    public string? InterfaceTypes { get; init; }
    /// <summary>Replacement enabled state.</summary>
    public bool? Enabled { get; init; }
    /// <summary>Replacement grouping.</summary>
    public string? Grouping { get; init; }
    /// <summary>Replacement profiles.</summary>
    public FirewallProfiles? Profiles { get; init; }
    /// <summary>Replacement edge traversal.</summary>
    public bool? EdgeTraversal { get; init; }
    /// <summary>Replacement direction.</summary>
    public FirewallDirection? Direction { get; init; }
    /// <summary>Replacement action.</summary>
    public FirewallAction? Action { get; init; }
}

/// <summary>Curated rule fields. Updates retain additional native fields not represented here.</summary>
public sealed record FirewallRuleSnapshot(FirewallRuleIdentity Identity, FirewallRuleSpecification Configuration);

/// <summary>Read-only profile policy, including native local-policy modification state.</summary>
/// <remarks>A locally stored rule is not proof that Group Policy permits it to affect traffic.</remarks>
public sealed record FirewallProfileSnapshot(FirewallProfiles Profile, bool Active, bool Enabled,
    bool BlockAllInboundTraffic, FirewallAction DefaultInboundAction, FirewallAction DefaultOutboundAction,
    int NativeLocalPolicyModifyState);
