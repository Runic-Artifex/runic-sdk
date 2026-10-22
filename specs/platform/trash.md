# Trash

An application moves a file or directory the user deletes to the user's trash
instead of deleting it permanently, so the user can restore it from the file
manager (planning item W310-003). `IDesktopTrash` in Runic.Platform is the
contract; `AddRunicPlatformServices()` registers the provider for the
operating system. .NET has no API for this: `File.Delete` and
`Directory.Delete` delete permanently.

## Delivery matrix — 2026-10-22

| Platform | Delivery state | Validation |
| --- | --- | --- |
| Linux | Implemented: the [freedesktop.org Trash specification 1.0](https://specifications.freedesktop.org/trash-spec/1.0/), home trash and topdir trashes, with `directorysizes`, listing, restoring and emptying, in Runic.Platform.Linux.Portal. | Tests in temporary directories, never the user's trash, for every outcome: home trash, name collisions and stale entries, directories, links, escaping, missing items, refused moves, a directory that contains the trash, and the topdir trashes on a second file system (`/dev/shm`) with sticky, non-sticky and linked trash directories; a batch, `directorysizes` lines and stale entries, listing with original paths, sizes and dates, restoring over a conflict, into a removed parent and from a topdir trash, IDs outside the trash, emptying with a link and an orphaned entry; info files and encoded paths. JIT and NativeAOT, x64 and arm64, in CI. |
| Sandboxed Linux | Implemented: the Trash portal (`org.freedesktop.portal.Trash`) inside Flatpak and Snap. Listing, restoring and emptying are `Unavailable(BackendUnavailable)`. | A fake portal on a private bus: trashed, refused, a link (sent as itself, and refused), a read-only file, no Trash interface, no portal. The real xdg-desktop-portal 1.22 in a Fedora container with a temporary home moves a file, a directory and a symbolic link, JIT and NativeAOT in CI. Inside a real Flatpak or Snap: unverified. |
| Windows | Implemented: `IFileOperation` with `FOFX_RECYCLEONDELETE` and no UI in Runic.Platform.Windows. A progress sink refuses a permanent deletion. Listing, restoring and emptying through the Recycle Bin's shell folder. | Portable tests for error mapping, the sink's refusal and the `$I` record. CI on the windows-latest and windows-11-arm runners' Recycle Bin, JIT and NativeAOT: a file and a directory are recycled and recorded with their original paths, a missing item is `NotFound`, a file on the administrative SMB share, which cannot be recycled, stays; both are listed with their size and deletion time, a restore over an existing file is `Conflict`, the file and the directory, into a removed parent, are restored, and the bin is emptied (on the ephemeral runner only). |
| macOS | Implemented: `-[NSFileManager trashItemAtURL:resultingItemURL:error:]` in Runic.Platform.MacOS. Listing, restoring and emptying are `Unavailable(BackendUnavailable)`. | Portable tests for error mapping. CI on the macos-26 runner's Trash, JIT and NativeAOT: a file, a directory, a symbolic link, a missing item, and list, restore and empty unavailable. A volume without a trash (`NSFeatureUnsupportedError`) is unverified. |

## Contract

```csharp
public interface IDesktopTrash
{
    ValueTask<PlatformResult<PlatformUnit>> MoveToTrashAsync(string path, CancellationToken cancellationToken = default);
    // Default members: a 0.10 implementation keeps working.
    ValueTask<ImmutableArray<PlatformResult<PlatformUnit>>> MoveAllToTrashAsync(IReadOnlyList<string> paths,
        IProgress<DesktopTrashProgress>? progress = null, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<ImmutableArray<DesktopTrashItem>>> ListAsync(CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<PlatformUnit>> RestoreAsync(string itemId, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<PlatformUnit>> EmptyAsync(CancellationToken cancellationToken = default);
}

public readonly record struct DesktopTrashProgress(int Completed, int Total);
public sealed record DesktopTrashItem(string Id, string OriginalPath)
{
    public DateTimeOffset? DeletedAt { get; init; }
    public long? Size { get; init; }
    public bool IsDirectory { get; init; }
}
```

- The path is a fully qualified local path to a file, a directory with its
  contents, or a symbolic link, which is trashed itself and not followed. A
  relative path, NUL, a root directory and, on Windows, a device (`\\?\`,
  `\\.\`) or alternate stream path are an `ArgumentException` before any
  native call. A trailing separator is ignored.
- Paths come from trusted C# application code, never directly from a web
  client.
- The item is never deleted permanently instead: unless the result is
  `Success`, it stays where it was.
- The trash needs no window. `DesktopTrash.Unavailable(reason)` serves hosts
  without a provider; CS-WebUI registers it with `ProviderNotConfigured`.
- Cancellation stops a request that has not moved the item yet; a move in
  progress completes.
- `MoveAllToTrashAsync` checks every path first, then calls
  `MoveToTrashAsync` for each, in order, and reports `(Completed, Total)` after
  each item. A failed item does not stop the others; each keeps its own
  outcome. Cancellation throws after the item in progress, which the last
  progress report counts.
- `ListAsync`, `RestoreAsync` and `EmptyAsync` are available on Windows and,
  outside a sandbox, on Linux. Elsewhere, and in the default implementation,
  they report `Unavailable(BackendUnavailable)` (D036.6).
- A listed item's `Id` is opaque and stable while the item stays in the trash.
  `Size` is reported where the trash records it; a directory is never walked to
  measure it. `RestoreAsync` never replaces what is now at the original path
  (`Failed(Conflict)`), recreates a missing parent directory, and reports an
  item no longer in the trash as `Failed(NotFound)`. `EmptyAsync` deletes
  every listed item permanently without asking: the application asks the user
  first.

### Outcomes

| Situation | Result | Linux | Windows | macOS |
| --- | --- | --- | --- | --- |
| Moved | `Success` | Renamed into a trash's `files/` with its info file | Recycled | Moved into the volume's trash |
| No such item | `Failed(NotFound)` | `ENOENT`, `ENOTDIR` | `0x80070002`, `0x80070003` | `NSFileNoSuchFileError`, `NSFileReadNoSuchFileError`, `ENOENT`, `ENOTDIR` |
| Access refused | `Failed(PermissionDenied)` | `EACCES`, `EPERM`, `EROFS` on the item or its directory; in a sandbox, a file the application cannot open for writing | `E_ACCESSDENIED` | `NSFileReadNoPermissionError`, `NSFileWriteNoPermissionError`, `NSFileWriteVolumeReadOnlyError`, `EACCES`, `EPERM`, `EROFS` |
| In use | `Failed(ResourceBusy)` | `EBUSY`, such as a mount point | Sharing and lock violations | `NSFileLockingError`, `EBUSY` |
| No usable trash for the location | `Unavailable(BackendUnavailable)` | No usable home trash on the item's file system and no usable topdir trash; in a sandbox, no Trash portal or a symbolic link | The shell would delete permanently: a network share, a drive without a Recycle Bin, an item larger than the bin, a policy | `NSFeatureUnsupportedError`, such as on a network volume |
| A directory that contains the trash | `Failed(InvalidData)` | `EINVAL` from the rename | — | — |
| A failed input/output operation, such as a full disk | `Failed(IOError)` with the native code | `EIO`, `ENOSPC`, `EDQUOT` | `ERROR_HANDLE_DISK_FULL`, `ERROR_DISK_FULL`, `ERROR_WRITE_FAULT`, `ERROR_READ_FAULT`, `ERROR_CRC`, `ERROR_IO_DEVICE` | `NSFileWriteOutOfSpaceError`; `EIO`, `ENOSPC`, `EDQUOT` |
| Other native errors | `Failed(Other)` with the native code | `errno`; the portal's result 0 | `HRESULT` | `NSError` domain and code |

Diagnostics carry the native error domain and code only, never a path.

## Linux

Outside a sandbox the provider implements the specification itself, as GIO
and KIO do, and needs no service:

- The home trash is `$XDG_DATA_HOME/Trash` (`~/.local/share/Trash`), created
  with mode 0700 when missing, with `files/` and `info/`.
- For each item the provider first creates `info/<name>.trashinfo`
  exclusively (`O_CREAT | O_EXCL`, mode 0600). That claims the name: another
  implementation trashing an item of the same name at the same time takes the
  next one. The info file is:

  ```ini
  [Trash Info]
  Path=/home/user/Documents/a%20b.txt
  DeletionDate=2026-10-17T14:03:12
  ```

  `Path` is the UTF-8 path, percent-encoded except for unreserved characters
  and `/`, as GIO writes it. Symbolic links in the parent directory are
  resolved; the item itself is not. `DeletionDate` is local time without a
  zone, as the specification requires.
- Then it renames the item to `files/<name>` with
  `renameat2(RENAME_NOREPLACE)`. A stale entry in `files/` without an info
  file is not replaced; the next name is taken. On a file system or C library
  without `RENAME_NOREPLACE`, a check precedes `rename(2)`. A rename never
  falls back to copying, so nothing is deleted permanently. A failed rename
  removes the info file again.
- Names follow GIO: `report.tar.gz`, then `report.2.tar.gz`, `report.3.tar.gz`.
- An item on another file system than the home trash (`EXDEV`) goes to the
  trash at the top of its mount, found in `/proc/self/mountinfo`:
  `$topdir/.Trash/$uid` when `$topdir/.Trash` is a directory with the sticky
  bit and not a link, otherwise `$topdir/.Trash-$uid`, created with mode 0700.
  A topdir trash that is a link or belongs to another user is not used. The
  info file's `Path` is relative to `$topdir`, so it stays valid when a
  removable drive is mounted elsewhere.
- After a directory is trashed, `directorysizes` gets the line `size mtime
  name`: the bytes of the blocks the directory and its contents use (as `du
  -B1`, links not followed), the info file's modification time in seconds and
  the percent-encoded name. The file is rewritten through a temporary file and
  a rename, as the specification asks; a failure leaves it as it was.
- Listing reads the home trash and the topdir trashes this user owns on every
  mount in `/proc/self/mountinfo` (`.Trash/$uid` under a sticky `.Trash` that is
  not a link, and `.Trash-$uid`), creating nothing. Each `info/*.trashinfo` with
  a `[Trash Info]` group and a `Path` is an item if its `files/` entry exists;
  the entry's path is its `Id`. `Path` is percent-decoded and, in a topdir
  trash, may be relative to the top directory; a relative path in the home
  trash is skipped. `DeletionDate` is read as local time. A file's `Size` is its
  size; a directory's comes only from a `directorysizes` entry whose time matches
  its info file.
- Restoring accepts only an `Id` in a known trash's `files/`. It creates the
  missing parents of the original path, renames the entry back with
  `renameat2(RENAME_NOREPLACE)` (`Failed(Conflict)` if anything is there), then
  removes the info file and the `directorysizes` entry.
- Emptying deletes, in every listed trash, each `files/` entry before its info
  file, then entries left without one, then `directorysizes`. A link is deleted
  itself; nothing is followed. The first failure is reported after the rest was
  deleted.
- Inside Flatpak (`/.flatpak-info`) or Snap (`$SNAP`), where the user's trash
  is out of reach, `org.freedesktop.portal.Trash.TrashFile` receives a
  descriptor of the item, opened as GIO opens it: a file read-write without
  following a link, a directory as `O_PATH`. The portal trashes what the
  application can write. A symbolic link goes as an `O_PATH | O_NOFOLLOW`
  descriptor of the link itself: xdg-desktop-portal 1.22 resolves the
  descriptor with `GLNX_CHASE_NOFOLLOW` and trashes the link; a portal that
  refuses it (result 0) reports `Unavailable(BackendUnavailable)` for the link,
  which stays. The portal chooses the entry's name itself, such as
  `report.0.txt`. It offers no listing, restoring or emptying.

## Windows

`IFileOperation` (`CLSID_FileOperation`) on a single-threaded apartment thread
of its own, with `FOF_NO_UI | FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE`, so no
confirmation, error or progress dialog is shown. The shell deletes an item
permanently when it cannot recycle it, and tells its progress sink
beforehand by leaving `TSF_DELETE_RECYCLE_IF_POSSIBLE` out of `PreDeleteItem`.
The provider's sink returns `E_ABORT` then, and the request reports
`Unavailable(BackendUnavailable)` with the item in place. The sink is a static
unmanaged vtable, so it is NativeAOT-safe.

Listing enumerates the Recycle Bin's shell folder (`FOLDERID_RecycleBinFolder`,
`BHID_EnumItems`), which spans the bins of every drive. An item's filesystem
path (`SIGDN_FILESYSPATH`) is its `$R` file or directory and is the item's
`Id`; the `$I` file beside it records the original path, the size (also of a
directory) and the deletion time as a `FILETIME`. Both record versions are
read: version 1 with a fixed 260-character path, version 2 (Windows 10 and
later) with a length-prefixed path. An entry without a readable `$I` file is
not listed. Restoring checks that nothing is at the original path, then runs
the item's own `undelete` verb through its `IContextMenu` with
`CMIC_MASK_FLAG_NO_UI | CMIC_MASK_NOASYNC`, which recreates a missing parent;
the result is `Success` once the original path exists and the `$R` entry is
gone. Emptying is `SHEmptyRecycleBinW` for every drive with
`SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND`; an already empty
bin, which answers `E_UNEXPECTED`, succeeds.

## macOS

`-[NSFileManager trashItemAtURL:resultingItemURL:error:]` on the thread pool,
as Finder's Move to Trash: `~/.Trash` for the startup volume and
`.Trashes/<uid>` on other local volumes. It needs no AppKit event loop. The
resulting location is not returned.

Listing, restoring and emptying report `Unavailable(BackendUnavailable)`:
macOS has no public API for them (Finder's Put Back only), and `~/.Trash` is
protected by privacy consent.

## Out of scope for 1.0

- Returning where the item went, for an application's own undo (D036.6).
- Deleting one trashed item permanently.
- Listing, restoring and emptying inside Flatpak and Snap: the Trash portal
  only moves items to the trash.
- Updating `directorysizes` on Linux, and trashing a symbolic link through the
  Trash portal.
