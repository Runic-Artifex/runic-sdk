# Desktop applications

A file manager or launcher asks the desktop what a file is, which installed
applications open it, which one is the user's default, and what icon to show,
then opens the file with the application the user picks (planning item
W310-002). `IDesktopApplications` in Runic.Platform is the contract: the
desktop's application database. `AddRunicPlatformServices()` registers the
provider for the operating system. It extends the owned file opening of
`IDesktopFileLauncher`, which lets the desktop choose; registering the
application's own types is [opening files and URLs](open-activation.md).

## Delivery matrix — 2026-10-19

| Platform | Delivery state | Validation |
| --- | --- | --- |
| Linux | Implemented in Runic.Platform.Linux.Portal, without GIO: the freedesktop.org [Desktop Entry 1.5](https://specifications.freedesktop.org/desktop-entry/latest/), [Shared MIME-info 0.21](https://specifications.freedesktop.org/shared-mime-info/latest/), [MIME Apps 1.0.1](https://specifications.freedesktop.org/mime-apps/latest/) and [Icon Theme 0.13](https://specifications.freedesktop.org/icon-theme/latest/) specifications. | Fixture trees in temporary directories, in the formats `update-mime-database` and `update-desktop-database` write, never the user's directories, MIME database, defaults, icon themes or session bus: entries, name and content detection, associations at every level, the byte-preserving `mimeapps.list` write, launches checked by a probe executable that records its argument vector, and icon lookup. D-Bus activation against a fake `org.freedesktop.Application` on a private bus. JIT and NativeAOT, locally on x64 and in CI. Against a real desktop: unverified. |
| Sandboxed Linux | The host's applications and defaults are out of reach: application methods report `Unavailable(BackendUnavailable)`; content types and icons come from the sandbox's own data. | Portable tests. Inside a real Flatpak or Snap: unverified. |
| Windows | Implemented in Runic.Platform.Windows: the registry's extension keys and MIME database, `AssocQueryString`, `SHAssocEnumHandlers` and `IAssocHandler::Invoke`, `shell:AppsFolder`, and shell icons. Defaults are the user's to choose: `SetDefaultApplicationAsync` reports `PermissionDenied`. | Portable tests of the type rules, launch grouping and PNG encoding on every OS. In CI on Windows x64 and ARM64, JIT and NativeAOT: the runner's associations read-only, then an extension, MIME type and URL protocol unique to the run, registered under `HKEY_CURRENT_USER\Software\Classes` with the test executable as handler, which records its argument vector; the keys are removed afterwards. |
| macOS | Implemented in Runic.Platform.MacOS: `UTType`, `NSWorkspace` (Launch Services) and the Applications folders. | Portable tests of the type forms, supertype order and bundle scan. In CI on macOS arm64, JIT and NativeAOT: the runner's Launch Services database read-only, then two app bundles unique to the run that declare a type and a URL scheme, registered with Launch Services, launched (their executable records each start), and unregistered afterwards. Unverified: delivery of the opened items to the application (Apple events), and `SetDefaultApplicationAsync`'s outcome, because macOS asks the user to confirm even the test type's default and the runner cannot answer. |
| CS-WebUI | `Unavailable(ProviderNotConfigured)`; register `PortalPlatformProvider.CreateApplications()` before `AddRunicPlatformServices()` to use it. | — |

## Contract

```csharp
public sealed record DesktopContentType(string Id)
{
    public string? Description { get; init; }
    public string? MimeType { get; init; }
    public string? IconName { get; init; }
    public ImmutableArray<string> Parents { get; init; }
    public ImmutableArray<string> Extensions { get; init; }
}
public sealed record DesktopApplicationInfo(string Id, string Name)
{
    public string? Description { get; init; }
    public string? IconName { get; init; }
    public ImmutableArray<string> ContentTypes { get; init; }
    public bool Hidden { get; init; }
}
public sealed record DesktopIconData(string MediaType, ImmutableArray<byte> Data, int Size);
public sealed record DesktopIconRequest
{
    public const int MaximumSize = 4096, MaximumScale = 16;
    public string? IconName { get; }
    public string? ApplicationId { get; }
    public string? ContentType { get; }
    public int Size { get; init; }
    public int Scale { get; init; }
    public static DesktopIconRequest ForIconName(string iconName, int size, int scale = 1);
    public static DesktopIconRequest ForApplication(string applicationId, int size, int scale = 1);
    public static DesktopIconRequest ForContentType(string contentType, int size, int scale = 1);
}
public enum DesktopContentDetection { NameOnly, NameAndContent }
public interface IDesktopApplications
{
    ValueTask<PlatformResult<DesktopContentType>> GetContentTypeAsync(string path, DesktopContentDetection detection = DesktopContentDetection.NameAndContent, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<ImmutableArray<DesktopApplicationInfo>>> GetApplicationsAsync(CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<ImmutableArray<DesktopApplicationInfo>>> GetApplicationsForAsync(string contentType, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<DesktopApplicationInfo?>> GetDefaultApplicationAsync(string contentType, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<PlatformUnit>> SetDefaultApplicationAsync(string contentType, string applicationId, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string applicationId, ImmutableArray<string> pathsOrUris, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<DesktopIconData?>> GetIconAsync(DesktopIconRequest request, CancellationToken cancellationToken = default);
}
public static class DesktopApplications { public static IDesktopApplications Unavailable(PlatformUnavailableReason reason); }
// Runic.Platform.Linux.Portal
public IDesktopApplications PortalApplication.CreateApplications();
public static IDesktopApplications PortalPlatformProvider.CreateApplications(Action<PortalDiagnostic>? diagnosticSink = null);
// Runic.Application.Views.Desktop: the provider for the operating system
public static IDesktopApplications PlatformServices.CreateApplications();
```

- The service is application-scoped and needs no window.
  `DesktopApplications.Unavailable(reason)` serves hosts and platforms without a
  provider.
- Identifiers are the platform's own and are passed back unchanged:

  | | Linux | Windows | macOS |
  | --- | --- | --- | --- |
  | Application | Desktop file ID, such as `org.gnome.TextEditor.desktop` | Path of the executable, such as `C:\Windows\System32\notepad.exe`; a packaged application's AppUserModelID, such as `Microsoft.WindowsCalculator_8wekyb3d8bbwe!App` | Bundle identifier, such as `com.apple.TextEdit` |
  | Content type | MIME type, such as `text/plain`, `inode/directory` or `x-scheme-handler/https` | MIME type when the registry maps it back to the same extension, else the extension, such as `.txt`; `inode/directory`; `x-scheme-handler/https` | Uniform type identifier, such as `public.plain-text`; requests also take a MIME type, `.txt` or `x-scheme-handler/https` |

- Arguments are checked the same way on every platform, before any native call:
  an empty identifier, NUL, an identifier over 4096 characters (a content type
  over 255), more than 1024 launch items, and a launch item that is neither a
  fully qualified path nor an absolute URI are an `ArgumentException`; an
  undefined `DesktopContentDetection` an `ArgumentOutOfRangeException`.
  `DesktopIconRequest` checks its size, scale and name when created.
- Paths, application identifiers and content types come from trusted C# code,
  never directly from a web client. Treat names and descriptions read from the
  desktop as untrusted text when showing them.
- Cancellation before the work starts throws `OperationCanceledException`; a
  started launch or write completes.
- Results are read fresh on every request; nothing watches for changes.

### Outcomes

| Situation | Result |
| --- | --- |
| Done | `Success`; a lookup with no answer is a successful empty list or `null` |
| A missing file, for `NameAndContent` | `Failed(IoError)` with `errno` 2 |
| An unreadable file, or an unwritable `mimeapps.list` | `Failed(PermissionDenied)` |
| A malformed content type, an application that is not installed or not associated with the type, or an item the application cannot take | `Failed(InvalidData)`, nothing changes |
| An icon file over 4 MiB | `Failed(TooLarge)` |
| A terminal application (Linux), or the host's applications from a sandbox | `Unavailable(BackendUnavailable)` |
| `SetDefaultApplicationAsync` on Windows, for an application that opens the type | `Failed(PermissionDenied)`; the diagnostic points to `ms-settings:defaultapps` |
| The user declines a default change macOS asks about | `Failed(UserDismissed)` |
| A failed start or D-Bus activation | `Failed(IoError)` or `Failed(PermissionDenied)` with the `errno` or the D-Bus error name |

Diagnostics carry an error code and remediation, never file contents.

## Linux

The provider reads the files directly, as GLib's GIO does, so it needs no
native library and is NativeAOT-safe. It reads `XDG_DATA_HOME`,
`XDG_DATA_DIRS`, `XDG_CONFIG_HOME`, `XDG_CONFIG_DIRS`, `XDG_CURRENT_DESKTOP`
and `PATH` when created, with the
[Base Directory](https://specifications.freedesktop.org/basedir/latest/)
defaults. The parsed MIME database is reused until one of its files changes;
desktop entries and `mimeapps.list` are read on each request.

### Desktop entries

- Entries are the `*.desktop` files below each data directory's
  `applications`, `XDG_DATA_HOME` first. The ID is the path below
  `applications` with `/` replaced by `-` (`kde4/okular.desktop` is
  `kde4-okular.desktop`); the first directory that has an ID provides it, even
  when that entry is unusable, so a user's copy with `Hidden=true` deletes a
  system entry.
- The key file parser follows the specification: groups, the escapes `\s \n
  \t \r \\`, lists split on unescaped `;`, and localized keys matched in the
  order `lang_COUNTRY@MODIFIER`, `lang_COUNTRY`, `lang@MODIFIER`, `lang`, then
  the unlocalized key, for the application's UI culture (else `LC_ALL`,
  `LC_MESSAGES`, `LANG`).
- An entry is usable when its first group is `[Desktop Entry]`, `Type` is
  `Application`, `Hidden` is not true, it has a `Name` and an `Exec` (or is
  `DBusActivatable`), and its `TryExec` program, if any, is on `PATH` and
  executable.
- `GetApplicationsAsync` lists the usable entries the current desktop shows:
  not `NoDisplay`, and allowed by `OnlyShowIn`/`NotShowIn` against
  `XDG_CURRENT_DESKTOP`. Other usable entries remain handlers and appear in
  `GetApplicationsForAsync` with `Hidden` set.
- `Description` is `Comment`, else `GenericName`; `IconName` is the `Icon` key,
  a theme name or an absolute path; `ContentTypes` are the `MimeType` key's
  types with aliases resolved.

### Content types

The Shared MIME-info database in each data directory's `mime`, as
`update-mime-database` writes it: `globs2` (or the older `globs`), `magic`,
`aliases`, `subclasses`, `icons`, `generic-icons` and the per-type XML files.
Directories load from the lowest precedence to `XDG_DATA_HOME`, so
`__NOGLOBS__` and `__NOMAGIC__` in a later directory replace earlier
definitions.

- **Names.** A literal pattern (`Makefile`) wins over every other; then the
  case-sensitive (`cs`) suffix patterns, else the case-insensitive ones, and the
  other glob patterns (`fnmatch` without flags); then the highest weight, then
  the longest pattern. Several types left mean the name is ambiguous.
- **`NameOnly`** never touches the file: the first type from the name, else
  `application/octet-stream`.
- **`NameAndContent`** follows the specification's recommended order. A
  directory, character or block device, FIFO or socket is its `inode/` type
  (`statx`, following links; a dangling link is `inode/symlink`). An
  unambiguous name decides without reading the file. Otherwise the provider
  reads as many leading bytes as the magic rules examine and takes the highest
  priority magic match (nested matchlets, masks, word sizes and ranges, with
  host-order values swapped). Among several name candidates it prefers one that
  equals or is a subclass of the magic result, else the strongest name match.
  Without a name match, the magic result, else `text/plain` when the first 128
  bytes have no control characters, else `application/octet-stream`. Content
  alone never makes a file `application/x-desktop`, as in GLib, because a
  launcher would run it.
- **Details.** The canonical type (aliases resolved) is the `Id` and
  `MimeType`. `Parents` are the `subclasses`, nearest first, with `text/plain`
  for `text/*` types and `application/octet-stream` last for every type that is
  a byte stream (not `inode/`, `x-scheme-handler/` or `x-content/`).
  `Extensions` come from the type's `*.ext` patterns, by weight.
  `Description` is the localized `<comment>` of `<media>/<subtype>.xml` in the
  first directory that has it. `IconName` is from `icons`, else
  `<media>-<subtype>`.

### Associations and defaults

The MIME Apps specification's files, in its order: `XDG_CONFIG_HOME`, then
`XDG_CONFIG_DIRS`, then `XDG_DATA_HOME/applications` and each
`XDG_DATA_DIRS/applications`. In each directory the desktop-specific
`<desktop>-mimeapps.list` files, for each name in `XDG_CURRENT_DESKTOP`
lowercased, come before `mimeapps.list`.

- **Associations for a type**: going through the directories in order,
  `[Added Associations]` entries, then the entries of that data directory whose
  `MimeType` lists the type (in `mimeinfo.cache` order, then by ID), skipping
  entries named in `[Removed Associations]` of the same or an earlier directory
  and entries shadowed by a data directory of higher precedence. Keys and
  declared types are compared after resolving aliases.
- **`GetApplicationsForAsync`**: the default first, then the associations of the
  type and of each of its `Parents`, so a `text/x-csrc` file also lists text
  editors and, last, the applications for `application/octet-stream`.
- **`GetDefaultApplicationAsync`**: for the type and then each explicit parent
  and `text/plain`, the first ID in a `[Default Applications]` list, in the
  file order above, that is usable and associated with the type; else the
  type's most preferred association. The implicit `application/octet-stream`
  parent is not inherited, so an unknown format has no default rather than a
  hex editor's.
- **`SetDefaultApplicationAsync`** checks that the ID is a usable entry
  associated with the type, directly or through a parent, then writes only
  `$XDG_CONFIG_HOME/mimeapps.list`. It sets the type's key in
  `[Default Applications]` to `<id>;`: every key of that group naming the type,
  an alias or a different letter case included, gets the new value; without
  one, a key is added after the group's last key, or the group at the end of
  the file. Every other byte stays as it was, comments, blank lines, line
  endings, other groups and their escapes included. The file is written to a
  temporary file in the same directory, with the old file's mode, and renamed
  over it, so a reader never sees half of it. A symbolic link, as dotfile
  managers create, is followed and keeps pointing at its target. It changes the
  default only, not the associations.

### Launch

- The application ID must name a usable desktop entry in the data directories.
  It is never a path or a command; `../`, `/` and IDs without `.desktop` are
  `Failed(InvalidData)`.
- `Terminal=true` is `Unavailable(BackendUnavailable)`.
- `DBusActivatable=true` calls
  [`org.freedesktop.Application`](https://specifications.freedesktop.org/desktop-entry/latest/dbus.html)
  on the session bus: the bus name is the ID without `.desktop`, the path that
  name with `.` as `/` and `-` as `_`. No items call `Activate`, items call
  `Open` with each one as a URI. Platform data is empty. A D-Bus error is
  `Failed(IoError)`, or `PermissionDenied` for an access error, naming the D-Bus
  error. Without a reachable session bus the `Exec` key starts the
  application; without one either, the launch is
  `Unavailable(BackendUnavailable)`.
- Otherwise the `Exec` key is split by the specification's quoting rules and
  started directly with an argument list, never through a shell. Field codes
  expand into whole arguments: `%f` and `%u` take one item, so several items
  start one process each; `%F` and `%U` pass every item as separate arguments.
  `%f`/`%F` pass local paths and accept `file:` URIs as paths; another URI for
  them is `Failed(InvalidData)`. `%u`/`%U` pass URIs, a path as a `file:` URI.
  `%i` becomes `--icon <Icon>`, `%c` the localized name, `%k` the entry's
  path, `%%` a `%`; deprecated (`%d %D %n %N %v %m`) and unknown codes are
  removed. A field code inside quotes, which the specification leaves
  undefined, expands as if unquoted. Without a file code, items are passed as
  `%f` would pass them, as GLib does.
- The program is the first argument: an absolute path or a name searched on
  `PATH`, which must be executable, else `Failed(IoError)` with `errno` 2. The
  working directory is the `Path` key, which must exist.
- Success means the processes started, or the activation call was accepted.
  Startup notification and activation tokens are not sent.

### Icons

- The theme is the `icon-theme` of `org.gnome.desktop.interface` from the
  Settings portal, read once every few seconds; else `[Icons] Theme` in KDE's
  `kdeglobals` in the config directories; else `hicolor`.
- The theme's `index.theme` (`Directories`, `ScaledDirectories`, `Size`,
  `Scale`, `Type` `Fixed`/`Scalable`/`Threshold`, `MinSize`, `MaxSize`,
  `Threshold`, `Inherits`) is read from the first base directory that has it:
  `~/.icons`, then each data directory's `icons`. Every base directory's copy of
  the theme is searched.
- The specification's lookup: the theme, then the themes it inherits depth
  first, then `hicolor`; in each, a directory that matches the size and scale,
  else the closest by its `DirectorySizeDistance`; then unthemed icons in the
  base directories and `/usr/share/pixmaps`. A name that is not found is tried
  again without its last `-` part (`text-x-csrc`, `text-x`, `text`).
- `ForApplication` uses the entry's `Icon` key: an absolute PNG or SVG path is
  read directly; a name has a `.png`, `.svg` or `.xpm` suffix removed.
  `ForContentType` tries the type's specific icon (from `icons`, else
  `<media>-<subtype>`), then its generic icon (from `generic-icons`, else
  `<media>-x-generic`), as GLib does.
- PNG and SVG files are returned as bytes; XPM is not. `Size` is the
  directory's nominal size, the requested size for a scalable icon, or an
  unthemed PNG's width.

### Sandboxes

Inside Flatpak (`/.flatpak-info`) or Snap (`$SNAP`) the host's desktop entries
and `mimeapps.list` are out of reach, so the application methods report
`Unavailable(BackendUnavailable)` with the `desktop-applications-sandboxed`
diagnostic. Open files and URIs there with `IDesktopFileLauncher` or
`OpenUriAsync`, which ask the OpenURI portal and its application chooser.
Content types and icons come from the sandbox's runtime.

## Windows

`WindowsPlatformProvider.CreateApplications()` reads the shell's association
database. Each request runs on a single-threaded apartment of its own, as the
Recycle Bin does, and releases every COM object before it returns; the COM
interfaces are CsWin32's raw vtables, so the provider is NativeAOT-safe
(`docs/interop-inventory.md` in the package).

### Content types

- **Names.** The extension's key under `HKEY_CLASSES_ROOT`, which merges the
  user's classes over the machine's. An extension the registry knows is
  identified by its `Content Type` when `MIME\Database\Content Type\<type>`
  maps that type back to the same extension, so an ID always stands for one
  extension's associations (`.htm` is `text/html`, while `.html`, which the
  database does not name, stays `.html` with `MimeType` `text/html`); else by
  the extension, lowercased. `PerceivedType` `text`, or a `text/` MIME type,
  gives the parent `text/plain`. `Extensions` is the extension; `Description`
  is the shell's friendly type name (`ASSOCSTR_FRIENDLYDOCNAME`). Windows has
  no themed icon names, so `IconName` is `null`.
- **`NameOnly`** never touches the file: an extension the registry does not
  know, or no extension, is `application/octet-stream`.
- **`NameAndContent`** reads the file's attributes: a directory is
  `inode/directory`; a known extension decides; otherwise the first 256 bytes
  go to urlmon's `FindMimeFromData` (with `FMFD_RETURNUPDATEDIMGMIMES`), the
  sniffing Windows itself uses, which recognizes images, HTML, XML, PDF,
  archives, text and binary data.
- **Requests** take an extension (`.txt`), a MIME type (the database's
  `Extension` for it; one without an extension has no applications),
  `x-scheme-handler/<scheme>` for a URL protocol and `inode/directory` for the
  `Directory` class.

### Applications and defaults

- **`GetApplicationsForAsync`**: the recommended handlers
  `SHAssocEnumHandlers` (`ASSOC_FILTER_RECOMMENDED`) lists for the extension, or
  `SHAssocEnumHandlersForProtocolByApplication` for a protocol, as the shell's
  Open With lists them. Each handler's `IAssocHandler::GetName` is the ID: the
  path of a desktop application's executable, or a packaged application's
  AppUserModelID (`Microsoft.WindowsNotepad_8wekyb3d8bbwe!App` on Windows 11),
  passed back unchanged; `GetUIName` is the name. The default comes first.
- **`GetDefaultApplicationAsync`**: `AssocQueryString` with
  `ASSOCSTR_EXECUTABLE`, else `ASSOCSTR_APPID` for a packaged application, and
  `ASSOCSTR_FRIENDLYAPPNAME`. It honours the user's choice
  (`UserChoice`) and, for a protocol, `ASSOCF_IS_PROTOCOL`.
  `ASSOCF_INIT_IGNOREUNKNOWN` makes an unknown type `null` rather than the
  Open With dialog.
- **`SetDefaultApplicationAsync`**: Windows 8 and later protect the user's
  choice with a hash that programs cannot write, and Settings resets defaults
  written another way. The provider checks that the application opens the type
  (else `InvalidData`) and reports `Failed(PermissionDenied)`; the diagnostic
  (`HRESULT 0x80070005`) points to `ms-settings:defaultapps`, which an
  application can open with `OpenUriAsync` for the user to choose.
- **`GetApplicationsAsync`**: the Start menu's applications, the children of
  `shell:AppsFolder`. A desktop application's ID is the executable its
  shortcut targets (`System.Link.TargetParsingPath`), as the handlers' IDs
  are; a packaged application's is its AppUserModelID. Shortcuts to documents
  and web pages are left out. `Description` and `ContentTypes` are empty.

### Launch

- **Items** group by what opens them: a path by its extension, a directory by
  the `Directory` class, a `file:` URI as its path, any other URI by its
  scheme. Every group must list the application among its handlers
  (`ASSOC_FILTER_NONE`) before anything starts, so an executable that does not
  open a type, such as `cmd.exe` for a document, is `Failed(InvalidData)`, as
  is a file without an extension.
- Each group goes to the handler's `IAssocHandler::Invoke` with a data object
  over the parsed items (`SHParseDisplayName`,
  `SHCreateShellItemArrayFromIDLists`, `BHID_DataObject`), as Explorer's Open
  With does. The shell expands the registered command, quoting each item as one
  argument; no command line passes through the provider or a command shell.
  The shell starts documents asynchronously, so the request's apartment holds a
  thread reference (`SHCreateThreadRef`, `SHSetThreadRef`) and pumps messages
  until the shell releases it, for at most 30 seconds.
- **Without items** the application must be in `shell:AppsFolder`; it starts
  as the Start menu starts it (`ShellExecute` of
  `shell:AppsFolder\<AppUserModelID>`). Other IDs are `Failed(InvalidData)`.
- A failure is `Failed(IoError)`, or `PermissionDenied` for
  `E_ACCESSDENIED`, with the HRESULT.

### Icons

- `ForContentType`: the type's `DefaultIcon` (`ASSOCSTR_DEFAULTICON`), with
  environment variables expanded, through `SHDefExtractIcon` at the requested
  size. A type whose `DefaultIcon` is not an icon resource, such as one a
  packaged application opens, or `%1` (an icon per file), gets the icon
  Explorer shows for a file of its extension, through
  `IShellItemImageFactory` on a simple ID list (`SHSimpleIDListFromPath`) of a
  name that need not exist.
- `ForApplication`: an executable's first icon through `SHDefExtractIcon`; a
  packaged application's tile through `IShellItemImageFactory`
  (`SIIGBF_ICONONLY`) on its `shell:AppsFolder` item.
- `ForIconName` is a successful `null`: Windows has no icon themes.
- WIC encodes the icon as PNG, with the thumbnails' encoder: an `HICON`
  through `CreateBitmapFromHICON`, which takes its alpha channel or its mask,
  and the image factory's premultiplied bitmap converted to straight alpha. The
  size is `Size × Scale`, at most 256 pixels, the largest icon Windows stores.

## macOS

`MacOSPlatformProvider.CreateApplications()` uses Launch Services through
`NSWorkspace` and uniform type identifiers through `UTType`
(UniformTypeIdentifiers), with `objc_msgSend` and the block helper the other
macOS services use, so it is NativeAOT-safe. Both are thread-safe: requests run
on the thread pool and need no window or AppKit event loop. Every API used is in
macOS 12 or earlier; Runic supports macOS 15 and later.

### Content types

- A type's ID is its uniform type identifier. `Description` is
  `localizedDescription`, `MimeType` `preferredMIMEType`, `Extensions`
  `preferredFilenameExtension` and then the type's other filename-extension
  tags, and `Parents` its `supertypes`, nearest first (a supertype that
  conforms to more of the others is nearer). `IconName` is `null`.
- **`NameOnly`**: `typeWithFilenameExtension:`. **`NameAndContent`**: the
  file's `NSURLContentTypeKey` after resolving symbolic links, which also
  knows folders (`public.folder`), packages and bundles. macOS does not sniff
  content. A type the system declares nowhere (a dynamic `dyn.` type) is
  `public.data`.
- **Requests** take a UTI, a MIME type (`typeWithMIMEType:`), an extension
  (`.txt`, `typeWithFilenameExtension:`) or `x-scheme-handler/<scheme>`. A
  well-formed identifier the system does not declare has no applications.

### Applications and defaults

- **`GetApplicationsForAsync`**: `URLsForApplicationsToOpenContentType:`, or
  `URLsForApplicationsToOpenURL:` with `<scheme>:` for a scheme, with
  `URLForApplicationToOpenContentType:` (or `URLForApplicationToOpenURL:`)
  first. Each application is its bundle: the ID is `bundleIdentifier`, the name
  the localized `CFBundleDisplayName`, else `CFBundleName`, and
  `ContentTypes` its `CFBundleDocumentTypes` `LSItemContentTypes` and its
  `CFBundleURLTypes` schemes as `x-scheme-handler/<scheme>`. A bundle without
  an identifier is left out; copies of one application are listed once.
- **`GetApplicationsAsync`**: the `.app` bundles in `/Applications`,
  `/System/Applications` and `~/Applications` and their subfolders (two levels,
  such as `Utilities`), not inside other bundles, as Launchpad shows them; the
  first copy of a bundle identifier wins.
- **`SetDefaultApplicationAsync`** checks that the application opens the type
  (else `InvalidData`), then calls
  `setDefaultApplicationAtURL:toOpenContentType:completionHandler:`, or
  `toOpenURLsWithScheme:` for a scheme. macOS may ask the user to confirm, as it
  does for a web browser; the request waits for the answer, and a refusal
  (`NSUserCancelledError`, `userCanceledErr`) is `Failed(UserDismissed)`.

### Launch

- The ID must name an application Launch Services knows
  (`URLForApplicationWithBundleIdentifier:`), never a path; else
  `Failed(InvalidData)`. A URI other than `file:` must have a scheme the
  application opens (`URLsForApplicationsToOpenURL:`); paths go to any
  application, as Finder's Open With allows.
- `openURLs:withApplicationAtURL:configuration:completionHandler:` with the
  items as `NSURL`s, or `openApplicationAtURL:configuration:completionHandler:`
  without items; Launch Services starts the application and sends it the items
  as an open-documents or get-URL Apple event. Success is the completion
  without an error; an error is `Failed(IoError)`, or `PermissionDenied` for a
  permission error, with the `NSError` domain and code.

### Icons

- `ForApplication`: `iconForFile:` of the application's bundle.
  `ForContentType`: `iconForContentType:`; a URL scheme has none.
  `ForIconName` is a successful `null`: macOS has no icon themes.
- The image is drawn at `Size × Scale` pixels, at most 1024, into an sRGB
  bitmap and encoded with `NSBitmapImageRep` as PNG.

## Out of scope for 1.0

- Starting `Terminal=true` applications, which needs the desktop's terminal.
- Startup notification and XDG activation tokens for launched applications.
- Desktop actions (`[Desktop Action …]`).
- Writing MIME definitions or desktop entries at run time.
- Host applications from Flatpak beyond the OpenURI portal.
- Rasterizing SVG icons.
- Watching the databases for changes.
- Windows: changing defaults (Windows reserves it for the user); applications
  outside the Start menu in `GetApplicationsAsync`; starting an application
  without items unless the Start menu lists it; declared types
  (`ContentTypes`) of Windows applications.
- macOS: applications outside the three Applications folders in
  `GetApplicationsAsync` (they still appear as handlers); content sniffing.
