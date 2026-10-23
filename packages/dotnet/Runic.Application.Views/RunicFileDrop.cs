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
/// <remarks>
/// <see cref="Files"/> may be empty when <see cref="PromisedFiles"/> is not: a drop of files that had no local path, such
/// as a mail attachment dragged from macOS Mail.
/// </remarks>
public sealed record RunicFileDrop(IReadOnlyList<RunicDroppedFile> Files, string? Target = null)
{
    /// <summary>Gets what the drop performed: copy, move or link; <see cref="RunicDropEffect.Copy"/> by default.</summary>
    /// <remarks>
    /// The effect the drag source and the page agreed on, from the source's allowed effects, the user's modifier keys and
    /// the page's <c>data-runic-drop-effect</c> attribute. On Windows the page reports it, so treat it like
    /// <see cref="Target"/>: to choose between copying and moving the files into the application, never for an access
    /// decision.
    /// </remarks>
    public RunicDropEffect Effect { get; init; }

    /// <summary>Gets the dropped files that had no local path, each readable once; empty for most drops.</summary>
    /// <remarks>
    /// The host received them into private staging files. Read each with <see cref="RunicPromisedFile.OpenReadAsync"/>
    /// during the handler and dispose it when done; the host disposes the files that were not opened, deleting their
    /// staged copies, when the handler's task completes.
    /// </remarks>
    public IReadOnlyList<RunicPromisedFile> PromisedFiles { get; init; } = [];
}

/// <summary>What a drop onto a Window performed, for <see cref="RunicFileDrop.Effect"/>.</summary>
/// <remarks>A closed set: the effects every desktop's drag and drop shares.</remarks>
public enum RunicDropEffect
{
    /// <summary>The files are copied into the application.</summary>
    Copy = 0,

    /// <summary>The files are moved: the drag source deletes them once the application has taken them.</summary>
    Move = 1,

    /// <summary>The application links to the files where they are.</summary>
    Link = 2,
}

/// <summary>A dropped file that had no local path, such as a mail attachment, for <see cref="RunicFileDrop.PromisedFiles"/>.</summary>
/// <remarks>The host stages the file privately; read it once and dispose it, which deletes the staged copy.</remarks>
public abstract class RunicPromisedFile : IAsyncDisposable
{
    /// <summary>Gets the file name, without a directory, as the drag source named it and sanitized for display.</summary>
    public abstract string Name { get; }

    /// <summary>Opens the file's content; a promised file can be opened once.</summary>
    /// <param name="cancellationToken">Cancels opening.</param>
    /// <returns>The content, owned by this file: disposing the file closes it.</returns>
    /// <exception cref="InvalidOperationException">The file was already opened.</exception>
    /// <exception cref="ObjectDisposedException">The file was disposed.</exception>
    public abstract ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the content and deletes the staged copy.</summary>
    /// <returns>A task that completes when the copy is deleted.</returns>
    public abstract ValueTask DisposeAsync();
}

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
