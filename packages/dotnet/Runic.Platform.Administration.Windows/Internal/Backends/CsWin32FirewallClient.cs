using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.WindowsFirewall;
using Windows.Win32.System.Com;
using Windows.Win32.System.Ole;
using Windows.Win32.System.Variant;
using Runic.Platform.Administration.Windows.Firewall;
using Runic.Platform.Administration.Windows.Internal;
using static Runic.Platform.Administration.Windows.Firewall.FirewallContract;

namespace Runic.Platform.Administration.Windows.Internal.Backends;

/// <summary>Local Windows Firewall administration. Requires Windows x64; mutations use the caller's privileges.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows6.1")]
internal sealed unsafe class CsWin32FirewallClient : IWindowsFirewallClient
{
    /// <summary>Creates a local firewall client.</summary>
    internal CsWin32FirewallClient() => Automation.RequireX64();

    /// <summary>Enumerates rules without treating duplicate native names as distinct persistent IDs.</summary>
    public Task<ImmutableArray<FirewallRuleSnapshot>> EnumerateAsync(CancellationToken cancellationToken = default) =>
        Execute((_, rules) => Enumerate(rules), cancellationToken);

    /// <summary>Finds a unique rule by native name. Ambiguous names fail rather than selecting arbitrarily.</summary>
    public Task<FirewallRuleSnapshot?> FindAsync(string name, CancellationToken cancellationToken = default)
    {
        NativeError.Text(name, nameof(name));
        return Execute((_, rules) => Unique(rules, name), cancellationToken);
    }

    /// <summary>Inspects effective profiles. Does not alter global firewall settings.</summary>
    public Task<ImmutableArray<FirewallProfileSnapshot>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
        Execute((policy, _) => Profiles(policy), cancellationToken);

    /// <summary>Creates a rule; an existing native name is a conflict.</summary>
    public Task CreateAsync(FirewallRuleSpecification specification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        Validate(specification);
        return Execute((policy, rules) => { Create(policy, rules, specification); return true; }, cancellationToken);
    }

    /// <summary>Updates selected properties on the existing native rule, preserving other native settings.</summary>
    /// <remarks>Changes are not transactional. If a later property setter fails, inspect the rule before retrying.</remarks>
    public Task UpdateAsync(FirewallRuleIdentity identity, FirewallRuleUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(update);
        return Execute((policy, rules) => { Update(policy, rules, identity, update); return true; }, cancellationToken);
    }

    /// <summary>Deletes a uniquely identified rule. Returns false only if absent.</summary>
    public Task<bool> DeleteAsync(FirewallRuleIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Execute((policy, rules) => Delete(policy, rules, identity), cancellationToken);
    }

    private static Task<T> Execute<T>(Func<GeneratedCom, GeneratedCom, T> action, CancellationToken cancellationToken) =>
        ComApartment.RunAsync(() =>
        {
            using var policy = GeneratedCom.Create(new("e2b3c97f-6ae1-41ac-817a-f6f92166d7dd"), new("98325047-c671-4174-8d81-defcd3f03186"));
            INetFwRules* pointer = null;
            var status = ((INetFwPolicy2*)policy.Pointer)->get_Rules(&pointer).Value;
            using var rules = GeneratedCom.FromResult(status, (nint)pointer, "Read firewall rules");
            return action(policy, rules);
        }, cancellationToken);

    private static ImmutableArray<FirewallRuleSnapshot> Enumerate(GeneratedCom rules)
    {
        IUnknown* pointer = null;
        var enumerationStatus = ((INetFwRules*)rules.Pointer)->get__NewEnum(&pointer).Value;
        using var unknown = GeneratedCom.FromResult(enumerationStatus, (nint)pointer, "Enumerate firewall rules");
        using var iterator = unknown.Query(new("00020404-0000-0000-c000-000000000046"));
        var result = ImmutableArray.CreateBuilder<FirewallRuleSnapshot>();
        while (true)
        {
            VARIANT item = default; uint count = 0;
            try
            {
                NativeError.Check(((IEnumVARIANT*)iterator.Pointer)->Next(1, &item, &count).Value, "Read firewall rule");
                if (count == 0) break;
                if (item.vt is not (VARENUM.VT_DISPATCH or VARENUM.VT_UNKNOWN) || item.punkVal == null)
                    throw NativeError.Win32("Read firewall rule object", 13);
                // QueryInterface acquires its own reference; VariantClear releases the enumeration reference.
                void* queried = null; var iid = new Guid("af230d27-baba-4e42-aced-f524f22cfce2");
                var queryStatus = item.punkVal->QueryInterface(&iid, &queried).Value;
                using var rule = GeneratedCom.FromResult(queryStatus, (nint)queried, "Query firewall rule");
                result.Add(Snapshot(rule));
            }
            finally { _ = PInvoke.VariantClear(&item); }
        }
        return result.ToImmutable();
    }

    private static FirewallRuleSnapshot? Unique(GeneratedCom rules, string name)
    {
        var matches = Enumerate(rules).Where(rule => string.Equals(rule.Identity.Name, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length > 1) throw NativeError.Win32("Select unambiguous firewall rule", 183);
        return matches.SingleOrDefault();
    }

    private enum Field { Name, Description, ApplicationName, ServiceName, Protocol, LocalPorts, RemotePorts, LocalAddresses, RemoteAddresses, IcmpTypesAndCodes, Direction, InterfaceTypes, Enabled, Grouping, Profiles, EdgeTraversal, Action }
    private static GeneratedCom Item(GeneratedCom rules, string name)
    {
        using var text = new BString(name); INetFwRule* pointer = null;
        var status = ((INetFwRules*)rules.Pointer)->Item(new BSTR((char*)text.Pointer), &pointer).Value;
        return GeneratedCom.FromResult(status, (nint)pointer, "Open firewall rule");
    }
    private static string Text(GeneratedCom rule, Field field)
    {
        BSTR result = default; var native = (INetFwRule*)rule.Pointer;
        try
        {
            var status = field switch
            {
            Field.Name => native->get_Name(&result),
            Field.Description => native->get_Description(&result),
            Field.ApplicationName => native->get_ApplicationName(&result),
            Field.ServiceName => native->get_ServiceName(&result),
            Field.LocalPorts => native->get_LocalPorts(&result),
            Field.RemotePorts => native->get_RemotePorts(&result),
            Field.LocalAddresses => native->get_LocalAddresses(&result),
            Field.RemoteAddresses => native->get_RemoteAddresses(&result),
            Field.IcmpTypesAndCodes => native->get_IcmpTypesAndCodes(&result),
            Field.InterfaceTypes => native->get_InterfaceTypes(&result),
            Field.Grouping => native->get_Grouping(&result),
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
            NativeError.Check(status.Value, $"Read firewall {field}");
            return result.Value == null ? "" : Marshal.PtrToStringBSTR((nint)result.Value);
        }
        finally { PInvoke.SysFreeString(result); }
    }
    private static int Integer(GeneratedCom rule, Field field)
    {
        int result = 0; var native = (INetFwRule*)rule.Pointer;
        var status = field switch
        {
            Field.Protocol => native->get_Protocol(&result),
            Field.Profiles => native->get_Profiles(&result),
            Field.Direction => native->get_Direction((NET_FW_RULE_DIRECTION*)&result),
            Field.Action => native->get_Action((NET_FW_ACTION*)&result),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        NativeError.Check(status.Value, $"Read firewall {field}"); return result;
    }
    private static bool Boolean(GeneratedCom rule, Field field)
    {
        VARIANT_BOOL value = default; var native = (INetFwRule*)rule.Pointer;
        var status = field == Field.Enabled ? native->get_Enabled(&value) : native->get_EdgeTraversal(&value);
        NativeError.Check(status.Value, $"Read firewall {field}"); return value.Value != 0;
    }

    private static unsafe FirewallRuleSnapshot Snapshot(GeneratedCom rule)
    {
        VARIANT interfaces = default;
        ImmutableArray<string> names;
        try
        {
            NativeError.Check(((INetFwRule*)rule.Pointer)->get_Interfaces(&interfaces).Value, "Read firewall interfaces");
            names = GeneratedArrays.ReadStrings(interfaces);
        }
        finally { _ = PInvoke.VariantClear(&interfaces); }
        var protocol = Integer(rule, Field.Protocol);
        var configuration = new FirewallRuleSpecification(Text(rule, Field.Name), (FirewallDirection)Integer(rule, Field.Direction), (FirewallAction)Integer(rule, Field.Action))
        {
            Description = Text(rule, Field.Description), ApplicationPath = Text(rule, Field.ApplicationName), ServiceName = Text(rule, Field.ServiceName),
            Protocol = protocol, LocalPorts = protocol is 6 or 17 ? Text(rule, Field.LocalPorts) : "",
            RemotePorts = protocol is 6 or 17 ? Text(rule, Field.RemotePorts) : "",
            LocalAddresses = Text(rule, Field.LocalAddresses), RemoteAddresses = Text(rule, Field.RemoteAddresses),
            IcmpTypesAndCodes = protocol is 1 or 58 ? Text(rule, Field.IcmpTypesAndCodes) : "",
            Interfaces = names, InterfaceTypes = Text(rule, Field.InterfaceTypes), Enabled = Boolean(rule, Field.Enabled),
            Grouping = Text(rule, Field.Grouping), Profiles = (FirewallProfiles)Integer(rule, Field.Profiles), EdgeTraversal = Boolean(rule, Field.EdgeTraversal)
        };
        return new(new(configuration.Name, configuration.Grouping, configuration.ApplicationPath, configuration.ServiceName, configuration.Direction), configuration);
    }

    private static void SetText(GeneratedCom rule, Field field, string value)
    {
        using var owned = new BString(value); var text = new BSTR((char*)owned.Pointer); var native = (INetFwRule*)rule.Pointer;
        var status = field switch
        {
            Field.Name => native->put_Name(text),
            Field.Description => native->put_Description(text),
            Field.ApplicationName => native->put_ApplicationName(text),
            Field.ServiceName => native->put_ServiceName(text),
            Field.LocalPorts => native->put_LocalPorts(text),
            Field.RemotePorts => native->put_RemotePorts(text),
            Field.LocalAddresses => native->put_LocalAddresses(text),
            Field.RemoteAddresses => native->put_RemoteAddresses(text),
            Field.IcmpTypesAndCodes => native->put_IcmpTypesAndCodes(text),
            Field.InterfaceTypes => native->put_InterfaceTypes(text),
            Field.Grouping => native->put_Grouping(text),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        NativeError.Check(status.Value, $"Set firewall {field}");
    }
    private static void SetInt(GeneratedCom rule, Field field, int value)
    {
        var native = (INetFwRule*)rule.Pointer;
        var status = field switch
        {
            Field.Protocol => native->put_Protocol(value),
            Field.Profiles => native->put_Profiles(value),
            Field.Direction => native->put_Direction((NET_FW_RULE_DIRECTION)value),
            Field.Action => native->put_Action((NET_FW_ACTION)value),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        NativeError.Check(status.Value, $"Set firewall {field}");
    }
    private static void SetBoolean(GeneratedCom rule, Field field, bool value)
    {
        var native = (INetFwRule*)rule.Pointer; var boolean = new VARIANT_BOOL(value ? (short)-1 : (short)0);
        var status = field == Field.Enabled ? native->put_Enabled(boolean) : native->put_EdgeTraversal(boolean);
        NativeError.Check(status.Value, $"Set firewall {field}");
    }
    private static void SetInterfaces(GeneratedCom rule, ImmutableArray<string> interfaces)
    {
        var value = GeneratedArrays.CreateStrings(interfaces);
        try { NativeError.Check(((INetFwRule*)rule.Pointer)->put_Interfaces(value).Value, "Set firewall interfaces"); }
        finally { _ = PInvoke.VariantClear(&value); }
    }
    private static int ModificationState(GeneratedCom policy)
    {
        NET_FW_MODIFY_STATE state = default;
        NativeError.Check(((INetFwPolicy2*)policy.Pointer)->get_LocalPolicyModifyState(&state).Value, "Read firewall policy restrictions");
        return (int)state;
    }

    private static void Writable(GeneratedCom policy)
    {
        if (ModificationState(policy) != 0)
            throw new WindowsAdministrationException("Modify firewall policy", AdministrationErrorCategory.AccessDenied,
                NativeErrorDomain.Win32, 5, "Current firewall policy restricts local policy modifications.");
    }

    private static unsafe void Create(GeneratedCom policy, GeneratedCom rules, FirewallRuleSpecification specification)
    {
        Writable(policy);
        if (Unique(rules, specification.Name) is not null) throw NativeError.Win32("Create firewall rule", 183);
        using var rule = GeneratedCom.Create(new("2c5bc43e-3369-4c33-ab0c-be9469677af4"), new("af230d27-baba-4e42-aced-f524f22cfce2"));
        SetText(rule, Field.Name, specification.Name);
        Apply(rule, new()
        {
            Description = Optional(specification.Description), ApplicationPath = Optional(specification.ApplicationPath),
            ServiceName = Optional(specification.ServiceName), Protocol = specification.Protocol,
            LocalPorts = specification.Protocol is 6 or 17 ? Optional(specification.LocalPorts) : null,
            RemotePorts = specification.Protocol is 6 or 17 ? Optional(specification.RemotePorts) : null,
            LocalAddresses = specification.LocalAddresses, RemoteAddresses = specification.RemoteAddresses,
            IcmpTypesAndCodes = specification.Protocol is 1 or 58 ? Optional(specification.IcmpTypesAndCodes) : null,
            Interfaces = specification.Interfaces.IsEmpty ? null : specification.Interfaces,
            InterfaceTypes = specification.InterfaceTypes, Enabled = specification.Enabled, Grouping = Optional(specification.Grouping),
            Profiles = specification.Profiles, EdgeTraversal = specification.EdgeTraversal,
            Direction = specification.Direction, Action = specification.Action
        });
        var status = ((INetFwRules*)rules.Pointer)->Add((INetFwRule*)rule.Pointer).Value;
        if (status < 0)
        {
            var error = NativeError.HResult("Add firewall rule", status);
            // Include non-secret native restrictions: Add may reject combinations after setters succeed.
            string details;
            try
            {
                var value = Snapshot(rule).Configuration;
                details = $" Protocol={value.Protocol}; direction={value.Direction}; action={value.Action}; profiles={value.Profiles}; " +
                    $"local ports='{value.LocalPorts}'; remote ports='{value.RemotePorts}'; interface types='{value.InterfaceTypes}'.";
            }
            catch (WindowsAdministrationException) { details = " Native rule inspection also failed."; }
            throw new WindowsAdministrationException(error.Operation, error.Category, error.NativeErrorDomain,
                error.NativeErrorCode, error.Message + details, error);
        }
    }

    private static void Update(GeneratedCom policy, GeneratedCom rules, FirewallRuleIdentity identity, FirewallRuleUpdate update)
    {
        Writable(policy);
        var existing = Unique(rules, identity.Name) ?? throw NativeError.Win32("Update firewall rule", 2);
        VerifyIdentity(identity, existing.Identity);
        var next = existing.Configuration with
        {
            Description = update.Description ?? existing.Configuration.Description,
            ApplicationPath = update.ApplicationPath ?? existing.Configuration.ApplicationPath,
            ServiceName = update.ServiceName ?? existing.Configuration.ServiceName,
            Protocol = update.Protocol ?? existing.Configuration.Protocol,
            LocalPorts = update.LocalPorts ?? existing.Configuration.LocalPorts,
            RemotePorts = update.RemotePorts ?? existing.Configuration.RemotePorts,
            LocalAddresses = update.LocalAddresses ?? existing.Configuration.LocalAddresses,
            RemoteAddresses = update.RemoteAddresses ?? existing.Configuration.RemoteAddresses,
            IcmpTypesAndCodes = update.IcmpTypesAndCodes ?? existing.Configuration.IcmpTypesAndCodes,
            Interfaces = update.Interfaces ?? existing.Configuration.Interfaces,
            InterfaceTypes = update.InterfaceTypes ?? existing.Configuration.InterfaceTypes,
            Enabled = update.Enabled ?? existing.Configuration.Enabled, Grouping = update.Grouping ?? existing.Configuration.Grouping,
            Profiles = update.Profiles ?? existing.Configuration.Profiles, EdgeTraversal = update.EdgeTraversal ?? existing.Configuration.EdgeTraversal,
            Direction = update.Direction ?? existing.Configuration.Direction, Action = update.Action ?? existing.Configuration.Action
        };
        Validate(next);
        using var rule = Item(rules, identity.Name);
        VerifyIdentity(identity, Snapshot(rule).Identity);
        // Windows validates each setter immediately. Clear protocol-specific fields
        // before switching protocol, then apply only fields valid for the new protocol.
        if (existing.Configuration.Protocol != next.Protocol)
        {
            if (existing.Configuration.Protocol is 6 or 17 && next.Protocol is not (6 or 17))
            {
                SetText(rule, Field.LocalPorts, "");
                SetText(rule, Field.RemotePorts, "");
                update = update with { LocalPorts = null, RemotePorts = null };
            }
            if (existing.Configuration.Protocol is 1 or 58 && next.Protocol is not (1 or 58))
            {
                SetText(rule, Field.IcmpTypesAndCodes, "");
                update = update with { IcmpTypesAndCodes = null };
            }
        }
        Apply(rule, update);
    }

    // Do not replace native defaults with empty optional restrictions when creating a rule.
    private static string? Optional(string value) => value.Length == 0 ? null : value;

    private static void Apply(GeneratedCom rule, FirewallRuleUpdate update)
    {
        if (update.Protocol is { } protocol) SetInt(rule, Field.Protocol, protocol);
        if (update.Description is { } description) SetText(rule, Field.Description, description);
        if (update.ApplicationPath is { } application) SetText(rule, Field.ApplicationName, application);
        if (update.ServiceName is { } service) SetText(rule, Field.ServiceName, service);
        if (update.LocalPorts is { } localPorts) SetText(rule, Field.LocalPorts, localPorts);
        if (update.RemotePorts is { } remotePorts) SetText(rule, Field.RemotePorts, remotePorts);
        if (update.LocalAddresses is { } localAddresses) SetText(rule, Field.LocalAddresses, localAddresses);
        if (update.RemoteAddresses is { } remoteAddresses) SetText(rule, Field.RemoteAddresses, remoteAddresses);
        if (update.IcmpTypesAndCodes is { } icmp) SetText(rule, Field.IcmpTypesAndCodes, icmp);
        if (update.Direction is { } direction) SetInt(rule, Field.Direction, (int)direction);
        if (update.Interfaces is { } interfaces) SetInterfaces(rule, interfaces);
        if (update.InterfaceTypes is { } interfaceTypes) SetText(rule, Field.InterfaceTypes, interfaceTypes);
        if (update.Enabled is { } enabled) SetBoolean(rule, Field.Enabled, enabled);
        if (update.Grouping is { } grouping) SetText(rule, Field.Grouping, grouping);
        if (update.Profiles is { } profiles) SetInt(rule, Field.Profiles, (int)profiles);
        if (update.EdgeTraversal is { } edge) SetBoolean(rule, Field.EdgeTraversal, edge);
        if (update.Action is { } action) SetInt(rule, Field.Action, (int)action);
    }

    private static unsafe bool Delete(GeneratedCom policy, GeneratedCom rules, FirewallRuleIdentity identity)
    {
        Writable(policy);
        var existing = Unique(rules, identity.Name);
        if (existing is null) return false;
        VerifyIdentity(identity, existing.Identity);
        using var name = new BString(identity.Name);
        var status = ((INetFwRules*)rules.Pointer)->Remove(new BSTR((char*)name.Pointer)).Value;
        if (status is unchecked((int)0x80070002) or unchecked((int)0x80070490)) return false;
        NativeError.Check(status, "Delete firewall rule");
        return true;
    }

    private static ImmutableArray<FirewallProfileSnapshot> Profiles(GeneratedCom policy)
    {
        var native = (INetFwPolicy2*)policy.Pointer; int current = 0;
        NativeError.Check(native->get_CurrentProfileTypes(&current).Value, "Read current firewall profiles");
        var modify = ModificationState(policy);
        var values = ImmutableArray.CreateBuilder<FirewallProfileSnapshot>();
        foreach (var profile in new[] { FirewallProfiles.Domain, FirewallProfiles.Private, FirewallProfiles.Public })
        {
            VARIANT_BOOL enabled = default, block = default;
            NET_FW_ACTION inbound = default, outbound = default; var kind = (NET_FW_PROFILE_TYPE2)profile;
            NativeError.Check(native->get_FirewallEnabled(kind, &enabled).Value, "Read firewall enabled state");
            NativeError.Check(native->get_BlockAllInboundTraffic(kind, &block).Value, "Read inbound blocking state");
            NativeError.Check(native->get_DefaultInboundAction(kind, &inbound).Value, "Read default inbound action");
            NativeError.Check(native->get_DefaultOutboundAction(kind, &outbound).Value, "Read default outbound action");
            values.Add(new(profile, (current & (int)profile) != 0, enabled.Value != 0, block.Value != 0, (FirewallAction)inbound, (FirewallAction)outbound, modify));
        }
        return values.ToImmutable();
    }
}
