using Runic.Platform.Administration.Windows.Firewall;
using Runic.Platform.Administration.Windows.Shares;
using System.Collections.Immutable;

namespace Runic.Platform.Administration.Windows.Internal.Backends;

internal interface IFirewallClient
{
    Task<ImmutableArray<FirewallRuleSnapshot>> EnumerateAsync(CancellationToken cancellationToken);
    Task<FirewallRuleSnapshot?> FindAsync(string name, CancellationToken cancellationToken);
    Task<ImmutableArray<FirewallProfileSnapshot>> GetProfilesAsync(CancellationToken cancellationToken);
    Task CreateAsync(FirewallRuleSpecification specification, CancellationToken cancellationToken);
    Task UpdateAsync(FirewallRuleIdentity identity, FirewallRuleUpdate update, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(FirewallRuleIdentity identity, CancellationToken cancellationToken);
    Task<int> DeleteAllAsync(string name, CancellationToken cancellationToken);
    Task<FirewallLocalPolicyState> GetLocalPolicyStateAsync(CancellationToken cancellationToken);
}

internal interface IShareClient
{
    ImmutableArray<ShareSummary> Enumerate();
    ShareSnapshot? Find(string name);
    void Create(ShareSpecification specification);
    void Update(string name, ShareUpdate update);
    bool Delete(string name);
}

internal static class GeneratedAdministrationClients
{
    internal static IFirewallClient Firewall(FirewallClientOptions options)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            throw new PlatformNotSupportedException("Windows 7 or later is required.");
        return new CsWin32FirewallClient(options);
    }

    internal static IShareClient Shares(string? machineName)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            throw new PlatformNotSupportedException("Windows 7 or later is required.");
        return new CsWin32ShareClient(machineName);
    }
}
