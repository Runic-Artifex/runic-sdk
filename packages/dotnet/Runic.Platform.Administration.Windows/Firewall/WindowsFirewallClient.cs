using System.Collections.Immutable;
using Runic.Platform.Administration.Windows.Internal.Backends;
namespace Runic.Platform.Administration.Windows.Firewall;

/// <summary>Local Windows Firewall administration using generated native bindings and the caller's privileges.</summary>
public sealed class WindowsFirewallClient : IWindowsFirewallClient
{
    private readonly IWindowsFirewallClient _client;
    /// <summary>Creates a local firewall client.</summary>
    public WindowsFirewallClient() => _client = AdministrationBackends.Firewall("cswin32");
    /// <summary>Enumerates rules without treating duplicate native names as distinct persistent IDs.</summary>
    public Task<ImmutableArray<FirewallRuleSnapshot>> EnumerateAsync(CancellationToken cancellationToken = default) => _client.EnumerateAsync(cancellationToken);
    /// <summary>Finds a unique rule by native name. Ambiguous names fail.</summary>
    public Task<FirewallRuleSnapshot?> FindAsync(string name, CancellationToken cancellationToken = default) => _client.FindAsync(name, cancellationToken);
    /// <summary>Inspects effective profiles without changing global firewall settings.</summary>
    public Task<ImmutableArray<FirewallProfileSnapshot>> GetProfilesAsync(CancellationToken cancellationToken = default) => _client.GetProfilesAsync(cancellationToken);
    /// <summary>Creates a rule; an existing native name is a conflict.</summary>
    public Task CreateAsync(FirewallRuleSpecification specification, CancellationToken cancellationToken = default) => _client.CreateAsync(specification, cancellationToken);
    /// <summary>Updates selected properties, preserving other native settings. Changes are not transactional.</summary>
    public Task UpdateAsync(FirewallRuleIdentity identity, FirewallRuleUpdate update, CancellationToken cancellationToken = default) => _client.UpdateAsync(identity, update, cancellationToken);
    /// <summary>Deletes a uniquely identified rule. Returns false only if absent.</summary>
    public Task<bool> DeleteAsync(FirewallRuleIdentity identity, CancellationToken cancellationToken = default) => _client.DeleteAsync(identity, cancellationToken);
}
