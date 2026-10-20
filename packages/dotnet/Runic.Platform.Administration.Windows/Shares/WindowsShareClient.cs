using System.Collections.Immutable;
using Runic.Platform.Administration.Windows.Internal;
using Runic.Platform.Administration.Windows.Internal.Backends;
namespace Runic.Platform.Administration.Windows.Shares;

/// <summary>Local or remote SMB share administration using generated NetAPI bindings and the current Windows identity.</summary>
/// <remarks>Native NetAPI calls run off the caller's thread. Cancellation before a call starts prevents it; once a
/// call starts, its actual result is returned.</remarks>
public sealed class WindowsShareClient
{
    private readonly IShareClient _client;
    /// <summary>Creates a share client; null targets the local computer.</summary>
    public WindowsShareClient(string? machineName = null) => _client = GeneratedAdministrationClients.Shares(machineName);
    /// <summary>Enumerates share summaries. Native enumeration failures remain errors.</summary>
    public Task<ImmutableArray<ShareSummary>> EnumerateAsync(CancellationToken cancellationToken = default) =>
        NativeWork.RunAsync(_client.Enumerate, cancellationToken);
    /// <summary>Reads share metadata and its nullable stored security descriptor. Null means the share is absent.</summary>
    public Task<ShareSnapshot?> FindAsync(string name, CancellationToken cancellationToken = default) =>
        NativeWork.RunAsync(() => _client.Find(name), cancellationToken);
    /// <summary>Creates a share. An existing name is a conflict; omitted security uses Windows defaults.</summary>
    public Task CreateAsync(ShareSpecification specification, CancellationToken cancellationToken = default) =>
        NativeWork.RunAsync(() => _client.Create(specification), cancellationToken);
    /// <summary>Updates supplied fields, retaining omitted security. Multiple native changes are not transactional.</summary>
    public Task UpdateAsync(string name, ShareUpdate update, CancellationToken cancellationToken = default) =>
        NativeWork.RunAsync(() => _client.Update(name, update), cancellationToken);
    /// <summary>Deletes a share. Returns false only if absent.</summary>
    public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        NativeWork.RunAsync(() => _client.Delete(name), cancellationToken);
}
