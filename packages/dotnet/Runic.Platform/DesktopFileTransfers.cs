using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>
/// Copies and moves files and directories with progress, cancellation and a decision for each conflict, as a file
/// manager does.
/// </summary>
/// <remarks>
/// <para>
/// A transfer puts each source into <see cref="FileTransferRequest.DestinationDirectory"/> under its own name. A file is
/// written under a temporary name in the destination and renamed into place once complete, so cancellation or a
/// failure never leaves a partial file under the real name. A move within one file system renames each source; a
/// move to another file system copies it and then removes the source, only after its copy is complete.
/// </para>
/// <para>
/// A transfer keeps the content, the last write time and, on Linux and macOS, the permission bits of every file and
/// directory, and on Windows the read-only, hidden and archive attributes. Symbolic links are copied as links and
/// never followed. It does not keep owners, access control lists, extended attributes, alternate data streams,
/// macOS resource forks and Finder information, hard links, which become separate files, or sparse regions. Other
/// special files, such as named pipes and devices, are skipped.
/// </para>
/// <para>
/// Outcomes: a completed transfer is <see cref="PlatformResult{T}.Success"/> with a summary. The first error stops the
/// transfer and is <see cref="PlatformResult{T}.Failed"/>: <see cref="PlatformFailureCode.NotFound"/> for a missing
/// source or destination, <see cref="PlatformFailureCode.PermissionDenied"/>,
/// <see cref="PlatformFailureCode.Conflict"/> for a conflict without a <see cref="FileTransferRequest.ResolveConflict"/>
/// handler, <see cref="PlatformFailureCode.UserDismissed"/> when the handler answers
/// <see cref="FileConflictAction.Cancel"/>, <see cref="PlatformFailureCode.IOError"/>, such as for a full disk, and
/// <see cref="PlatformFailureCode.Other"/> for a native error no other code describes.
/// Items transferred before an error or cancellation stay transferred; the item in progress is left as it was.
/// Caller cancellation throws <see cref="OperationCanceledException"/>.
/// </para>
/// </remarks>
public interface IDesktopFileTransfers
{
    /// <summary>Copies the request's sources into its destination directory.</summary>
    /// <exception cref="ArgumentException">
    /// A source or the destination is not a fully qualified local path, there is no source, or the destination is a
    /// source or inside one.
    /// </exception>
    ValueTask<PlatformResult<FileTransferSummary>> CopyAsync(FileTransferRequest request, CancellationToken cancellationToken = default);

    /// <summary>Moves the request's sources into its destination directory.</summary>
    /// <exception cref="ArgumentException">
    /// A source or the destination is not a fully qualified local path, there is no source, or the destination is a
    /// source or inside one.
    /// </exception>
    ValueTask<PlatformResult<FileTransferSummary>> MoveAsync(FileTransferRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What to copy or move, and where.</summary>
/// <param name="Sources">Fully qualified paths of files, directories and symbolic links.</param>
/// <param name="DestinationDirectory">The existing directory that receives every source under its own name.</param>
public sealed record FileTransferRequest(ImmutableArray<string> Sources, string DestinationDirectory)
{
    /// <summary>Receives progress about ten times a second and once when the transfer completes.</summary>
    /// <remarks>Reports come from a worker thread. <see cref="Progress{T}"/> created in a command forwards them to the
    /// command's context, such as a ViewModel's model context.</remarks>
    public IProgress<FileTransferProgress>? Progress { get; init; }

    /// <summary>
    /// Decides what to do when an entry of the destination has a source's name. Without a handler the transfer stops
    /// at the first conflict with <see cref="PlatformFailureCode.Conflict"/>, and nothing is replaced.
    /// </summary>
    /// <remarks>
    /// The handler is called on the <see cref="SynchronizationContext"/> that was current when the transfer started,
    /// such as a ViewModel's model context in a command, otherwise on the thread pool, one conflict at a time. It can
    /// ask the user, for example through an interaction; the transfer waits for its answer. Its cancellation token is
    /// the transfer's.
    /// </remarks>
    public FileConflictHandler? ResolveConflict { get; init; }
}

/// <summary>Decides one conflict of a file transfer.</summary>
public delegate ValueTask<FileConflictResolution> FileConflictHandler(FileTransferConflict conflict, CancellationToken cancellationToken);

/// <summary>A source whose name an entry of the destination already has.</summary>
/// <param name="SourcePath">The source being transferred, a top-level source or an entry inside a merged directory.</param>
/// <param name="DestinationPath">The existing entry.</param>
/// <param name="Source">What the source is.</param>
/// <param name="Destination">What the existing entry is.</param>
public sealed record FileTransferConflict(string SourcePath, string DestinationPath, FileEntryInfo Source, FileEntryInfo Destination)
{
    /// <summary>Whether both are directories, so that <see cref="FileConflictAction.Merge"/> is possible.</summary>
    public bool CanMerge => Source.IsDirectory && Destination.IsDirectory;
}

/// <summary>A file, directory or symbolic link in a conflict, for showing the user what would be replaced.</summary>
/// <param name="IsDirectory">Whether it is a directory; a symbolic link is not, even when it leads to one.</param>
/// <param name="Length">The size in bytes of a file, 0 for a directory or link.</param>
/// <param name="LastWriteTime">When it was last written.</param>
public sealed record FileEntryInfo(bool IsDirectory, long Length, DateTimeOffset LastWriteTime);

/// <summary>What to do about a conflict.</summary>
/// <remarks>This set is closed: it gains values only in a major release.</remarks>
public enum FileConflictAction
{
    /// <summary>Leave the existing entry and do not transfer the source; a moved source stays where it is.</summary>
    Skip,
    /// <summary>Replace the existing entry with the source; a directory is replaced with all its contents.</summary>
    Replace,
    /// <summary>Transfer the source under a free name, such as <c>report (2).txt</c>.</summary>
    KeepBoth,
    /// <summary>Transfer a directory's contents into the existing directory, deciding each conflict inside it.</summary>
    /// <remarks>Only when <see cref="FileTransferConflict.CanMerge"/>; otherwise the transfer throws
    /// <see cref="InvalidOperationException"/>.</remarks>
    Merge,
    /// <summary>Stop the transfer; it returns <see cref="PlatformFailureCode.UserDismissed"/>.</summary>
    Cancel
}

/// <summary>The decision for one conflict, and optionally for every later one.</summary>
/// <param name="Action">What to do.</param>
public sealed record FileConflictResolution(FileConflictAction Action)
{
    /// <summary>
    /// Applies <see cref="Action"/> to every later conflict of the same kind, between two directories or not, without
    /// asking. <see cref="FileConflictAction.Merge"/> applies to directory conflicts only.
    /// </summary>
    public bool ApplyToAll { get; init; }
}

/// <summary>How far a file transfer is.</summary>
/// <param name="CompletedItems">Files, directories and links done or skipped.</param>
/// <param name="TotalItems">Files, directories and links in every source.</param>
/// <param name="CompletedBytes">Bytes of file content done or skipped.</param>
/// <param name="TotalBytes">Bytes of file content in every source.</param>
/// <param name="CurrentPath">The source entry being transferred, or <see langword="null"/> once the transfer completed.</param>
public sealed record FileTransferProgress(int CompletedItems, int TotalItems, long CompletedBytes, long TotalBytes, string? CurrentPath);

/// <summary>What a completed transfer did.</summary>
/// <param name="Transferred">Files, directories and links copied or moved.</param>
/// <param name="Skipped">Files, directories and links skipped, by a <see cref="FileConflictAction.Skip"/> or as special files.</param>
/// <param name="Bytes">Bytes of file content copied or moved.</param>
public sealed record FileTransferSummary(int Transferred, int Skipped, long Bytes);

/// <summary>File transfers for hosts and platforms that cannot provide them.</summary>
public static class DesktopFileTransfers
{
    /// <summary>Creates file transfers that report every request unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>Nothing throws except for a missing or invalid argument and caller cancellation.</remarks>
    public static IDesktopFileTransfers Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableTransfers(reason);
    }

    private sealed class UnavailableTransfers(PlatformUnavailableReason reason) : IDesktopFileTransfers
    {
        public ValueTask<PlatformResult<FileTransferSummary>> CopyAsync(FileTransferRequest request, CancellationToken cancellationToken = default) =>
            Report(request, cancellationToken);

        public ValueTask<PlatformResult<FileTransferSummary>> MoveAsync(FileTransferRequest request, CancellationToken cancellationToken = default) =>
            Report(request, cancellationToken);

        private ValueTask<PlatformResult<FileTransferSummary>> Report(FileTransferRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Sources.IsDefaultOrEmpty) throw new ArgumentException("Supply at least one source.", nameof(request));
            foreach (string source in request.Sources) ItemPaths.Validate(source, nameof(request));
            ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationDirectory, nameof(request));
            if (!Path.IsPathFullyQualified(request.DestinationDirectory) || request.DestinationDirectory.Contains('\0'))
                throw new ArgumentException("Supply a fully qualified destination directory.", nameof(request));
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<FileTransferSummary>>(new PlatformResult<FileTransferSummary>.Unavailable(reason));
        }
    }
}
