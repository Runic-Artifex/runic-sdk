using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>
/// Moves files and directories to the user's trash: the freedesktop.org Trash on Linux, the Trash on macOS and the
/// Recycle Bin on Windows, from where the user can restore them.
/// </summary>
/// <remarks>
/// <para>
/// A path is a fully qualified local path to a file, a directory with its contents, or a symbolic link, which is
/// trashed itself and not followed. Paths are supplied by trusted C# application code, never directly by a web client.
/// </para>
/// <para>
/// Outcomes common to every platform: a moved item is <see cref="PlatformResult{T}.Success"/>; a path that does not
/// exist is <see cref="PlatformFailureCode.NotFound"/>; access refused is <see cref="PlatformFailureCode.PermissionDenied"/>;
/// a location without a usable trash, such as a network share or a volume that has none, is
/// <see cref="PlatformResult{T}.Unavailable"/> with <see cref="PlatformUnavailableReason.BackendUnavailable"/>. The
/// item is never deleted permanently instead: it stays where it was unless the result is a success. A failed
/// input/output operation, such as on a full disk, is <see cref="PlatformFailureCode.IOError"/>; other native errors
/// are <see cref="PlatformFailureCode.Other"/> with a diagnostic.
/// </para>
/// <para>
/// Listing, restoring and emptying the trash are available on Windows and on Linux outside Flatpak and Snap. macOS
/// has no public interface for them and the Trash portal only moves items to the trash, so there they report
/// <see cref="PlatformResult{T}.Unavailable"/> with <see cref="PlatformUnavailableReason.BackendUnavailable"/>, as
/// the default implementations do.
/// </para>
/// </remarks>
public interface IDesktopTrash
{
    /// <summary>Moves the file, directory or symbolic link at <paramref name="path"/> to the trash.</summary>
    /// <param name="path">A fully qualified local path.</param>
    /// <param name="cancellationToken">Cancels the request before the item is moved; a move in progress completes.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty, relative, contains NUL or names a root directory.</exception>
    ValueTask<PlatformResult<PlatformUnit>> MoveToTrashAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Moves several files, directories or symbolic links to the trash, one after another, reporting progress.</summary>
    /// <param name="paths">Fully qualified local paths, each checked as <see cref="MoveToTrashAsync"/> checks one.</param>
    /// <param name="progress">Receives the number of items handled after each one, and the total.</param>
    /// <param name="cancellationToken">Cancels before the next item is moved; a move in progress completes.</param>
    /// <returns>One result per path, in the order of <paramref name="paths"/>, each as <see cref="MoveToTrashAsync"/>
    /// reports it. A failed item does not stop the others.</returns>
    /// <remarks>
    /// The default implementation calls <see cref="MoveToTrashAsync"/> for each path, so each item is moved or left
    /// where it was on its own, and never deleted permanently. Cancellation throws <see cref="OperationCanceledException"/>
    /// after the item in progress: the items before it, as many as <paramref name="progress"/> last reported, are in the
    /// trash.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A path is empty, relative, contains NUL or names a root directory; nothing
    /// was moved.</exception>
    async ValueTask<ImmutableArray<PlatformResult<PlatformUnit>>> MoveAllToTrashAsync(IReadOnlyList<string> paths,
        IProgress<DesktopTrashProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (string path in paths) ItemPaths.Validate(path, nameof(paths));
        cancellationToken.ThrowIfCancellationRequested();
        var results = ImmutableArray.CreateBuilder<PlatformResult<PlatformUnit>>(paths.Count);
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await MoveToTrashAsync(path, CancellationToken.None).ConfigureAwait(false));
            progress?.Report(new DesktopTrashProgress(results.Count, paths.Count));
        }
        return results.MoveToImmutable();
    }

    /// <summary>Lists the items in the user's trash.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The items, in no particular order: on Windows those of the Recycle Bin of every drive, on Linux those of the
    /// home trash and of the trash directories of mounted filesystems. An empty trash is an empty array.
    /// </returns>
    /// <remarks>The default implementation returns <see cref="PlatformResult{T}.Unavailable"/> with
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>.</remarks>
    ValueTask<PlatformResult<ImmutableArray<DesktopTrashItem>>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PlatformResult<ImmutableArray<DesktopTrashItem>>>(
            new PlatformResult<ImmutableArray<DesktopTrashItem>>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
    }

    /// <summary>Moves a trashed item back to its <see cref="DesktopTrashItem.OriginalPath"/>.</summary>
    /// <param name="itemId">The item's <see cref="DesktopTrashItem.Id"/> from <see cref="ListAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the request before the item is moved.</param>
    /// <returns>
    /// <see cref="PlatformResult{T}.Success"/> once the item is back; <see cref="PlatformFailureCode.Conflict"/> when
    /// something now exists at the original path, which is never replaced; <see cref="PlatformFailureCode.NotFound"/>
    /// when the item is no longer in the trash. A missing parent directory of the original path is created again.
    /// </returns>
    /// <remarks>The default implementation returns <see cref="PlatformResult{T}.Unavailable"/> with
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>.</remarks>
    /// <exception cref="ArgumentException"><paramref name="itemId"/> is empty.</exception>
    ValueTask<PlatformResult<PlatformUnit>> RestoreAsync(string itemId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
    }

    /// <summary>Deletes every item in the trash permanently, without asking the user.</summary>
    /// <param name="cancellationToken">Cancels the request before deletion starts; a deletion in progress completes.</param>
    /// <remarks>
    /// Ask the user first: the items cannot be restored afterwards. The trashes <see cref="ListAsync"/> lists are
    /// emptied; a symbolic link is deleted itself and never followed. The default implementation returns
    /// <see cref="PlatformResult{T}.Unavailable"/> with <see cref="PlatformUnavailableReason.BackendUnavailable"/>.
    /// </remarks>
    ValueTask<PlatformResult<PlatformUnit>> EmptyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
    }
}

/// <summary>The progress of <see cref="IDesktopTrash.MoveAllToTrashAsync"/>.</summary>
/// <param name="Completed">The number of items handled so far, moved or not.</param>
/// <param name="Total">The number of items in the request.</param>
public readonly record struct DesktopTrashProgress(int Completed, int Total);

/// <summary>One item in the trash, as <see cref="IDesktopTrash.ListAsync"/> reports it.</summary>
/// <param name="Id">The item's identifier for <see cref="IDesktopTrash.RestoreAsync"/>, stable while the item stays in
/// the trash. Treat it as opaque.</param>
/// <param name="OriginalPath">The fully qualified path the item was trashed from, where restoring puts it back.</param>
public sealed record DesktopTrashItem(string Id, string OriginalPath)
{
    /// <summary>When the item was moved to the trash, when known.</summary>
    public DateTimeOffset? DeletedAt { get; init; }
    /// <summary>The item's size in bytes, when known. A directory's size is known only where the trash records it,
    /// such as the freedesktop.org <c>directorysizes</c> file; Runic never walks a trashed directory to measure it.</summary>
    public long? Size { get; init; }
    /// <summary>Whether the item is a directory.</summary>
    public bool IsDirectory { get; init; }
}

/// <summary>The trash for hosts and platforms that cannot provide one.</summary>
public static class DesktopTrash
{
    /// <summary>Creates a trash that reports every request unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>Nothing throws except for a missing or invalid argument and caller cancellation.</remarks>
    public static IDesktopTrash Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableTrash(reason);
    }

    private sealed class UnavailableTrash(PlatformUnavailableReason reason) : IDesktopTrash, IDesktopServiceStatus
    {
        public CapabilityStatus Status { get; } = DesktopServiceStatus.Unavailable(reason);
        public ValueTask<PlatformResult<PlatformUnit>> MoveToTrashAsync(string path, CancellationToken cancellationToken = default)
        {
            ItemPaths.Validate(path, nameof(path));
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Unavailable(reason));
        }

        public ValueTask<PlatformResult<ImmutableArray<DesktopTrashItem>>> ListAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<ImmutableArray<DesktopTrashItem>>>(new PlatformResult<ImmutableArray<DesktopTrashItem>>.Unavailable(reason));
        }

        public ValueTask<PlatformResult<PlatformUnit>> RestoreAsync(string itemId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(itemId);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Unavailable(reason));
        }

        public ValueTask<PlatformResult<PlatformUnit>> EmptyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Unavailable(reason));
        }
    }
}

// The same checks as Runic.Platform.Runtime.DesktopServiceValidation.ItemPath, which providers use; this assembly has
// no reference to it.
internal static class ItemPaths
{
    internal static void Validate(string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, name);
        if (path.Contains('\0') || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Supply a fully qualified local path.", name);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Path.GetPathRoot(full) is { } root && Path.TrimEndingDirectorySeparator(root) == full)
            throw new ArgumentException("Supply a file or directory below a root directory.", name);
    }
}
