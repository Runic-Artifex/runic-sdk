# File transfers

An application copies and moves files and directories as a file manager does:
with progress, cancellation and a decision for each conflict (planning item
W310-003). `IDesktopFileTransfers` in Runic.Platform is the contract;
`LocalFileTransfers.Create()` in Runic.Platform.Runtime is the engine every
operating system uses, and `AddRunicPlatformServices()` registers it.

.NET has `File.Copy`, `File.Move` and `Directory.Move`, but no progress, no
cancellation within a file, no directory copy and no conflict decisions, and
`File.Move` silently copies across file systems.

## Delivery matrix — 2026-10-17

| Platform | Delivery state | Validation |
| --- | --- | --- |
| Linux | Implemented: the managed engine with `renameat2(RENAME_NOREPLACE)` and `statx`. | Tests in temporary directories: trees, times, permissions, links, progress, moves within and across file systems (`/dev/shm`), every conflict answer, merges, apply to all, cancellation, undo after a failure, named pipes, the handler's context, validation. JIT in CI. |
| Windows | Implemented: the managed engine with `MoveFileExW`. | The same tests, CI-verified on the windows-latest runner (JIT). Cases that need Unix permissions, named pipes or a second file system do not apply. |
| macOS | Implemented: the managed engine with `renamex_np(RENAME_EXCL)` and `lstat`. | The same tests, CI-verified on the macos-26 runner (JIT). A move across volumes is unverified. |

Directory watching is `RunicPathWatcher` in Runic.Application.Views
(W300-012): inotify on Linux, `ReadDirectoryChangesW` on Windows and FSEvents on
macOS through `FileSystemWatcher`, with polling for network file systems. A
test watches a directory while a transfer copies into it.

## Contract

```csharp
public interface IDesktopFileTransfers
{
    ValueTask<PlatformResult<FileTransferSummary>> CopyAsync(FileTransferRequest request, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<FileTransferSummary>> MoveAsync(FileTransferRequest request, CancellationToken cancellationToken = default);
}

public sealed record FileTransferRequest(ImmutableArray<string> Sources, string DestinationDirectory)
{
    public IProgress<FileTransferProgress>? Progress { get; init; }
    public FileConflictHandler? ResolveConflict { get; init; }
}

public delegate ValueTask<FileConflictResolution> FileConflictHandler(FileTransferConflict conflict, CancellationToken cancellationToken);
```

- Sources are fully qualified paths of files, directories and symbolic links,
  checked as `IDesktopTrash` checks its path. The destination is an existing
  directory; each source goes into it under its own name. No source, a
  relative path, or a destination that is a source or inside one is an
  `ArgumentException`. A missing source or destination is `Failed(NotFound)`.
- Paths come from trusted C# application code, never directly from a web
  client.
- The transfer first reads every source tree, so that progress has totals and a
  destination reached through a link inside a source is never walked.
- Transfers need no window. `DesktopFileTransfers.Unavailable(reason)` serves
  hosts without them; CS-WebUI registers it with `ProviderNotConfigured`, and an
  application can register `LocalFileTransfers.Create()` there instead.

### Copying

- A file is read and written in 1 MiB blocks under a hidden name in the
  destination, `.<name>.runic-<random>.partial`, created with mode 0600 on
  Linux and macOS. Once complete it gets the source's last write time and
  permissions (or Windows attributes) and is renamed to its real name with an
  exclusive rename, which never replaces an entry. A listing or a watcher
  therefore never sees a partial file under a real name.
- A directory is created, its entries are transferred, and then it gets the
  source's last write time and permissions, so a read-only directory can still
  be filled.
- A symbolic link is created with the same target, under a hidden name and
  renamed into place. It is never followed, also not inside a source tree. A
  Windows junction is copied as a symbolic link.
- Named pipes, sockets and devices (Linux and macOS) are skipped and counted in
  `Skipped`; they are never opened.

| Kept | Not kept |
| --- | --- |
| Content; last write time of files and directories | Owner and group; creation and access times |
| Permission bits on Linux and macOS | Access control lists, extended attributes (macOS quarantine and tags, SELinux labels) |
| Read-only, hidden and archive attributes on Windows | Alternate data streams |
| Symbolic links as links; empty directories | macOS resource forks and Finder information |
|  | Hard links, which become separate files; sparse regions, which are written out |

Reflinks (`FICLONE`), `copy_file_range`, `CopyFileEx` and `clonefile` are not
used, so a copy on Btrfs, XFS or APFS takes as long as on any file system.

### Moving

- A source on the destination's file system is renamed with the exclusive
  rename: `renameat2(RENAME_NOREPLACE)` on Linux, `renamex_np(RENAME_EXCL)` on
  macOS, `MoveFileExW` without `MOVEFILE_REPLACE_EXISTING` and
  `MOVEFILE_COPY_ALLOWED` on Windows. Where the file system does not support
  the exclusive form, a check precedes `rename(2)`.
- On another file system (`EXDEV`, `ERROR_NOT_SAME_DEVICE`) the source is
  copied as above, and its files, links and then empty directories are removed
  once the whole top-level item is complete. A skipped entry keeps its source
  and the directories above it.
- Moving an item into the directory it is in does nothing and counts as
  skipped; copying it there keeps both, as `name (2).ext`, without asking.

### Conflicts

When an entry of the destination has a source's name, the transfer asks
`ResolveConflict` with the source's and the existing entry's path, kind, size
and last write time:

| Action | Effect |
| --- | --- |
| `Skip` | The source is not transferred and counts as skipped with its whole tree. |
| `Replace` | The existing entry is renamed aside to a hidden name, the source takes its place, and the aside entry is removed once the top-level item is complete. A file can replace a directory and a directory a file. |
| `KeepBoth` | The source takes the first free name: `report (2).txt`, `report (3).txt`; a directory or a name without an extension gets the number at its end. |
| `Merge` | Both are directories: the source's entries are transferred into the existing directory, each conflict decided in turn. For another conflict the transfer throws `InvalidOperationException`. |
| `Cancel` | The transfer stops with `Failed(UserDismissed)`. |

`ApplyToAll` answers every later conflict of the same kind, between two
directories or not, without asking. Without a handler the transfer stops at
the first conflict with `Failed(Conflict)`, and nothing is replaced. The
handler is called one conflict at a time on the `SynchronizationContext` that
was current when the transfer started, so in a ViewModel command it runs in the
model context and can ask through an interaction; with no context, on the
thread pool.

### Progress

`Progress` receives `FileTransferProgress` with completed and total items
(files, directories and links) and bytes, and the entry in progress: once with
the totals before any work, about ten times a second while blocks are copied,
and once at the end with `CurrentPath` null. Skipped entries count as
completed. Reports come from a worker thread; a `Progress<T>` created in a
command forwards them to its context.

### Stopping

The first error stops the transfer. Items completed before stay done. What the
top-level item in progress did is undone, in reverse order: files, links and
directories it created are removed, entries it renamed move back, and entries
it replaced return from aside. A partial file is removed in any case.
Undoing is best effort: an entry that cannot be removed or moved back stays.

| Situation | Result |
| --- | --- |
| Completed | `Success(FileTransferSummary)` with transferred and skipped items and the bytes transferred |
| A source or the destination is missing | `Failed(NotFound)` |
| Access refused, a read-only file system | `Failed(PermissionDenied)` |
| A file in use (Windows sharing violations) | `Failed(ResourceBusy)` |
| A conflict without a handler, or an entry that appeared while the transfer ran | `Failed(Conflict)` |
| A conflict answered `Cancel` | `Failed(UserDismissed)` |
| A full disk and other I/O errors | `Failed(IoError)` with the `errno` or `HRESULT` |
| Caller cancellation | `OperationCanceledException`, within one block |

## Out of scope for 1.0

- Copy acceleration: reflinks, `copy_file_range`, `CopyFileEx`, `clonefile`.
- Keeping extended attributes, ACLs, owners, alternate data streams, resource
  forks, hard links and sparse files.
- Asking about errors (retry or skip a file that cannot be read) instead of
  stopping; the transfer stops at the first error.
- Native operations with their own UI: `IFileOperation` dialogs on Windows,
  GIO and KIO jobs on Linux, Finder on macOS.
- Per-entry change details from `RunicPathWatcher` (which names were added or
  removed); a watcher reports that a directory changed.
