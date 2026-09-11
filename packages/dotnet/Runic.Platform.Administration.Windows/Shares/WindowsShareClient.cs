using System.Collections.Immutable;
using Runic.Platform.Administration.Windows.Internal.Backends;
namespace Runic.Platform.Administration.Windows.Shares;

/// <summary>Local or remote SMB share administration using generated NetAPI bindings and the current Windows identity.</summary>
public sealed class WindowsShareClient
{
    private readonly IShareClient _client;
    /// <summary>Creates a share client; null targets the local computer.</summary>
    public WindowsShareClient(string? server = null) => _client = AdministrationBackends.Shares("cswin32", server);
    /// <summary>Enumerates share summaries. Native enumeration failures remain errors.</summary>
    public ImmutableArray<ShareSummary> Enumerate() => _client.Enumerate();
    /// <summary>Reads share metadata and its nullable stored security descriptor. Null means the share is absent.</summary>
    public ShareSnapshot? Find(string name) => _client.Find(name);
    /// <summary>Creates a share. An existing name is a conflict; omitted security uses Windows defaults.</summary>
    public void Create(ShareSpecification specification) => _client.Create(specification);
    /// <summary>Updates supplied fields, retaining omitted security. Multiple native changes are not transactional.</summary>
    public void Update(string name, ShareUpdate update) => _client.Update(name, update);
    /// <summary>Deletes a share. Returns false only if absent.</summary>
    public bool Delete(string name) => _client.Delete(name);
}
