namespace Runic.Platform.Administration.Windows.Shortcuts;

/// <summary>Shell-link operations, replaceable by application-owned fakes.</summary>
public interface IWindowsShellLinkClient
{
    /// <summary>Reads an existing shell link without resolution; null means absent.</summary>
    Task<ShellLinkSnapshot?> FindAsync(string path, CancellationToken cancellationToken = default);
    /// <summary>Creates a link; replacing an existing link requires explicit opt-in.</summary>
    Task CreateAsync(string path, ShellLinkSpecification specification, bool replaceExisting = false, CancellationToken cancellationToken = default);
    /// <summary>Updates selected fields while preserving unmodified metadata.</summary>
    Task UpdateAsync(string path, ShellLinkUpdate update, CancellationToken cancellationToken = default);
    /// <summary>Explicitly resolves a target without UI or writing the source link.</summary>
    Task<ShellLinkSnapshot> ResolveAsync(string path, TimeSpan timeout, CancellationToken cancellationToken = default);
}
