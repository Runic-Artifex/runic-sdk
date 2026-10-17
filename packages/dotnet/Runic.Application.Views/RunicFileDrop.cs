namespace Runic.Application.Views;

/// <summary>
/// A Window's root ViewModel that receives the files the user drops onto the Window, with their real paths.
/// </summary>
/// <remarks>
/// <para>
/// Implement it on the ViewModel a Window opens with, and open the dropped files from the paths:
/// <code>
/// public sealed partial class EditorViewModel : ObservableObject, IRunicFileDropTarget
/// {
///     public async ValueTask OnFilesDroppedAsync(RunicFileDrop drop, CancellationToken cancellationToken)
///     {
///         foreach (var file in drop.Files.Where(static file =&gt; !file.IsDirectory))
///             await OpenAsync(file.Path, cancellationToken);
///     }
/// }
/// </code>
/// The host calls it in the ViewModel's model turn, as a command runs, once for each drop, in drop order. The token is
/// cancelled when the Window closes. A failure is logged (event 1054, <c>WindowFileDropFailed</c>) and the Window stays
/// open.
/// </para>
/// <para>
/// The paths come from the native window, never from the page: Runic Desktop's embedded window on Windows, macOS and
/// Linux (GTK 3 and GTK 4) and the WPF child web View deliver drops. CS-WebUI and an installed browser have no
/// native window that could report them; the page there only gets HTML5 <c>File</c> objects without paths. The page
/// still receives its own <c>dragenter</c>, <c>dragover</c> and <c>drop</c> events, so it can highlight a drop target;
/// dropping a file never navigates the Window away. A nested View that should receive drops on its own area marks
/// its element with <c>data-runic-drop-target</c>; the root ViewModel routes the drop by <see cref="RunicFileDrop.Target"/>.
/// </para>
/// </remarks>
public interface IRunicFileDropTarget
{
    /// <summary>Receives one drop of files onto the Window.</summary>
    /// <param name="drop">The dropped files and the page's routing label.</param>
    /// <param name="cancellationToken">Cancelled when the Window closes.</param>
    /// <returns>A task that completes when the drop has been handled.</returns>
    ValueTask OnFilesDroppedAsync(RunicFileDrop drop, CancellationToken cancellationToken);
}

/// <summary>One drop of files onto a Window, for <see cref="IRunicFileDropTarget"/>.</summary>
/// <param name="Files">The dropped files and directories, in the order the platform reported them; at least one.</param>
/// <param name="Target">
/// The <c>data-runic-drop-target</c> attribute of the nearest element under the drop that has one, or
/// <see langword="null"/>. The page reports it: use it as an untrusted routing label, such as which folder of a list
/// the files were dropped on, never as a path and never for an access decision.
/// </param>
public sealed record RunicFileDrop(IReadOnlyList<RunicDroppedFile> Files, string? Target = null);

/// <summary>A file or directory dropped onto a Window.</summary>
/// <param name="Path">
/// The absolute local path, from the native window. In a Flatpak sandbox it is a document-portal path under
/// <c>/run/user/&lt;uid&gt;/doc</c>; in a macOS App Sandbox the drop grants access while the application runs.
/// </param>
/// <param name="IsDirectory">Whether the path was a directory when it was dropped.</param>
public sealed record RunicDroppedFile(string Path, bool IsDirectory)
{
    /// <summary>Gets the file or directory name, the last segment of <see cref="Path"/>.</summary>
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));

    /// <summary>Creates the description of a dropped path, reading whether it is a directory.</summary>
    /// <param name="path">The absolute local path.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or not absolute.</exception>
    public static RunicDroppedFile FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("A dropped path must be absolute.", nameof(path));
        return new RunicDroppedFile(path, Directory.Exists(path));
    }
}
