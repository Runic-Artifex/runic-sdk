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
        if (!Enum.IsDefined(value.Action) || !Enum.IsDefined(value.Direction) || value.Protocol is < 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(value));
        var profiles = (int)value.Profiles;
        if (profiles != int.MaxValue && (profiles <= 0 || (profiles & ~7) != 0)) throw new ArgumentOutOfRangeException(nameof(value));
        if (value.Protocol is not (6 or 17) && (value.LocalPorts.Length != 0 || value.RemotePorts.Length != 0))
            throw new ArgumentException("Ports require TCP or UDP.", nameof(value));
        if (value.Protocol is not (1 or 58) && value.IcmpTypesAndCodes.Length != 0)
            throw new ArgumentException("ICMP type/code settings require ICMP.", nameof(value));
        if (value.Interfaces.IsDefault) throw new ArgumentException("Interfaces must be initialized.", nameof(value));
        foreach (var name in value.Interfaces) NativeError.Text(name, nameof(value));
    }

    internal static void VerifyIdentity(FirewallRuleIdentity expected, FirewallRuleIdentity actual)
    {
        if (expected != actual) throw NativeError.Win32("Match firewall rule identity", 183);
    }

}
