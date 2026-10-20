using System.Buffers;
using System.Collections.Immutable;

namespace Runic.Platform.Runtime;

/// <summary>Creates the managed file transfer engine that every platform shares.</summary>
public static class LocalFileTransfers
{
    /// <summary>Creates file transfers for local file systems.</summary>
    /// <remarks>
    /// The engine copies file content in 1 MiB blocks, so it reports progress and stops at cancellation within a
    /// block. A move renames each source when it stays on its file system. The only native calls are an exclusive
    /// rename (<c>renameat2(RENAME_NOREPLACE)</c> on Linux, <c>renamex_np(RENAME_EXCL)</c> on macOS, <c>MoveFileExW</c>
    /// on Windows) and, on Linux and macOS, reading an entry's type.
    /// </remarks>
    public static IDesktopFileTransfers Create() => new FileTransferEngine();
}

internal sealed class FileTransferEngine(TimeProvider? time = null, int blockSize = 1 << 20) : IDesktopFileTransfers
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly int _blockSize = blockSize;

    public ValueTask<PlatformResult<FileTransferSummary>> CopyAsync(FileTransferRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(request, move: false, cancellationToken);

    public ValueTask<PlatformResult<FileTransferSummary>> MoveAsync(FileTransferRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(request, move: true, cancellationToken);

    private async ValueTask<PlatformResult<FileTransferSummary>> RunAsync(FileTransferRequest request, bool move, CancellationToken cancellationToken)
    {
        var (sources, destination) = Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        var context = SynchronizationContext.Current;
        if (context?.GetType() == typeof(SynchronizationContext)) context = null;
        var run = new Run(this, request, move, context, cancellationToken);
        // The transfer runs on the thread pool; only the conflict handler returns to the caller's context.
        return await Task.Run(() => run.ExecuteAsync(sources, destination), cancellationToken).ConfigureAwait(false);
    }

    internal static (ImmutableArray<string> Sources, string Destination) Validate(FileTransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Sources.IsDefaultOrEmpty) throw new ArgumentException("Supply at least one source.", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationDirectory, nameof(request));
        if (request.DestinationDirectory.Contains('\0') || !Path.IsPathFullyQualified(request.DestinationDirectory))
            throw new ArgumentException("Supply a fully qualified destination directory.", nameof(request));
        string destination = Path.GetFullPath(request.DestinationDirectory);
        if (Path.GetPathRoot(destination) is not { } root || Path.TrimEndingDirectorySeparator(root) != Path.TrimEndingDirectorySeparator(destination))
            destination = Path.TrimEndingDirectorySeparator(destination);
        var sources = ImmutableArray.CreateBuilder<string>(request.Sources.Length);
        foreach (string source in request.Sources)
        {
            string path;
            try { path = DesktopServiceValidation.ItemPath(source); }
            catch (ArgumentException error) { throw new ArgumentException(error.Message, nameof(request), error); }
            if (SamePath(path, destination) || IsInside(destination, path))
                throw new ArgumentException("The destination is a source or inside one.", nameof(request));
            sources.Add(path);
        }
        return (sources.MoveToImmutable(), destination);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool SamePath(string first, string second) => string.Equals(first, second, PathComparison);

    private static bool IsInside(string path, string directory) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, PathComparison);

    // "report (2).txt", "report (3).txt"; a directory or a name without an extension gets the number at its end.
    internal static string FreeName(string directory, string name, bool isDirectory)
    {
        string extension = isDirectory ? "" : Path.GetExtension(name);
        if (extension.Length == name.Length) extension = ""; // ".bashrc"
        string stem = name[..^extension.Length];
        for (int number = 2; ; number++)
        {
            string candidate = $"{stem} ({number}){extension}";
            if (!FileTransferNative.Exists(Path.Combine(directory, candidate))) return candidate;
        }
    }

    private enum Kind { File, Directory, Link, Special }

    // A source entry, read before anything is written so that totals are known and a destination inside a source,
    // reached through a link, is never walked.
    private sealed class Node(string path, Kind kind, long length, DateTime lastWriteUtc)
    {
        internal string Path { get; } = path;
        internal string Name => System.IO.Path.GetFileName(Path);
        internal Kind Kind { get; } = kind;
        internal long Length { get; } = length;
        internal DateTime LastWriteUtc { get; } = lastWriteUtc;
        internal string? LinkTarget { get; init; }
        internal bool LinkToDirectory { get; init; }
        internal UnixFileMode Mode { get; init; }
        internal FileAttributes Attributes { get; init; }
        internal List<Node> Children { get; } = [];
        internal int Items { get; set; } = 1;
        internal long Bytes { get; set; } = length;
        internal FileEntryInfo Info => new(Kind == Kind.Directory, Kind == Kind.File ? Length : 0, new DateTimeOffset(LastWriteUtc));
    }

    private static Node Scan(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = new FileInfo(path);
        if (file.LinkTarget is { } linkTarget)
            return new Node(path, Kind.Link, 0, file.LastWriteTimeUtc)
            {
                LinkTarget = linkTarget,
                LinkToDirectory = file.Attributes.HasFlag(FileAttributes.Directory),
                Attributes = file.Attributes,
            };
        var directory = new DirectoryInfo(path);
        if (directory.Exists)
        {
            var node = new Node(path, Kind.Directory, 0, directory.LastWriteTimeUtc)
            { Mode = OperatingSystem.IsWindows() ? 0 : directory.UnixFileMode, Attributes = directory.Attributes };
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
            foreach (var entry in directory.EnumerateFileSystemInfos("*", options).OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                var child = Scan(entry.FullName, cancellationToken);
                node.Children.Add(child);
                node.Items += child.Items;
                node.Bytes += child.Bytes;
            }
            return node;
        }
        if (!file.Exists) throw new FileNotFoundException(null, path);
        if (!FileTransferNative.IsRegularFile(path)) return new Node(path, Kind.Special, 0, file.LastWriteTimeUtc);
        return new Node(path, Kind.File, file.Length, file.LastWriteTimeUtc)
        { Mode = OperatingSystem.IsWindows() ? 0 : file.UnixFileMode, Attributes = file.Attributes };
    }

    private static FileEntryInfo? Probe(string path)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null) return new FileEntryInfo(false, 0, new DateTimeOffset(file.LastWriteTimeUtc));
        if (file.Exists) return new FileEntryInfo(false, file.Length, new DateTimeOffset(file.LastWriteTimeUtc));
        var directory = new DirectoryInfo(path);
        if (directory.Exists) return new FileEntryInfo(true, 0, new DateTimeOffset(directory.LastWriteTimeUtc));
        return FileTransferNative.Exists(path) ? new FileEntryInfo(false, 0, default) : null;
    }

    // Stops the transfer with a typed outcome.
    private sealed class TransferFailedException(PlatformFailureCode code, PlatformDiagnostic? diagnostic = null) : Exception
    {
        internal PlatformFailureCode Code { get; } = code;
        internal PlatformDiagnostic? Diagnostic { get; } = diagnostic;
    }

    internal static (PlatformFailureCode Code, PlatformDiagnostic? Diagnostic) Classify(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => (PlatformFailureCode.NotFound, null),
        UnauthorizedAccessException => (PlatformFailureCode.PermissionDenied, null),
        IOException io when OperatingSystem.IsWindows() => (FileTransferNative.CodeForWin32(io.HResult & 0xFFFF), PlatformDiagnostic.FromHResult(io.HResult)),
        // .NET reports the errno as the HResult of an IOException on Linux and macOS.
        IOException io when io.HResult is > 0 and < 4096 => (FileTransferNative.CodeForErrno(io.HResult), PlatformDiagnostic.FromErrno(io.HResult)),
        _ => (PlatformFailureCode.IOError, null),
    };

    // What one item did, so that it can be undone when the item cannot complete.
    private abstract record Change
    {
        internal sealed record Created(string Path, bool IsDirectory) : Change;
        internal sealed record Renamed(string From, string To) : Change;
    }

    private sealed class Run(FileTransferEngine engine, FileTransferRequest request, bool move, SynchronizationContext? context,
        CancellationToken cancellationToken)
    {
        private readonly Dictionary<bool, FileConflictAction> _always = [];
        private readonly List<Change> _changes = [];
        // Sources moved by copying, and replaced entries moved aside: removed once their top-level item is complete.
        private readonly List<Node> _copiedSources = [];
        private readonly List<string> _replaced = [];
        private int _totalItems, _completedItems, _transferred, _skipped;
        private long _totalBytes, _completedBytes, _transferredBytes;
        private long _lastReport = long.MinValue;
        private string? _current;

        internal async Task<PlatformResult<FileTransferSummary>> ExecuteAsync(ImmutableArray<string> sources, string destination)
        {
            try
            {
                if (!Directory.Exists(destination)) return new PlatformResult<FileTransferSummary>.Failed(PlatformFailureCode.NotFound);
                var nodes = sources.Select(source => Scan(source, cancellationToken)).ToList();
                _totalItems = nodes.Sum(node => node.Items);
                _totalBytes = nodes.Sum(node => node.Bytes);
                Report(force: true);
                foreach (var node in nodes)
                {
                    _changes.Clear();
                    _copiedSources.Clear();
                    _replaced.Clear();
                    try { await TransferAsync(node, destination, node.Name).ConfigureAwait(false); }
                    catch
                    {
                        Undo();
                        throw;
                    }
                    // The item is complete: what it replaced and the sources it copied go.
                    foreach (string replaced in _replaced) DeleteTree(replaced);
                    RemoveCopiedSources();
                }
                _current = null;
                Report(force: true);
                return new PlatformResult<FileTransferSummary>.Success(new FileTransferSummary(_transferred, _skipped, _transferredBytes));
            }
            catch (TransferFailedException failure) { return new PlatformResult<FileTransferSummary>.Failed(failure.Code, failure.Diagnostic); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                var (code, diagnostic) = Classify(error);
                return new PlatformResult<FileTransferSummary>.Failed(code, diagnostic);
            }
        }

        private async Task TransferAsync(Node source, string directory, string name)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _current = source.Path;
            if (source.Kind == Kind.Special) { Skip(source); return; }
            string target = Path.Combine(directory, name);
            if (Probe(target) is { } existing)
            {
                if (SamePath(source.Path, target))
                {
                    // Moving an item onto itself does nothing; copying it makes a second one.
                    if (move) { Skip(source); return; }
                    target = Path.Combine(directory, FreeName(directory, name, source.Kind == Kind.Directory));
                }
                else
                {
                    var conflict = new FileTransferConflict(source.Path, target, source.Info, existing);
                    switch (await ResolveAsync(conflict).ConfigureAwait(false))
                    {
                        case FileConflictAction.Skip: Skip(source); return;
                        case FileConflictAction.Cancel: throw new TransferFailedException(PlatformFailureCode.UserDismissed);
                        case FileConflictAction.KeepBoth:
                            target = Path.Combine(directory, FreeName(directory, name, source.Kind == Kind.Directory));
                            break;
                        case FileConflictAction.Merge:
                            await MergeAsync(source, target).ConfigureAwait(false);
                            return;
                        case FileConflictAction.Replace:
                            // The existing entry moves aside and is removed once the whole item is complete, so a
                            // failure or cancellation can put it back.
                            string aside = Path.Combine(directory, Hidden(name, "replaced"));
                            Rename(target, aside);
                            _changes.Add(new Change.Renamed(target, aside));
                            _replaced.Add(aside);
                            break;
                    }
                }
            }

            if (move)
            {
                var renamed = FileTransferNative.Rename(source.Path, target);
                switch (renamed.Status)
                {
                    case FileTransferNative.RenameStatus.Done:
                        _changes.Add(new Change.Renamed(source.Path, target));
                        Complete(source, source.Items, source.Bytes);
                        return;
                    case FileTransferNative.RenameStatus.Exists: throw new TransferFailedException(PlatformFailureCode.Conflict);
                    case FileTransferNative.RenameStatus.Failed: throw new TransferFailedException(renamed.Code, renamed.Diagnostic);
                    // Another file system: copy, and remove the source once the whole item is done.
                }
            }
            await CopyAsync(source, target).ConfigureAwait(false);
        }

        private async Task MergeAsync(Node source, string target)
        {
            foreach (var child in source.Children) await TransferAsync(child, target, child.Name).ConfigureAwait(false);
            Complete(source, 1, 0);
            if (move) _copiedSources.Add(source);
        }

        private async Task CopyAsync(Node source, string target)
        {
            switch (source.Kind)
            {
                case Kind.File:
                    await CopyFileAsync(source, target).ConfigureAwait(false);
                    break;
                case Kind.Link:
                {
                    string staging = Path.Combine(Path.GetDirectoryName(target)!, Hidden(Path.GetFileName(target), "partial"));
                    if (source.LinkToDirectory && OperatingSystem.IsWindows()) Directory.CreateSymbolicLink(staging, source.LinkTarget!);
                    else File.CreateSymbolicLink(staging, source.LinkTarget!);
                    Place(staging, target);
                    Complete(source, 1, 0);
                    break;
                }
                case Kind.Directory:
                    Directory.CreateDirectory(target);
                    _changes.Add(new Change.Created(target, IsDirectory: true));
                    foreach (var child in source.Children)
                        await TransferAsync(child, target, child.Name).ConfigureAwait(false);
                    ApplyMetadata(source, target);
                    Complete(source, 1, 0);
                    break;
            }
            if (move) _copiedSources.Add(source);
        }

        private async Task CopyFileAsync(Node source, string target)
        {
            string staging = Path.Combine(Path.GetDirectoryName(target)!, Hidden(Path.GetFileName(target), "partial"));
            long copied = 0;
            try
            {
                var readOptions = new FileStreamOptions
                {
                    Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = 0,
                };
                var writeOptions = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                    Options = FileOptions.Asynchronous, BufferSize = 0, PreallocationSize = source.Length,
                };
                // Private until complete; the source's mode is applied before it is renamed into place.
                if (!OperatingSystem.IsWindows()) writeOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                byte[] buffer = ArrayPool<byte>.Shared.Rent(engine._blockSize);
                try
                {
                    await using var input = new FileStream(source.Path, readOptions);
                    await using var output = new FileStream(staging, writeOptions);
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, engine._blockSize), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        copied += read;
                        _completedBytes += read;
                        Report(force: false);
                    }
                    // A file that grew or shrank while it was copied is copied as read.
                    if (output.Length != copied) output.SetLength(copied);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                ApplyMetadata(source, staging);
                Place(staging, target);
            }
            catch
            {
                _completedBytes -= copied;
                TryDelete(staging);
                throw;
            }
            _transferredBytes += copied;
            _completedBytes += source.Length - copied;
            Complete(source, 1, 0, bytesAlreadyCounted: true);
        }

        // Puts a complete staging entry under its real name, never replacing an entry.
        private void Place(string staging, string target)
        {
            var result = FileTransferNative.Rename(staging, target);
            if (result.Status != FileTransferNative.RenameStatus.Done)
            {
                TryDelete(staging);
                throw result.Status == FileTransferNative.RenameStatus.Exists
                    ? new TransferFailedException(PlatformFailureCode.Conflict)
                    : new TransferFailedException(result.Code, result.Diagnostic);
            }
            _changes.Add(new Change.Created(target, IsDirectory: false));
        }

        private static void Rename(string source, string target)
        {
            var result = FileTransferNative.Rename(source, target);
            if (result.Status == FileTransferNative.RenameStatus.Done) return;
            throw result.Status == FileTransferNative.RenameStatus.Exists
                ? new TransferFailedException(PlatformFailureCode.Conflict)
                : new TransferFailedException(result.Status == FileTransferNative.RenameStatus.CrossDevice ? PlatformFailureCode.Other : result.Code, result.Diagnostic);
        }

        private static void ApplyMetadata(Node source, string target)
        {
            if (OperatingSystem.IsWindows())
            {
                var info = source.Kind == Kind.Directory ? (FileSystemInfo)new DirectoryInfo(target) : new FileInfo(target);
                info.LastWriteTimeUtc = source.LastWriteUtc;
                const FileAttributes kept = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive;
                if ((source.Attributes & kept) != 0) info.Attributes |= source.Attributes & kept;
                return;
            }
            if (source.Kind == Kind.Directory) Directory.SetLastWriteTimeUtc(target, source.LastWriteUtc);
            else File.SetLastWriteTimeUtc(target, source.LastWriteUtc);
            File.SetUnixFileMode(target, source.Mode);
        }

        private async ValueTask<FileConflictAction> ResolveAsync(FileTransferConflict conflict)
        {
            if (_always.TryGetValue(conflict.CanMerge, out var always)) return always;
            if (request.ResolveConflict is not { } handler) throw new TransferFailedException(PlatformFailureCode.Conflict);
            var resolution = await AskAsync(handler, conflict).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The conflict handler returned no resolution.");
            var action = resolution.Action;
            if (!Enum.IsDefined(action)) throw new InvalidOperationException($"The conflict handler returned an unknown action, {action}.");
            if (action == FileConflictAction.Merge && !conflict.CanMerge)
                throw new InvalidOperationException("Merge applies only when both the source and the existing entry are directories.");
            if (resolution.ApplyToAll) _always[conflict.CanMerge] = action;
            return action;
        }

        private async ValueTask<FileConflictResolution?> AskAsync(FileConflictHandler handler, FileTransferConflict conflict)
        {
            if (context is null) return await handler(conflict, cancellationToken).ConfigureAwait(false);
            var answer = new TaskCompletionSource<FileConflictResolution?>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Post(static state =>
            {
                var (handler, conflict, token, answer) = ((FileConflictHandler, FileTransferConflict, CancellationToken, TaskCompletionSource<FileConflictResolution?>))state!;
                _ = Forward(handler(conflict, token), answer);
            }, (handler, conflict, cancellationToken, answer));
            return await answer.Task.ConfigureAwait(false);

            static async Task Forward(ValueTask<FileConflictResolution> pending, TaskCompletionSource<FileConflictResolution?> answer)
            {
                try { answer.TrySetResult(await pending.ConfigureAwait(false)); }
                catch (OperationCanceledException canceled) { answer.TrySetCanceled(canceled.CancellationToken); }
                catch (Exception error) { answer.TrySetException(error); }
            }
        }

        private void Skip(Node source)
        {
            _skipped += source.Items;
            _completedItems += source.Items;
            _completedBytes += source.Bytes;
            Report(force: false);
        }

        private void Complete(Node source, int items, long bytes, bool bytesAlreadyCounted = false)
        {
            _transferred += items;
            _completedItems += items;
            if (!bytesAlreadyCounted)
            {
                _completedBytes += bytes;
                _transferredBytes += bytes;
            }
            _current = source.Path;
            Report(force: false);
        }

        private void Report(bool force)
        {
            if (request.Progress is not { } progress) return;
            long now = engine._time.GetTimestamp();
            if (!force && _lastReport != long.MinValue && engine._time.GetElapsedTime(_lastReport, now) < ReportInterval) return;
            _lastReport = now;
            progress.Report(new FileTransferProgress(_completedItems, _totalItems, _completedBytes, _totalBytes, _current));
        }

        // Removes the sources of a move to another file system, after their item is complete: files and links, then
        // directories that are empty, so a skipped entry keeps its directory.
        private void RemoveCopiedSources()
        {
            foreach (var node in _copiedSources.Where(node => node.Kind != Kind.Directory)) DeleteEntry(new FileInfo(node.Path));
            foreach (var node in _copiedSources.Where(node => node.Kind == Kind.Directory).OrderByDescending(node => node.Path.Length))
                if (!Directory.EnumerateFileSystemEntries(node.Path).Any()) Directory.Delete(node.Path);
        }

        // Puts back what the item in progress changed: removes what it created and moves renamed entries back. Best
        // effort: the transfer already failed, and this must not hide why.
        private void Undo()
        {
            for (int index = _changes.Count - 1; index >= 0; index--)
            {
                try
                {
                    switch (_changes[index])
                    {
                        case Change.Created { IsDirectory: true } created: DeleteTree(created.Path); break;
                        case Change.Created created: TryDelete(created.Path); break;
                        case Change.Renamed renamed: _ = FileTransferNative.Rename(renamed.To, renamed.From); break;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static string Hidden(string name, string purpose) =>
        "." + name + ".runic-" + Guid.NewGuid().ToString("N")[..8] + "." + purpose;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // Removes a tree without following links.
    private static void DeleteTree(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null || !info.Exists) { TryDelete(path); return; }
        foreach (var entry in info.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if (entry is DirectoryInfo directory && directory.LinkTarget is null) DeleteTree(directory.FullName);
            else DeleteEntry(entry);
        }
        DeleteEntry(info);
    }

    // Windows refuses to delete a read-only entry; the transfer itself made or moved it.
    private static void DeleteEntry(FileSystemInfo entry)
    {
        if (OperatingSystem.IsWindows() && entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
        entry.Delete();
    }
}
