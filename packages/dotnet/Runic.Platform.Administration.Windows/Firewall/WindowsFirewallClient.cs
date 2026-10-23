using System.Collections.Immutable;
using Runic.Platform.Administration.Windows.Internal.Backends;
namespace Runic.Platform.Administration.Windows.Firewall;

/// <summary>Local Windows Firewall administration using generated native bindings and the caller's privileges.</summary>
public sealed class WindowsFirewallClient
{
    private readonly IFirewallClient _client;
    /// <summary>Creates a local firewall client.</summary>
    public WindowsFirewallClient() => _client = GeneratedAdministrationClients.Firewall();
    /// <summary>Enumerates rules without treating duplicate native names as distinct persistent IDs.</summary>
    public Task<ImmutableArray<FirewallRuleSnapshot>> EnumerateAsync(CancellationToken cancellationToken = default) => _client.EnumerateAsync(cancellationToken);
    /// <summary>Finds a unique rule by native name. Ambiguous names fail.</summary>
    public Task<FirewallRuleSnapshot?> FindAsync(string name, CancellationToken cancellationToken = default) => _client.FindAsync(name, cancellationToken);
    /// <summary>Inspects effective profiles without changing global firewall settings.</summary>
    public Task<ImmutableArray<FirewallProfileSnapshot>> GetProfilesAsync(CancellationToken cancellationToken = default) => _client.GetProfilesAsync(cancellationToken);
    /// <summary>Creates a rule; an existing native name is a conflict.</summary>
    /// <remarks>An inbound allow rule needs a program, service, local port or ICMP protocol; otherwise this throws
    /// <see cref="ArgumentException"/>.</remarks>
    public Task CreateAsync(FirewallRuleSpecification specification, CancellationToken cancellationToken = default) => _client.CreateAsync(specification, cancellationToken);
    /// <summary>Updates selected properties, preserving other native settings. Changes are not transactional.</summary>
    /// <remarks>An update that would turn a rule into an inbound allow rule without a program, service, local port or ICMP
    /// protocol throws <see cref="ArgumentException"/> and changes nothing. A rule that already allows such traffic can
    /// still be updated.</remarks>
    public Task UpdateAsync(FirewallRuleIdentity identity, FirewallRuleUpdate update, CancellationToken cancellationToken = default) => _client.UpdateAsync(identity, update, cancellationToken);
    /// <summary>Deletes a uniquely identified rule. Returns false only if absent.</summary>
    /// <remarks>The rule that matched <paramref name="identity"/> is renamed to a unique temporary name and removed by
    /// that name, so a rule that takes the name concurrently is not removed.</remarks>
    public Task<bool> DeleteAsync(FirewallRuleIdentity identity, CancellationToken cancellationToken = default) => _client.DeleteAsync(identity, cancellationToken);
}
