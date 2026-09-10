using System.Collections.Immutable;
using Runic.Platform.Administration.Windows.Firewall;
using Runic.Platform.Administration.Windows.Shares;
namespace Runic.Platform.Administration.Windows.Internal.Backends;
internal interface IShareClient
{
    ImmutableArray<ShareSummary> Enumerate();
    ShareSnapshot? Find(string name);
    void Create(ShareSpecification specification);
    void Update(string name, ShareUpdate update);
    bool Delete(string name);
}
internal static class AdministrationBackends
{
    internal static IWindowsFirewallClient Firewall(string backend)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        return backend switch
        {
            "handwritten" => new HandwrittenFirewallClient(), "cswin32" => new CsWin32FirewallClient(),
            _ => throw new ArgumentException("Unknown administration backend.", nameof(backend))
        };
    }
    internal static IShareClient Shares(string backend, string? server = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        return backend switch
        {
            "handwritten" => new HandwrittenShares(new HandwrittenShareClient(server)),
            "cswin32" => new CsWin32ShareClient(server),
            _ => throw new ArgumentException("Unknown administration backend.", nameof(backend))
        };
    }
    private sealed class HandwrittenShares(HandwrittenShareClient client) : IShareClient
    {
        public ImmutableArray<ShareSummary> Enumerate() => client.Enumerate();
        public ShareSnapshot? Find(string name) => client.Find(name);
        public void Create(ShareSpecification specification) => client.Create(specification);
        public void Update(string name, ShareUpdate update) => client.Update(name, update);
        public bool Delete(string name) => client.Delete(name);
    }
}
