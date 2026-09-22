using Runic.Platform.Administration.Windows.Firewall;
using Runic.Platform.Administration.Windows.Shares;
using System.Collections.Immutable;

namespace Runic.Platform.Administration.Windows.Internal.Backends;

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
    internal static IWindowsFirewallClient Firewall()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            throw new PlatformNotSupportedException("Windows 7 or later is required.");
        return new CsWin32FirewallClient();
    }

    internal static IShareClient Shares(string? server)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            throw new PlatformNotSupportedException("Windows 7 or later is required.");
        return new CsWin32ShareClient(server);
    }
}
