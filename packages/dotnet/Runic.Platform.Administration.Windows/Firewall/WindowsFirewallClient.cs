using System.Collections.Immutable;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.Firewall;

/// <summary>Local Windows Firewall administration. Requires Windows x64; mutations use the caller's privileges.</summary>
public sealed class WindowsFirewallClient : IWindowsFirewallClient
{
    /// <summary>Creates a local firewall client.</summary>
    public WindowsFirewallClient() => Automation.RequireX64();

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

    private static Task<T> Execute<T>(Func<ComObject, ComObject, T> action, CancellationToken cancellationToken) =>
        ComApartment.RunAsync(() =>
        {
            using var policy = ComObject.Create(new("e2b3c97f-6ae1-41ac-817a-f6f92166d7dd"), new("98325047-c671-4174-8d81-defcd3f03186"));
            using var rules = Automation.GetObject(policy, 18, "Read firewall rules");
            return action(policy, rules);
        }, cancellationToken);

    private static unsafe ImmutableArray<FirewallRuleSnapshot> Enumerate(ComObject rules)
    {
        using var unknown = Automation.GetObject(rules, 11, "Enumerate firewall rules");
        using var iterator = unknown.Query(new("00020404-0000-0000-c000-000000000046"));
        var result = ImmutableArray.CreateBuilder<FirewallRuleSnapshot>();
        while (true)
        {
            Variant item = default;
            uint count = 0;
            try
            {
                var status = ((delegate* unmanaged[Stdcall]<nint, uint, Variant*, uint*, int>)iterator.Slot(3))(iterator.Pointer, 1, &item, &count);
                NativeError.Check(status, "Read firewall rule");
                if (count == 0) break;
                if (item.Type is not (9 or 13) || item.Pointer == 0) throw NativeError.Win32("Read firewall rule object", 13);
                var pointer = item.Pointer;
                ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)pointer)[1])(pointer);
                using var value = ComObject.Own(pointer);
                using var rule = value.Query(new("af230d27-baba-4e42-aced-f524f22cfce2"));
                result.Add(Snapshot(rule));
            }
            finally { _ = AutomationArrays.VariantClear(&item); }
        }
        return result.ToImmutable();
    }

    private static FirewallRuleSnapshot? Unique(ComObject rules, string name)
    {
        var matches = Enumerate(rules).Where(rule => string.Equals(rule.Identity.Name, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length > 1) throw NativeError.Win32("Select unambiguous firewall rule", 183);
        return matches.SingleOrDefault();
    }

    private static void VerifyIdentity(FirewallRuleIdentity expected, FirewallRuleIdentity actual)
    {
        if (expected != actual) throw NativeError.Win32("Match firewall rule identity", 183);
    }

    private static unsafe ComObject Item(ComObject rules, string name)
    {
        using var text = new BString(name);
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)rules.Slot(10))(rules.Pointer, text.Pointer, &result), "Open firewall rule");
        return ComObject.Own(result);
    }

    private static string Text(ComObject rule, int slot) => Automation.GetString(rule, slot, "Read firewall rule field");
    private static int Integer(ComObject rule, int slot) => Automation.GetInt32(rule, slot, "Read firewall rule field");
    private static bool Boolean(ComObject rule, int slot) => Automation.GetBoolean(rule, slot, "Read firewall rule field");

    private static unsafe FirewallRuleSnapshot Snapshot(ComObject rule)
    {
        Variant interfaces = default;
        ImmutableArray<string> names;
        try
        {
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, Variant*, int>)rule.Slot(29))(rule.Pointer, &interfaces), "Read firewall interfaces");
            names = AutomationArrays.ReadStrings(interfaces);
        }
        finally { _ = AutomationArrays.VariantClear(&interfaces); }
        var protocol = Integer(rule, 15);
        var configuration = new FirewallRuleSpecification(Text(rule, 7), (FirewallDirection)Integer(rule, 27), (FirewallAction)Integer(rule, 41))
        {
            Description = Text(rule, 9), ApplicationPath = Text(rule, 11), ServiceName = Text(rule, 13),
            Protocol = protocol, LocalPorts = protocol is 6 or 17 ? Text(rule, 17) : "",
            RemotePorts = protocol is 6 or 17 ? Text(rule, 19) : "",
            LocalAddresses = Text(rule, 21), RemoteAddresses = Text(rule, 23),
            IcmpTypesAndCodes = protocol is 1 or 58 ? Text(rule, 25) : "",
            Interfaces = names, InterfaceTypes = Text(rule, 31), Enabled = Boolean(rule, 33),
            Grouping = Text(rule, 35), Profiles = (FirewallProfiles)Integer(rule, 37), EdgeTraversal = Boolean(rule, 39)
        };
        return new(new(configuration.Name, configuration.Grouping, configuration.ApplicationPath, configuration.ServiceName, configuration.Direction), configuration);
    }

    private static void Validate(FirewallRuleSpecification value)
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

    private static unsafe void SetText(ComObject rule, int slot, string value)
    {
        using var text = new BString(value);
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)rule.Slot(slot))(rule.Pointer, text.Pointer), "Set firewall rule field");
    }

    private static unsafe void SetInt(ComObject rule, int slot, int value) =>
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int, int>)rule.Slot(slot))(rule.Pointer, value), "Set firewall rule field");

    private static unsafe void SetInterfaces(ComObject rule, ImmutableArray<string> interfaces)
    {
        var value = AutomationArrays.CreateStrings(interfaces);
        try { NativeError.Check(((delegate* unmanaged[Stdcall]<nint, Variant, int>)rule.Slot(30))(rule.Pointer, value), "Set firewall interfaces"); }
        finally { _ = AutomationArrays.VariantClear(&value); }
    }

    private static void Writable(ComObject policy)
    {
        if (Automation.GetInt32(policy, 28, "Read firewall policy restrictions") != 0)
            throw new WindowsAdministrationException("Modify firewall policy", AdministrationErrorCategory.AccessDenied,
                NativeErrorDomain.Win32, 5, "Current firewall policy restricts local policy modifications.");
    }

    private static unsafe void Create(ComObject policy, ComObject rules, FirewallRuleSpecification specification)
    {
        Writable(policy);
        if (Unique(rules, specification.Name) is not null) throw NativeError.Win32("Create firewall rule", 183);
        using var rule = ComObject.Create(new("2c5bc43e-3369-4c33-ab0c-be9469677af4"), new("af230d27-baba-4e42-aced-f524f22cfce2"));
        SetText(rule, 8, specification.Name);
        Apply(rule, new()
        {
            Description = specification.Description, ApplicationPath = specification.ApplicationPath,
            ServiceName = specification.ServiceName, Protocol = specification.Protocol,
            LocalPorts = specification.Protocol is 6 or 17 ? specification.LocalPorts : null,
            RemotePorts = specification.Protocol is 6 or 17 ? specification.RemotePorts : null,
            LocalAddresses = specification.LocalAddresses, RemoteAddresses = specification.RemoteAddresses,
            IcmpTypesAndCodes = specification.Protocol is 1 or 58 ? specification.IcmpTypesAndCodes : null,
            Interfaces = specification.Interfaces.IsEmpty ? null : specification.Interfaces,
            InterfaceTypes = specification.InterfaceTypes, Enabled = specification.Enabled, Grouping = specification.Grouping,
            Profiles = specification.Profiles, EdgeTraversal = specification.EdgeTraversal,
            Direction = specification.Direction, Action = specification.Action
        });
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)rules.Slot(8))(rules.Pointer, rule.Pointer), "Add firewall rule");
    }

    private static void Update(ComObject policy, ComObject rules, FirewallRuleIdentity identity, FirewallRuleUpdate update)
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
                SetText(rule, 18, "");
                SetText(rule, 20, "");
                update = update with { LocalPorts = null, RemotePorts = null };
            }
            if (existing.Configuration.Protocol is 1 or 58 && next.Protocol is not (1 or 58))
            {
                SetText(rule, 26, "");
                update = update with { IcmpTypesAndCodes = null };
            }
        }
        Apply(rule, update);
    }

    private static void Apply(ComObject rule, FirewallRuleUpdate update)
    {
        if (update.Protocol is { } protocol) SetInt(rule, 16, protocol);
        if (update.Description is { } description) SetText(rule, 10, description);
        if (update.ApplicationPath is { } application) SetText(rule, 12, application);
        if (update.ServiceName is { } service) SetText(rule, 14, service);
        if (update.LocalPorts is { } localPorts) SetText(rule, 18, localPorts);
        if (update.RemotePorts is { } remotePorts) SetText(rule, 20, remotePorts);
        if (update.LocalAddresses is { } localAddresses) SetText(rule, 22, localAddresses);
        if (update.RemoteAddresses is { } remoteAddresses) SetText(rule, 24, remoteAddresses);
        if (update.IcmpTypesAndCodes is { } icmp) SetText(rule, 26, icmp);
        if (update.Direction is { } direction) SetInt(rule, 28, (int)direction);
        if (update.Interfaces is { } interfaces) SetInterfaces(rule, interfaces);
        if (update.InterfaceTypes is { } interfaceTypes) SetText(rule, 32, interfaceTypes);
        if (update.Enabled is { } enabled) Automation.SetBoolean(rule, 34, enabled, "Enable firewall rule");
        if (update.Grouping is { } grouping) SetText(rule, 36, grouping);
        if (update.Profiles is { } profiles) SetInt(rule, 38, (int)profiles);
        if (update.EdgeTraversal is { } edge) Automation.SetBoolean(rule, 40, edge, "Set firewall edge traversal");
        if (update.Action is { } action) SetInt(rule, 42, (int)action);
    }

    private static unsafe bool Delete(ComObject policy, ComObject rules, FirewallRuleIdentity identity)
    {
        Writable(policy);
        var existing = Unique(rules, identity.Name);
        if (existing is null) return false;
        VerifyIdentity(identity, existing.Identity);
        using var name = new BString(identity.Name);
        var status = ((delegate* unmanaged[Stdcall]<nint, nint, int>)rules.Slot(9))(rules.Pointer, name.Pointer);
        if (status is unchecked((int)0x80070002) or unchecked((int)0x80070490)) return false;
        NativeError.Check(status, "Delete firewall rule");
        return true;
    }

    private static unsafe ImmutableArray<FirewallProfileSnapshot> Profiles(ComObject policy)
    {
        var current = Automation.GetInt32(policy, 7, "Read current firewall profiles");
        var modify = Automation.GetInt32(policy, 28, "Read local firewall modification state");
        var values = ImmutableArray.CreateBuilder<FirewallProfileSnapshot>();
        foreach (var profile in new[] { FirewallProfiles.Domain, FirewallProfiles.Private, FirewallProfiles.Public })
        {
            short enabled, block;
            int inbound, outbound;
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int, short*, int>)policy.Slot(8))(policy.Pointer, (int)profile, &enabled), "Read firewall enabled state");
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int, short*, int>)policy.Slot(12))(policy.Pointer, (int)profile, &block), "Read inbound blocking state");
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int, int*, int>)policy.Slot(23))(policy.Pointer, (int)profile, &inbound), "Read default inbound action");
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int, int*, int>)policy.Slot(25))(policy.Pointer, (int)profile, &outbound), "Read default outbound action");
            values.Add(new(profile, (current & (int)profile) != 0, enabled != 0, block != 0, (FirewallAction)inbound, (FirewallAction)outbound, modify));
        }
        return values.ToImmutable();
    }
}
