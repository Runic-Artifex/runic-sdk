using Runic.Platform.Administration.Windows.Internal;
namespace Runic.Platform.Administration.Windows.Firewall;
internal static class FirewallContract
{
    internal static void Validate(FirewallRuleSpecification value)
    {
        NativeError.Text(value.Name, nameof(value.Name));
        foreach (var text in new[] { value.Description, value.ApplicationPath, value.ServiceName, value.LocalPorts,
            value.RemotePorts, value.LocalAddresses, value.RemoteAddresses, value.IcmpTypesAndCodes, value.InterfaceTypes, value.Grouping })
            NativeError.Text(text, nameof(value), true);
        if (!Enum.IsDefined(value.Action) || !Enum.IsDefined(value.Direction) || (int)value.Protocol is < 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(value));
        var profiles = (int)value.Profiles;
        if (profiles != int.MaxValue && (profiles <= 0 || (profiles & ~7) != 0)) throw new ArgumentOutOfRangeException(nameof(value));
        if (value.Protocol is not (FirewallProtocol.Tcp or FirewallProtocol.Udp) && (value.LocalPorts.Length != 0 || value.RemotePorts.Length != 0))
            throw new ArgumentException("Ports require TCP or UDP.", nameof(value));
        if (value.Protocol is not (FirewallProtocol.IcmpV4 or FirewallProtocol.IcmpV6) && value.IcmpTypesAndCodes.Length != 0)
            throw new ArgumentException("ICMP type/code settings require ICMP.", nameof(value));
        if (value.Interfaces.IsDefault) throw new ArgumentException("Interfaces must be initialized.", nameof(value));
        foreach (var name in value.Interfaces) NativeError.Text(name, nameof(value));
    }

    // Without a program, service, local port or ICMP protocol, a new inbound allow rule admits all traffic to the host.
    internal static void RequireInboundAllowScope(FirewallRuleSpecification value)
    {
        if (IsUnscopedInboundAllow(value))
            throw new ArgumentException("A new inbound allow rule requires a program, service, local port or ICMP protocol.", nameof(value));
    }

    // An update must not turn a scoped or non-allow rule into an unscoped inbound allow rule, which create refuses.
    // A rule that already is one (for example a system rule) can still be updated, so it can be disabled or narrowed.
    internal static void RequireInboundAllowScope(FirewallRuleSpecification existing, FirewallRuleSpecification next)
    {
        if (IsUnscopedInboundAllow(next) && !IsUnscopedInboundAllow(existing))
            throw new ArgumentException("An update cannot make an inbound allow rule without a program, service, local port or ICMP protocol.", nameof(next));
    }

    private static bool IsUnscopedInboundAllow(FirewallRuleSpecification value) =>
        value.Direction == FirewallDirection.Inbound && value.Action == FirewallAction.Allow && value.ApplicationPath.Length == 0 &&
        value.ServiceName is "" or "*" && value.LocalPorts is "" or "*" && value.Protocol is not (FirewallProtocol.IcmpV4 or FirewallProtocol.IcmpV6);

    internal static void VerifyIdentity(FirewallRuleIdentity expected, FirewallRuleIdentity actual)
    {
        if (expected != actual) throw NativeError.Win32("Match firewall rule identity", 183);
    }

}
