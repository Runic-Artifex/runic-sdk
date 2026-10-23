using Runic.Platform;

namespace Runic.Desktop;

/// <summary>Provides the files the user dropped onto a window, for <see cref="DesktopWindow.FilesDropped"/>.</summary>
public sealed class DesktopFileDropEventArgs : EventArgs
{
    /// <summary>Creates event data for dropped files.</summary>
    /// <param name="paths">The absolute local paths of the dropped files and directories, as the platform reported them.</param>
    /// <param name="target">The page's routing label, or <see langword="null"/>; see <see cref="Target"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="paths"/> is empty, or contains an empty or relative path.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads",
        Justification = "The promised-files overload takes a required label: an optional one would make new(paths, null) ambiguous.")]
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
        PromisedFiles = [];
        Target = target;
    }

    /// <summary>Creates event data for a drop that may include files without a local path.</summary>
    /// <param name="paths">The absolute local paths of the dropped files and directories; may be empty when <paramref name="promisedFiles"/> is not.</param>
    /// <param name="promisedFiles">The dropped files that had no local path, as read leases; see <see cref="PromisedFiles"/>.</param>
    /// <param name="target">The page's routing label, or <see langword="null"/>; see <see cref="Target"/>.</param>
    /// <exception cref="ArgumentException">
    /// Both lists are empty, <paramref name="paths"/> contains an empty or relative path, or <paramref name="promisedFiles"/>
    /// contains <see langword="null"/>.
    /// </exception>
    public DesktopFileDropEventArgs(IReadOnlyList<string> paths, IReadOnlyList<IReadFileLease> promisedFiles, string? target)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(promisedFiles);
        if (paths.Count == 0 && promisedFiles.Count == 0)
            throw new ArgumentException("A drop has at least one path or promised file.", nameof(paths));
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("Every dropped path must be absolute.", nameof(paths));
        }
        foreach (var file in promisedFiles)
        {
            if (file is null) throw new ArgumentException("A promised file is null.", nameof(promisedFiles));
        }
        Paths = [.. paths];
        PromisedFiles = [.. promisedFiles];
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

    /// <summary>Gets the dropped files that had no local path, such as a mail attachment, each as a read lease.</summary>
    /// <remarks>
    /// <para>
    /// Empty unless the platform delivered promised content: on macOS a file promise (<c>NSFilePromiseReceiver</c>), which
    /// Runic receives into a private staging directory. Read each lease once with
    /// <see cref="IReadFileLease.OpenReadAsync"/>; disposing it deletes the staged copy. A lease that no handler disposes
    /// stays until the process ends, so dispose every lease of the drop, opened or not, when done with it.
    /// </para>
    /// <para><see cref="Paths"/> may be empty when this list is not.</para>
    /// </remarks>
    public IReadOnlyList<IReadFileLease> PromisedFiles { get; }

    /// <summary>Gets what the drop performed: <see cref="DesktopDragEffects.Copy"/>, <see cref="DesktopDragEffects.Move"/> or <see cref="DesktopDragEffects.Link"/>.</summary>
    /// <remarks>
    /// The effect the drag source and the page agreed on, from the drag source's allowed effects, the user's modifier
    /// keys and the page's <c>data-runic-drop-effect</c> attribute or <c>dropEffect</c>. <see cref="DesktopDragEffects.Copy"/>
    /// by default. On WebView2 the page reports it, so it is as untrusted as <see cref="Target"/>: use it to choose
    /// between copying and moving the user's files into the application, never for an access decision.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not exactly one of Copy, Move or Link.</exception>
    public DesktopDragEffects Effect
    {
        get;
        init => field = value is DesktopDragEffects.Copy or DesktopDragEffects.Move or DesktopDragEffects.Link
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "A drop performs exactly one of Copy, Move or Link.");
    } = DesktopDragEffects.Copy;
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

/// <summary>An optional native host that starts drags out of its window.</summary>
/// <remarks>
/// Report <see cref="DesktopWindowCapabilities.FileDrag"/> in <see cref="IDesktopWindowHost.Capabilities"/>. Runic
/// Desktop finds the interface by type and calls <see cref="StartDragAsync"/> for
/// <see cref="DesktopWindow.StartDragAsync"/> with checked data.
/// </remarks>
public interface IDesktopDragSourceWindowHost : IDesktopWindowHost
{
    /// <summary>Starts the platform's drag and drop with <paramref name="data"/> and completes with the drop's effect.</summary>
    /// <param name="data">The data, already checked against the rules of <see cref="DesktopDragData"/>.</param>
    /// <param name="cancellationToken">Cancels before the drag starts.</param>
    /// <returns>
    /// The effect the target chose, one of <see cref="DesktopDragData.AllowedEffects"/>, or
    /// <see cref="DesktopDragEffects.None"/> when the drag was cancelled, dropped nowhere, or no mouse button was held.
    /// </returns>
    /// <remarks>
    /// Start the drag on the window's UI thread from the pointer's current position, offering the paths as the
    /// platform's file list, the text as plain text and the URIs as links, and complete once the drag has ended.
    /// </remarks>
    ValueTask<DesktopDragEffects> StartDragAsync(DesktopDragData data, CancellationToken cancellationToken = default);
}
