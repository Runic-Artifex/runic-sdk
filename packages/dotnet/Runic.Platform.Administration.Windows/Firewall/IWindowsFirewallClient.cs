using System.Collections.Immutable;
namespace Runic.Platform.Administration.Windows.Firewall;

/// <summary>Local firewall administration, replaceable by application-owned fakes.</summary>
public interface IWindowsFirewallClient
{
    /// <summary>Enumerates rules.</summary>
    Task<ImmutableArray<FirewallRuleSnapshot>> EnumerateAsync(CancellationToken cancellationToken = default);
    /// <summary>Finds a uniquely named native rule.</summary>
    Task<FirewallRuleSnapshot?> FindAsync(string name, CancellationToken cancellationToken = default);
    /// <summary>Reads effective profiles.</summary>
    Task<ImmutableArray<FirewallProfileSnapshot>> GetProfilesAsync(CancellationToken cancellationToken = default);
    /// <summary>Creates a rule without replacing an existing native name.</summary>
    Task CreateAsync(FirewallRuleSpecification specification, CancellationToken cancellationToken = default);
    /// <summary>Updates selected fields of an existing rule.</summary>
    Task UpdateAsync(FirewallRuleIdentity identity, FirewallRuleUpdate update, CancellationToken cancellationToken = default);
    /// <summary>Deletes a uniquely identified rule.</summary>
    Task<bool> DeleteAsync(FirewallRuleIdentity identity, CancellationToken cancellationToken = default);
}
