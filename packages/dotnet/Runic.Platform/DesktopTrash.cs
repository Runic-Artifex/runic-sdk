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
/// </remarks>
public interface IDesktopTrash
{
    /// <summary>Moves the file, directory or symbolic link at <paramref name="path"/> to the trash.</summary>
    /// <param name="path">A fully qualified local path.</param>
    /// <param name="cancellationToken">Cancels the request before the item is moved; a move in progress completes.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty, relative, contains NUL or names a root directory.</exception>
    ValueTask<PlatformResult<PlatformUnit>> MoveToTrashAsync(string path, CancellationToken cancellationToken = default);
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
