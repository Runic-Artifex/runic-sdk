using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>What a drag and drop does with the dragged files: the effects a drag source allows and the one a drop performed.</summary>
/// <remarks>A closed set: the effects every desktop's drag and drop shares.</remarks>
[Flags]
public enum DesktopDragEffects
{
    /// <summary>Nothing: the drag was cancelled or dropped where nothing accepted it.</summary>
    None = 0,

    /// <summary>The target copies the data.</summary>
    Copy = 1,

    /// <summary>The target moves the files; the source deletes what remains.</summary>
    Move = 2,

    /// <summary>The target links to the files, for example with a shortcut or an alias.</summary>
    Link = 4,
}

/// <summary>The data of a drag out of a window, for <see cref="IWindowPlatformServices.StartDragAsync"/>.</summary>
/// <remarks>
/// <para>
/// Set at least one of <see cref="Paths"/>, <see cref="Text"/> and <see cref="Uris"/>. The data comes from the
/// application, never from the page: a page asks for a drag through an ordinary command, and the ViewModel builds the
/// data from its own state.
/// </para>
/// <para>
/// To drag content that has no file yet, write it to a file first and drag the file's path: the desktops render
/// promised content differently, so Runic drags only existing files.
/// </para>
/// </remarks>
public sealed record DesktopDragData
{
    /// <summary>Gets the absolute paths of existing files and directories to drag; at most 1024.</summary>
    public ImmutableArray<string> Paths { get; init; } = [];

    /// <summary>Gets plain text to drag, at most 1 MiB (1,048,576 UTF-16 code units) without NUL; <see langword="null"/> for none.</summary>
    public string? Text { get; init; }

    /// <summary>Gets absolute URIs to drag, such as web links; at most 1024.</summary>
    /// <remarks><c>file:</c> URIs are refused: drag local files through <see cref="Paths"/>.</remarks>
    public ImmutableArray<Uri> Uris { get; init; } = [];

    /// <summary>Gets the effects the drop target may choose; <see cref="DesktopDragEffects.Copy"/> by default.</summary>
    /// <remarks>
    /// <see cref="DesktopDragEffects.Move"/> and <see cref="DesktopDragEffects.Link"/> need <see cref="Paths"/>. With
    /// <see cref="DesktopDragEffects.Move"/> the application deletes what remains of the files when the drag reports a
    /// move; a target may already have moved them.
    /// </remarks>
    public DesktopDragEffects AllowedEffects { get; init; } = DesktopDragEffects.Copy;
}
