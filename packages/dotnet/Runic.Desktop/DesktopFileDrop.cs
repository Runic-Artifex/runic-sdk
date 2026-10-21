namespace Runic.Desktop;

/// <summary>Provides the files the user dropped onto a window, for <see cref="DesktopWindow.FilesDropped"/>.</summary>
public sealed class DesktopFileDropEventArgs : EventArgs
{
    /// <summary>Creates event data for dropped files.</summary>
    /// <param name="paths">The absolute local paths of the dropped files and directories, as the platform reported them.</param>
    /// <param name="target">The page's routing label, or <see langword="null"/>; see <see cref="Target"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="paths"/> is empty, or contains an empty or relative path.</exception>
    public DesktopFileDropEventArgs(IReadOnlyList<string> paths, string? target = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0) throw new ArgumentException("A drop has at least one path.", nameof(paths));
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("Every dropped path must be absolute.", nameof(paths));
        }
        Paths = [.. paths];
        Target = target;
    }

    /// <summary>Gets the absolute local paths of the dropped files and directories, in the order the platform reported them.</summary>
    /// <remarks>
    /// They come from the native window, never from the page. In a Flatpak sandbox they are document-portal paths
    /// under <c>/run/user/&lt;uid&gt;/doc</c>, which the application can open; in a macOS App Sandbox the drop grants
    /// access to them while the application runs.
    /// </remarks>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>
    /// Gets the <c>data-runic-drop-target</c> attribute of the nearest element under the drop that has one, or
    /// <see langword="null"/> when there is none or the page did not report it.
    /// </summary>
    /// <remarks>
    /// The page reports it, so treat it as an untrusted routing label, such as which folder of a list the files were
    /// dropped on: never as a path, and never for an access decision.
    /// </remarks>
    public string? Target { get; }
}

/// <summary>An optional native host that reports files the user dropped onto its window.</summary>
/// <remarks>
/// Report <see cref="DesktopWindowCapabilities.FileDrop"/> in <see cref="IDesktopWindowHost.Capabilities"/>. Take the
/// paths from the platform's drag and drop, never from the page, and keep the page's own drop events working. Runic
/// Desktop passes each drop to <see cref="DesktopWindow.FilesDropped"/>.
/// </remarks>
public interface IDesktopFileDropWindowHost : IDesktopWindowHost
{
    /// <summary>Occurs once for each drop of files onto the window, on any thread.</summary>
    event EventHandler<DesktopFileDropEventArgs>? FilesDropped;
}
