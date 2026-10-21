# Runic.Platform

Host-independent native-service contracts for Runic applications.

Preview API targeting .NET 10. Native providers are explicitly selected; this package has no Desktop or ASP.NET Core dependency.
Runic.Application.Views.Desktop selects one for each window with `services.AddRunicPlatformServices()`.

Services return `PlatformResult<T>`: `Success`, `Unavailable` with a
`PlatformUnavailableReason`, or `Failed` with a `PlatformFailureCode`. Operations
without a value return `PlatformResult<PlatformUnit>`. The type names carry the
`Platform` prefix so they do not collide with `System.Reactive.Unit` and similar
names when both namespaces are imported.

Branch on the failure code. The set of codes is open: a minor release may add one, so
give a `switch` a default arm. `Unknown` (0) is only the default value and is never
reported; `Other` is a failure no other code describes, such as an unclassified native
error. When a provider observed a native error, the `Diagnostic` of `PlatformResult<T>.Failed`,
`PickerResult<T>.Failed`, `FileCommitResult.NotCommitted` and `FileCommitResult.CommitUnknown`
carries a `PlatformDiagnostic` with its domain (`HRESULT`, `Win32`, `errno`, an
`NSError`/`GError` domain or a D-Bus error name), numeric code and, where the operating system's error table describes the code, its
message. It is for logs and support, not display. Free-form text from native services
(GError and D-Bus messages, NSError descriptions) is left out, so a diagnostic contains
no paths.

These services report what happens on the desktop as results and throw only for a
caller mistake (`ArgumentException`), caller cancellation (`OperationCanceledException`)
and use after disposal. Runic's administration APIs and the Desktop window host do the
opposite and throw exceptions that keep the native error: they are called for one
operation at a time, where a failure ends the operation, while a host capability is
asked for by an application that must carry on when the desktop cannot help.

The result unions `PlatformResult<T>`, `PickerResult<T>` and `FileCommitResult` are
closed: they keep their cases for the major version. Each enum says in its
documentation whether it is open or closed.

`IFileDialogs.OpenDirectoryAsync(new OpenDirectoryOptions())` returns a
`PickerResult<IDirectoryLease>` for one existing local directory. Keep the selected
lease alive while C# filesystem operations use `LocalPath`; it retains native
access until disposal or presentation close. The path preserves the selected
identity, including Unicode and escaped filename characters. Native paths that
cannot be represented exactly in C# are refused. Leases and native grants remain
in C#; expose only application DTOs to a frontend.

Presentation close releases every lease the presentation still owns; on macOS this
ends security-scoped access, and `LocalPath` throws once closing starts. Native
owner closure alone does not release leases. When application work that uses a
lease can outlive the window, such as recovery owned by an `AcceptedWorkScope`,
drain that work before the window closes, or dispose the lease at the end of the
work itself:

```csharp
await acceptedWork.DisposeAsync(); // Work using lease.LocalPath has finished; closing the window releases the rest.
```

User dismissal returns `Dismissed`, caller cancellation throws
`OperationCanceledException`, and presentation shutdown returns
`Unavailable(OwnerClosed)`. The `platform.directories.open` capability reports
whether the window's dialogs can select a directory. A file-dialog provider
(`IPickerBackend` in Runic.Platform.Runtime) that implements `OpenDirectoryAsync`
must also return `true` from `SupportsDirectorySelection`; the host always calls the
provider's method and throws `InvalidOperationException` if an undeclared provider
returns a result other than `Unavailable`.

`PickerResult<T>.Selected` and `PlatformResult<T>.Success` deconstruct, so a
caller can match and take the value in one pattern:

```csharp
if (await files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Selected(var lease))
{
    await using (lease) { /* read */ }
}
```

## File dialog options

`OpenFileOptions` and `SaveFileOptions` take `Filters` and `InitialDirectory`,
`OpenDirectoryOptions` takes `InitialDirectory`, and `SaveFileOptions` keeps its
`SuggestedName`. `OpenFilesAsync` selects several files at once:

```csharp
var images = new FileDialogFilter("Images", ["png", "jpg"]);
var options = new OpenFileOptions
{
    Filters = [images, new FileDialogFilter("All files", ["*"])],
    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
};
if (await files.OpenFilesAsync(options) is PickerResult<IReadFileLeaseList>.Selected(var leases))
{
    await using (leases)
        foreach (var lease in leases) { /* read each file */ }
}
```

A `FileDialogFilter` names a file type and lists its extensions without the dot,
such as `png` or `tar.gz`, or `*` for every file. The first filter is selected
when the dialog opens; no filters offer every file. Extensions match without
regard to case on every platform. Filters and the initial folder are hints to the
dialog: a folder that does not exist is ignored, and the user can still choose or
type a name that matches no filter on some platforms. Runic does not reject such
a selection or add an extension to a saved name, so check the content you read.
The options check their values when they are set and throw `ArgumentException`
for an empty or wildcard extension, a relative folder or a `null` filter.

`OpenFilesAsync` opens every selected file before it returns: if one cannot be
opened, all are released and the result is `Failed`. Each lease holds its file
open until it, the list or the presentation is disposed. A provider without
multiple selection returns `Unavailable(BackendUnavailable)`, and the
`platform.files.openMultiple` capability reports the same.

| Option | Windows | macOS | Linux portal (GTK 3 and GTK 4) | Linux GTK 3 native chooser |
| --- | --- | --- | --- | --- |
| Filters | File type list (`SetFileTypes`) | The union of every filter restricts the panel (`allowedContentTypes`); no filter pop-up | FileChooser `filters` and `current_filter` | File type list |
| Multiple selection | `FOS_ALLOWMULTISELECT` | `allowsMultipleSelection` | FileChooser `multiple` | `select-multiple` |
| Initial folder | `SetFolder`, which overrides the folder Windows remembers | `directoryURL` | FileChooser `current_folder`; ignored when opening by portals before FileChooser version 4 | `current-folder` |
| Suggested name | `SetFileName` | `nameFieldStringValue` | FileChooser `current_name` | `current-name` |

On macOS a save panel with filters may add the first allowed extension to a
name the user types without one. Applications that need the user to pick between
filters on macOS can offer the choice in their own interface for now.

## Window services

`IWindowPlatformServices` groups the file dialogs, text clipboard and file
launcher of one window, and reports their capabilities together under
`WindowPlatformServices.CapabilityIds`. Host adapters register it, and its
parts, per window scope through `services.AddRunicPlatformServices()`, so a
ViewModel can take `IFileDialogs`, `ITextClipboard`, `IDesktopFileLauncher` or
`IWindowPlatformServices` in its constructor. Runic.Application.Views.Desktop binds
them to the native window when it opens. A host without a native owner
registers `WindowPlatformServices.Unavailable(reason)`, whose operations and
capabilities all report that reason. Tests can use the same instance.

The capability identifiers are constants of `PlatformCapabilityIds`, so a typo
does not compile:

```csharp
var snapshot = services.GetSnapshot();
bool canSave = snapshot.Statuses[PlatformCapabilityIds.FilesSave] is CapabilityStatus.Available;
```

### Opening links

`IDesktopFileLauncher.LaunchUriAsync(uri)` opens a web or mail link in the
user's default browser or mail client, or a Windows Settings page, and reports
the outcome as a `PlatformResult` (capability
`PlatformCapabilityIds.UrisLaunch`):

```csharp
var result = await launcher.LaunchUriAsync(new Uri("https://example.org/help"));
```

It accepts only absolute `http`, `https`, `mailto` and `ms-settings` URIs. A relative URI, a
path (an absolute path parses as a `file:` URI, a UNC path too) and every other
scheme throw `ArgumentException` before anything native runs, so a link never
starts a program; open local files with `LaunchAsync`. A `mailto` URI that names
an attachment (a header field `attach`, `attachment` or another name beginning
with `attach`, in any case or escaping) throws too: some mail clients attach that
file without the user choosing it. The URI comes from trusted application code:
validate links a web page supplies before passing them on.

`LaunchAsync(path)` opens a local file in its default application. On Windows and
macOS, `DesktopFileOperation.Open` returns `Failed(PermissionDenied)` instead of
running programs, scripts, installers, shortcuts and disk images; Linux applies
the desktop portal's policy. The block lists are best effort, so do not `Open`
files from untrusted sources, such as downloads or names a page supplies: use
`DesktopFileOperation.Reveal` or `DesktopFileOperation.ChooseApplication` instead.

| Platform | Mechanism | Outcomes |
| --- | --- | --- |
| Linux (GTK 3 and GTK 4, also in Flatpak and Snap) | The OpenURI desktop portal | The portal may let the user choose an application; cancelling is `Failed(UserDismissed)` |
| Windows | `ShellExecuteEx` with the URI and the shell's URL association (the user's default), with zone checks | Without a handler, for example no mail client, Windows 11 offers to find an application and reports success; older shells report `Unavailable(BackendUnavailable)` |
| macOS | `NSWorkspace openURL:configuration:completionHandler:` | No application for the scheme: `Failed(Other)` with the `NSError` domain and code (macOS 26: `NSCocoaErrorDomain 256`) |

Success means the desktop accepted the URI, not that an application showed it.

`ms-settings:` URIs, such as `ms-settings:display` or `ms-settings:defaultapps`,
open the Windows Settings app through the same shell call. The URI must be
exactly `ms-settings:<page>`, where the page is 1 to 64 letters, digits or
hyphens (compared lower-case), with no path, query, fragment or percent-escapes.
Pages ending in `-action`, such as `windowsupdate-action`, start an action
instead of showing a page and are refused. Other values throw
`ArgumentException` on every platform. Linux and macOS accept a valid page and
return `Unavailable(BackendUnavailable)` without a native call, so
cross-platform code can offer a Settings link on Windows and handle the result
elsewhere:

```csharp
var result = await launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
if (result is PlatformResult<PlatformUnit>.Unavailable)
    ShowManualInstructions();
```

`IDesktopSettings` is application-scoped, so `AddRunicPlatformServices()`
registers one singleton for it. A host or platform without a provider
registers `DesktopSettings.Unavailable(reason)`: reads return
`Unavailable(reason)`, and a watch emits that value once and then waits.
`IDesktopNotifications` is registered the same way; without a provider it is
`DesktopNotifications.Unavailable(reason)`, whose requests return
`Unavailable(reason)` and which never raises `Activated`.
`IDesktopStatusIcon`, `IDesktopLoginStartup` and `IDesktopInhibition` are
application singletons too; without a provider they are
`DesktopStatusIcon.Unavailable(reason)`, `DesktopLoginStartup.Unavailable(reason)`
and `DesktopInhibition.Unavailable(reason)`. Unavailable inhibition supports no
effects, still validates the effects and reason as a provider does, and returns
`Unavailable(reason)` from every acquisition.

### Hosts without a native owner

One `services.AddRunicPlatformServices()` call registers every service above on
both hosts, so a ViewModel that takes any of them resolves on Runic Desktop and on
CS-WebUI. Window services need the window's native owner; application services
need none. A window has no native owner when it is shown in an installed browser
after an embedded-window fallback, served under `RUNIC_APPLICATION_SERVE_ONLY=1`,
or opened on CS-WebUI; a ViewModel resolved outside a window's scope has none
either.

| Service | Desktop window with a native owner | Desktop window without one, or outside a window scope | CS-WebUI |
| --- | --- | --- | --- |
| `IWindowPlatformServices`, `IFileDialogs`, `ITextClipboard`, `IDesktopFileLauncher` | Native provider; `OwnerClosed` after the window closes | `Unavailable(OwnerUnavailable)` | `Unavailable(OwnerUnavailable)` |
| Announcements and `DesktopPlatformServices.PresentAsync` | Native window | `Unavailable(OwnerUnavailable)` | `Unavailable(OwnerUnavailable)` |
| Window state, `DesktopPlatformServices.GetWindowState` | The window's bounds and state, also the last ones after it closes | `Unavailable(OwnerUnavailable)` | `Unavailable(OwnerUnavailable)` |
| Dropped files, `IRunicFileDropTarget` | Native paths, once per drop | None | None |
| Menu bar commands, `IRunicMenuCommandSource` | The macOS menu bar, while the window is key | None | None |
| `IDesktopSettings`, `IDesktopNotifications`, `IDesktopStatusIcon`, `IDesktopLoginStartup`, `IDesktopInhibition`, `IDesktopSecretStore`, `IDesktopTrash`, `IDesktopFileTransfers`, `IDesktopVolumes`, `IDesktopApplications`, `IDesktopThumbnails` | Operating system provider | Operating system provider, the same instance | `Unavailable(ProviderNotConfigured)` |

On Runic Desktop the application services behave the same with or without a
native owner. The macOS settings, notifications and status icon need the AppKit
event loop that the Desktop host runs, as their providers describe. Linux inhibition sends no parent window, so the desktop does not tie it to a
window. CS-WebUI selects no native provider; register one before calling
`AddRunicPlatformServices()` to use an application service there, for example
`WindowsPlatformProvider.CreateInhibition()` or
`new PortalApplication("org.example.Notes").CreateInhibition()`.
Existing registrations are kept on both hosts.

The application composes `IDesktopPrivilegedActions` itself, because it needs
declared actions and a provider package, and claims `ApplicationInstance` before
it starts a host.

## Menus

Status icon menus (`DesktopStatusIconState.Menu`) and the Runic Desktop menu bar
(`DesktopHostOptions.MenuBar`) share one model:

| Type | Is |
| --- | --- |
| `DesktopMenuEntry` | The base of every entry. The set is open: a minor release may add entry kinds, so give a `switch` over entries a default arm. Code that walks a menu skips entries it does not know. |
| `DesktopMenuItem(Title, CommandId)` | An item that runs a command, with `IsEnabled`, `IsChecked` (`null` for a plain item) and `Shortcut` |
| `DesktopMenuSeparator` | A line between groups of entries |
| `DesktopMenu(Title, Items)` | A submenu, or a menu of the menu bar, with `IsEnabled` and the menu bar's `Role` |
| `DesktopShortcut`, `DesktopShortcutModifiers` | A key and its modifiers, written as `Primary+Shift+S` |

```csharp
ImmutableArray<DesktopMenuEntry> menu =
[
    new DesktopMenuItem(Title: "Open window", CommandId: "open"),
    new DesktopMenuItem(Title: "Start at login", CommandId: "login") { IsChecked = startsAtLogin },
    new DesktopMenuSeparator(),
    new DesktopMenu("More", [new DesktopMenuItem(Title: "About", CommandId: "about")]),
    new DesktopMenuItem(Title: "Quit", CommandId: "quit"),
];
```

The title comes first. A title is literal user-visible text: not blank, at most
128 characters and without NUL. It has no mnemonic markers: providers show `&`
and `_` as written. A command id is an application-owned routing key, never a
command line: 1 to 64 ASCII letters, digits, dots, underscores or hyphens. Ids
starting with `runic.`, in any letter case, are reserved for Runic's standard
commands. Each surface checks these rules when it receives a menu and throws an
`ArgumentException` that names the id or title and the rule.

| | Status icon menu | Menu bar (Runic Desktop) |
| --- | --- | --- |
| Command ids | Unique in the menu | May repeat |
| Limits | Four nested submenus, 256 entries | None beyond the rules above |
| `IsEnabled`, `IsChecked` | Shown as declared | Shown as declared; an item is also disabled while no command target can run it |
| `Shortcut`, `Role` | Ignored | The macOS key equivalent and where the menu goes |
| Choosing an item | `IDesktopStatusIcon.Activated` with `Kind` `MenuItem` and the item's `CommandId` | The command target's `Execute(DesktopMenuItem)` |

## Opening files and URLs

`IDesktopOpenRequests` raises `Opened` with the files and URLs the user opens
with the application: its registered URL schemes, such as an OAuth callback, and
its file types, at launch and while it runs. Linux and Windows start the
application with them as arguments, which `ApplicationInstance` forwards to the
running instance; macOS sends open events to the running application.
`DesktopPlatformServices.CreateOpenRequests` in Runic.Application.Views.Desktop selects
the provider:

```csharp
var launch = ApplicationLaunch.FromProcess(args);
// After ApplicationInstance.ClaimAsync made this process the running instance; on macOS on the main thread,
// before the first window.
await using var opens = DesktopPlatformServices.CreateOpenRequests(new(["org.example.notes"]) { IncludeFiles = true }, instance, launch);
opens.Opened += (_, request) => { /* worker thread: validate each URI, then dispatch to a window */ };
```

`DesktopOpenRequests.FromLaunch` is the argument parser on its own. Treat every
opened URL and file as untrusted input: any web page can open a URL with the
application's scheme. The [opening files and URLs design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/open-activation.md) lists what
each platform's package registers.

## Secret storage

`IDesktopSecretStore` keeps secrets, such as access tokens, in the user's
credential store: the Secret Service on Linux (GNOME Keyring, KeePassXC,
KWallet), Credential Manager on Windows and the login keychain on macOS.
`AddRunicPlatformServices()` registers one for the application; it needs no
window.

```csharp
await secrets.SetAsync("com.example.Mail", account, refreshToken);
if (await secrets.GetAsync("com.example.Mail", account) is PlatformResult<string?>.Success(var token)) { /* null: none stored */ }
await secrets.DeleteAsync("com.example.Mail", account); // Success(false): none stored
```

Services and accounts are 1 to 256 characters without NUL. Secrets are UTF-8
text of at most 2560 bytes on every platform; a longer one is
`Failed(TooLarge)`. No reachable store is `Unavailable(BackendUnavailable)`; a
locked store that cannot ask the user to unlock it is `Failed(Locked)`; a
dismissed unlock or access prompt is `Failed(UserDismissed)`. A request may wait
while the store asks the user. Hosts without a provider register
`DesktopSecretStore.Unavailable(reason)`. The
[secret storage design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/secret-storage.md) has the
per-platform behaviour and its verification.

## Trash

`IDesktopTrash` moves a file or directory the user deletes to the trash, from
where the user can restore it: the freedesktop.org Trash on Linux, with the
Trash portal inside Flatpak and Snap, the Recycle Bin on Windows and the Trash
on macOS. .NET's `File.Delete` and `Directory.Delete` delete permanently.
`AddRunicPlatformServices()` registers one for the application; it needs no
window.

```csharp
switch (await trash.MoveToTrashAsync(document.Path))
{
    case PlatformResult<PlatformUnit>.Success: break;
    case PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound }: break; // Already gone.
    case PlatformResult<PlatformUnit>.Unavailable: /* No trash here: ask before deleting permanently. */ break;
    case PlatformResult<PlatformUnit>.Failed failed: /* Report failed.Code. */ break;
}
```

The path is a fully qualified local path to a file, a directory with its
contents, or a symbolic link, which is trashed itself. A location without a
usable trash, such as a network share, is `Unavailable(BackendUnavailable)`, and
the item is never deleted permanently instead: it stays where it was unless the
result is `Success`. A missing item is `Failed(NotFound)`, a refused move
`Failed(PermissionDenied)`. Hosts without a provider register
`DesktopTrash.Unavailable(reason)`. The
[trash design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/trash.md)
has the per-platform behaviour and its verification.

## Desktop applications

`IDesktopApplications` is the desktop's application database: what a file is,
which installed applications open it, the user's default for it, and their
icons. A file manager uses it for "Open With", and opens a file with the
application the user picks. `AddRunicPlatformServices()` registers one for the
application; it needs no window. Linux reads the freedesktop.org desktop
entries, shared MIME-info database, `mimeapps.list` and icon themes directly;
Windows uses the shell's file and protocol associations, and macOS Launch
Services with uniform type identifiers.

```csharp
if (await applications.GetContentTypeAsync(path) is PlatformResult<DesktopContentType>.Success(var type)
    && await applications.GetApplicationsForAsync(type.Id) is PlatformResult<ImmutableArray<DesktopApplicationInfo>>.Success(var handlers))
{
    // handlers[0] is the default; show each one's Name and GetIconAsync(DesktopIconRequest.ForApplication(app.Id, 32)).
    await applications.LaunchAsync(handlers[0].Id, [path]);
}
```

Identifiers are the platform's own: on Linux a desktop file ID such as
`org.gnome.TextEditor.desktop` and a MIME type such as `text/plain`; on Windows
an executable's path (an AppUserModelID for a packaged application) and a MIME
type or an extension such as `.txt`; on macOS a bundle identifier such as
`com.apple.TextEdit` and a uniform type identifier such as `public.plain-text`.
`x-scheme-handler/<scheme>` names a URL scheme everywhere. `LaunchAsync` starts
only an installed application the desktop knows, and passes each path or URI as
one argument, never through a shell. `SetDefaultApplicationAsync` changes one
key of the user's `mimeapps.list` on Linux, asks Launch Services on macOS, which
may confirm with the user, and reports `PermissionDenied` on Windows, where only
the user chooses defaults (in `ms-settings:defaultapps`). Icons are PNG or SVG
bytes.
Inside Flatpak and Snap the host's applications are out of reach and the
application methods report `Unavailable(BackendUnavailable)`; use
`IDesktopFileLauncher` there. Hosts without a provider register
`DesktopApplications.Unavailable(reason)`. The
[desktop applications design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/desktop-applications.md)
has the per-platform behaviour, the algorithms and their verification.

## Copy and move

`IDesktopFileTransfers` copies and moves files and directories as a file
manager does: with progress, cancellation and a decision for each conflict.
`AddRunicPlatformServices()` registers one for the application; every
operating system uses the same managed engine
(`Runic.Platform.Runtime.LocalFileTransfers.Create()`).

```csharp
var request = new FileTransferRequest([report, photos], destinationDirectory);
if (await transfers.CopyAsync(request, cancellationToken) is PlatformResult<FileTransferSummary>.Success(var summary))
    Console.WriteLine($"{summary.Transferred} copied, {summary.Skipped} skipped");
```

Each source goes into the destination directory under its own name. A file is
written under a hidden temporary name and renamed into place once complete, so
a directory listing, or a `RunicPathWatcher` watching it, never sees a partial
file. A move renames a source that stays on its file system and copies one that
does not, removing the source only after its whole item is complete.

When the transfer stops, by an error, a conflict answered `Cancel` or
cancellation, items completed before stay done, and what the item in progress
did is put back: its copies are removed, renamed entries move back, and an entry
it replaced returns. The first error is `Failed` with `NotFound`,
`PermissionDenied`, `Conflict` (a conflict without a handler), `UserDismissed`
(a conflict answered `Cancel`), `IOError`, such as for a full disk, or `Other`
for a native error no other code describes. Caller
cancellation throws `OperationCanceledException`.

### What a transfer keeps

| Kept | Not kept |
| --- | --- |
| File content | Owner and group |
| Last write time of files and directories | Access control lists and extended attributes, including macOS quarantine and tags |
| Permission bits on Linux and macOS (`UnixFileMode`) | Alternate data streams on Windows |
| Read-only, hidden and archive attributes on Windows | macOS resource forks and Finder information |
| Symbolic links, as links; they are never followed | Hard links: each becomes a separate file |
| Directory structure, empty directories included | Sparse regions: holes are written out; creation and access times |

Named pipes, sockets and devices are skipped and counted in `Skipped`. On
Windows, copying a symbolic link needs Developer Mode or an elevated process;
otherwise the transfer fails with `PermissionDenied`. Copying is block by
block in .NET: reflinks, `CopyFileEx` and `clonefile` are not used.

### Conflicts

When an entry of the destination has a source's name, the transfer calls
`ResolveConflict` with a `FileTransferConflict`, the source and the existing
entry with their sizes and times, and waits for a `FileConflictResolution`:
`Skip`, `Replace`, `KeepBoth` (`report (2).txt`), `Merge` (directories only:
each conflict inside is decided in turn) or `Cancel`. `ApplyToAll` answers every
later conflict of the same kind, between two directories or not. Without a
handler the transfer stops at the first conflict with `Failed(Conflict)`.
Copying an item into its own directory keeps both without asking; moving it
there does nothing.

The handler is called on the `SynchronizationContext` that was current when the
transfer started. In a ViewModel command that is the model context, so the
handler can ask the page through an interaction, and progress reported through
a `Progress<T>` created in the command updates state in a later turn.

### Show progress and ask the page

This ReactiveUI ViewModel pastes files the user copied in its own listing. The
command is a bridged operation, so the page can cancel it; its progress is
state, and conflicts reach the page through an interaction. The paths stay in
.NET; the page sees names and sizes.

```csharp
/// <summary>How far a paste is.</summary>
public sealed record PasteProgress(int Items, int TotalItems, double Bytes, double TotalBytes);

/// <summary>An entry of the folder that a pasted one would replace.</summary>
public sealed record ConflictPrompt(string Name, bool IsDirectory, double Bytes, DateTimeOffset Modified, bool CanMerge);

/// <summary>The user's decision.</summary>
public sealed record ConflictAnswer(FileConflictAction Action, bool ApplyToAll);

/// <summary>What a paste did.</summary>
public sealed record PasteResult(int Transferred, int Skipped, bool Cancelled);

public sealed partial class FolderViewModel : ReactiveObject
{
    private readonly IDesktopFileTransfers _transfers;
    private ImmutableArray<string> _copied = [];  // Set by the folder's own Copy command.

    public FolderViewModel(IDesktopFileTransfers transfers, string location)
    {
        _transfers = transfers;
        Location = location;
        PasteCommand = ReactiveCommand.CreateFromTask(PasteAsync);
    }

    public string Location { get; }

    /// <summary>The paste in progress, or null.</summary>
    [Reactive]
    public partial PasteProgress? Pasting { get; private set; }

    /// <summary>Asks which way to resolve a conflict.</summary>
    public Interaction<ConflictPrompt, ConflictAnswer> ResolveConflict { get; } = new();

    /// <summary>Copies the entries copied earlier into this folder.</summary>
    public ReactiveCommand<RxVoid, PasteResult> PasteCommand { get; }

    private async Task<PasteResult> PasteAsync(CancellationToken cancellationToken)
    {
        var request = new FileTransferRequest(_copied, Location)
        {
            // Created in the command's turn: each report sets state in a later turn of the model context.
            Progress = new Progress<FileTransferProgress>(progress =>
                Pasting = new(progress.CompletedItems, progress.TotalItems, progress.CompletedBytes, progress.TotalBytes)),
            // Called in the model context; the page answers through the interaction.
            ResolveConflict = async (conflict, token) =>
            {
                var prompt = new ConflictPrompt(Path.GetFileName(conflict.DestinationPath), conflict.Destination.IsDirectory,
                    conflict.Destination.Length, conflict.Destination.LastWriteTime, conflict.CanMerge);
                var answer = await ResolveConflict.Handle(prompt); // The command's cancellation cancels the question.
                return new FileConflictResolution(answer.Action) { ApplyToAll = answer.ApplyToAll };
            },
        };
        try
        {
            return await _transfers.CopyAsync(request, cancellationToken) switch
            {
                PlatformResult<FileTransferSummary>.Success(var done) => new(done.Transferred, done.Skipped, Cancelled: false),
                PlatformResult<FileTransferSummary>.Failed { Code: PlatformFailureCode.UserDismissed } => new(0, 0, Cancelled: true),
                var stopped => throw new IOException($"The paste stopped: {stopped}"),
            };
        }
        finally { Pasting = null; }
    }
}
```

The page starts the operation, renders `pasting` from the snapshot, cancels
through the operation controller and answers conflicts while it is mounted:

```ts
import { createOperationController } from "@runic-artifex/views";

const paste = createOperationController(() => folder.startPaste());
const unregister = folder.interactions.resolveConflict.handle(
  async (prompt, { signal }) => askReplaceDialog(prompt, signal), // resolves to { action: "Replace", applyToAll: false }
);
folder.subscribe(state => renderProgress(state.pasting));
pasteButton.onclick = () => void paste.run();
cancelButton.onclick = () => void paste.cancel();
```

Cancel aborts the command's `CancellationToken`, which stops the transfer within
a block and puts back the item in progress. The
[operations guide](https://docs.runic-artifex.eu/guides/application/guides/operations-and-cancellation/)
covers pending state and cancellation feedback. The
[file transfer design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/file-transfers.md)
has the full behaviour and its verification.

### Watch a directory

`RunicPathWatcher` in Runic.Application.Views watches the directory a file
manager shows, also while a transfer writes into it:
`RunicWatchTarget.Directory(location)` reports entries added, removed and
renamed, through inotify on Linux, `ReadDirectoryChangesW` on Windows and
FSEvents on macOS, and polls network file systems. Hidden temporary files that
a transfer writes start with `.`, so a pattern such as `*.txt`, or skipping
names that start with `.`, shows only complete entries. See
[Watching files](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/dotnet/Runic.Application.Views#watching-files).

## Volumes

`IDesktopVolumes` lists the desktop's storage volumes, such as USB sticks, SD
cards and other disks, mounts, unmounts and ejects them, and reports them as
they appear, change and disappear. `AddRunicPlatformServices()` registers one
for the application; it needs no window and is disposed with the service
provider.

```csharp
await foreach (var change in volumes.WatchAsync(cancellationToken))
{
    switch (change)
    {
        case PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Snapshot(var all)): Show(all); break;
        case PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Added(var volume)): Add(volume); break;
        case PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Changed(var volume)): Update(volume); break;
        case PlatformResult<DesktopVolumeChange>.Success(DesktopVolumeChange.Removed(var id)): Remove(id); break;
        case PlatformResult<DesktopVolumeChange>.Unavailable: ShowNone(); break; // A Snapshot follows if the service returns.
    }
}

if (await volumes.MountAsync(stick.Id) is PlatformResult<DesktopVolume>.Success(var mounted))
    OpenFolder(mounted.MountPaths[0]);
```

The watch starts with a `Snapshot` of every volume. `DesktopVolume.Id` is
opaque; `MountPaths` is empty while the volume is not mounted, and `CanMount`,
`CanUnmount` and `CanEject` say what applies now. Mounting may show an
authorization prompt: a refusal is `Failed(PermissionDenied)`, a dismissed
prompt `Failed(UserDismissed)`, a volume in use `Failed(ResourceBusy)`, a
locked encrypted volume `Failed(Locked)`, and a volume that is gone
`Failed(NotFound)`. Unmounting a volume that is not mounted succeeds; ejecting
unmounts every volume of the drive first. An operation the volume or platform
does not offer is `Unavailable(BackendUnavailable)`.

| Platform | Provider |
| --- | --- |
| Linux | UDisks2 on the system bus, which asks Polkit; inside Flatpak add `--system-talk-name=org.freedesktop.UDisks2` |
| Windows | The volume management API: drive letters and mounted folders. Windows mounts volumes itself, so `MountAsync` and `UnmountAsync` only succeed for a volume already in that state and otherwise report `Unavailable(BackendUnavailable)`; `EjectAsync` is "Safely Remove Hardware" |
| macOS | Disk Arbitration, as Finder does; an application with a file open on the volume makes an unmount or eject `Failed(ResourceBusy)`. The startup disk's volumes are never unmounted or ejected |

Hosts without a provider register `DesktopVolumes.Unavailable(reason)`. The
[volumes design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/volumes.md)
has the per-platform behaviour and its verification.

## Thumbnails

`IDesktopThumbnails` returns the thumbnail the desktop's file manager shows for
a file, as a PNG image, so a file browser can show pictures, videos and
documents without decoding them itself. `AddRunicPlatformServices()` registers
one for the application; it needs no window.

```csharp
if (await thumbnails.GetThumbnailAsync(path, 128) is PlatformResult<DesktopThumbnail?>.Success({ } thumbnail))
    ShowImage("data:image/png;base64," + Convert.ToBase64String(thumbnail.Png.AsSpan()), thumbnail.Width, thumbnail.Height);
else
    ShowIcon(path); // No thumbnail: show the type's icon, from IDesktopApplications.GetIconAsync.
```

`size` is the largest edge to display, up to 1024 pixels. The image keeps the
file's aspect ratio and can differ in size from the request, so scale it to
display: Linux returns the cached size at least as large (128, 256, 512 or 1024
pixels), Windows and macOS at most `size`. A file without a thumbnail, such as
a type no thumbnailer handles or one whose thumbnailing failed, is
`Success(null)`; an icon is never returned instead. A missing file is
`Failed(NotFound)`.

| Platform | Provider |
| --- | --- |
| Linux | The freedesktop.org thumbnail cache (`~/.cache/thumbnails`) that file managers fill; on a miss the session's `org.freedesktop.thumbnails.Thumbnailer1` service, such as Tumbler on Xfce. GNOME and KDE run no such service, so there only thumbnails their file managers already made are returned. Inside Flatpak add `--filesystem=xdg-cache/thumbnails:ro` and `--talk-name=org.freedesktop.thumbnails.Thumbnailer1`. |
| Windows | The shell's thumbnail providers and cache, as Explorer shows them |
| macOS | Quick Look, as Finder shows them |

Hosts without a provider register `DesktopThumbnails.Unavailable(reason)`. The
[thumbnails design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/thumbnails.md)
has the per-platform behaviour and its verification.

## Privileged actions

`IDesktopPrivilegedActions` runs a step the application declared at build time,
a fixed program with bounded arguments, as the administrator after the
platform's authorization prompt. The program ran is `Success` with its exit
code; a refused authorization is `Failed(PermissionDenied)`, a dismissed prompt
`Failed(UserDismissed)`, and no authorization service `Unavailable`. Arguments
are an argument vector, never a shell command line.
`DesktopPrivilegedActions.Unavailable(reason)` is the provider for hosts and
platforms without one. Linux uses Polkit and pkexec through
Runic.Platform.Linux.Polkit, Windows a UAC-elevated helper through
Runic.Platform.Windows.Elevation, and macOS a launch daemon and Authorization
Services through Runic.Platform.MacOS
([guide](../Runic.Platform.MacOS/README.md#privileged-actions)).

### Declare privileged actions

Declarations are `PrivilegedActionDeclaration` values that this package's build
targets generate from `RunicPrivilegedAction` items, in any project that
references Runic.Platform as a package:

```xml
<ItemGroup>
  <RunicPrivilegedAction Include="org.example.app.activate"
                         Program="/usr/libexec/example/example-helper"
                         Verb="activate"
                         Description="Activate a system configuration"
                         Message="Authentication is required to activate the system configuration."
                         MaximumArguments="1"
                         ArgumentPattern="[A-Za-z0-9._+-]+" />
</ItemGroup>
```

| Metadata | Meaning |
| --- | --- |
| Item identity | The action ID: lowercase reverse-DNS, at least two segments, the same on every platform. |
| `Program` | The absolute, normalized Unix path of the privileged program. Linux runs it there; other platforms derive their own location from it unless their metadata below says otherwise. Required. |
| `Verb` | The first argument, which selects the action. Every action needs its own verb, also across programs, and an action without a verb is allowed only on its own (`RUNICPA003`). |
| `Description`, `Message` | What the authorization prompt shows, passed unchanged to the platform packages. Both required. |
| `MaximumArguments` | Arguments accepted after the verb, 0 to 64. Default 0. |
| `ArgumentPattern` | A .NET regular expression every argument after the verb must match completely, evaluated with `RegexOptions.NonBacktracking`. |
| `Name` | The generated property name, when the last ID segment does not make a good one. |
| `WindowsProgram` | Windows only: the elevated helper, as a path relative to the application's directory (such as `helpers\example-helper.exe`) or an absolute path. Default: the file name of `Program` with `.exe`, in the application's directory. |
| `MacProgram` | macOS only: the helper, as a path relative to the application bundle (such as `Contents/MacOS/example-helper`) or an absolute path. |

#### Commands under one action

The platform keeps an authorization per action: Polkit's `auth_admin_keep`
retains it for the action ID. Several operations of one program that should
share one authorization, such as activating a system and then collecting
garbage, are commands of one action, declared as `RunicPrivilegedCommand`
items. The first argument after the verb names the command, and each further
argument must match the pattern at its position:

```xml
<ItemGroup>
  <RunicPrivilegedAction Include="org.example.app.system"
                         Program="/usr/libexec/example/example-helper"
                         Verb="system"
                         Description="Change the system"
                         Message="Authentication is required to change the system."
                         PolkitAllowActive="auth_admin_keep" />
  <RunicPrivilegedCommand Include="activate" Action="org.example.app.system"
                          Arguments="switch|boot;/nix/store/[a-z0-9]{32}-nixos-system-[A-Za-z0-9._+-]+" />
  <RunicPrivilegedCommand Include="rollback" Action="org.example.app.system" />
  <RunicPrivilegedCommand Include="gc" Action="org.example.app.system"
                          Arguments="[1-9][0-9]{0,2}" MinimumArguments="0" />
</ItemGroup>
```

| Metadata | Meaning |
| --- | --- |
| Item identity | The command's name, the first argument after the verb. Unique within its action. |
| `Action` | The ID of the `RunicPrivilegedAction` item it belongs to. Required. |
| `Arguments` | One pattern per argument after the name, separated by `;`, each matched completely like `ArgumentPattern`. Write `\x3B` for a semicolon inside a pattern. Default: no arguments. |
| `MinimumArguments` | How many of those arguments are required; the rest may be left out from the end. Default: all. |

An action with commands accepts only its commands. Its `MaximumArguments`
defaults to the most arguments a command takes, counting the name; its
`ArgumentPattern`, when set, still applies to every argument, the name
included. The declaration carries the commands as
`PrivilegedActionDeclaration.Commands`, so the application's request and the
privileged program's `PolkitHelper.Match` (or `ElevatedHelper.Match`, or the
macOS daemon) check the same positions; the program then switches on the
command name. Invalid commands are build error `RUNICPA015`, and invalid
command patterns `RUNICPA008`.

Platform packages read further metadata of their own, named after their
platform, such as Polkit's `PolkitIconName` and `PolkitAllowActive`; see their
guides. Metadata no package reads is
ignored.

Each build validates the items and writes `PrivilegedActions.g.cs` under
`obj/.../runic-privileged/`: an internal static partial class with one
`PrivilegedActionDeclaration` property per action and `All`. The application
and its privileged program both use it. Invalid items are build errors
`RUNICPA001` to `RUNICPA009`, `RUNICPA011` and `RUNICPA015`, described in the
[diagnostics catalog](../../../docs/diagnostics.md#privileged-actions).

| Property | Default |
| --- | --- |
| `RunicPrivilegedActionsClassName` | `PrivilegedActions` |
| `RunicPrivilegedActionsNamespace` | `$(RootNamespace)` |
| `RunicPrivilegedActionsReservedNames` | `All`, `MacOSDaemon` and `WindowsElevation`, the property names platform packages generate in other parts of the class. An action's `Name` cannot be one of them. |

The class name and namespace defaults are set when the package's targets are
evaluated, after the project file, so a project's own `RootNamespace` applies
and platform targets can read the final values.

Two targets are supported hooks: `RunicValidatePrivilegedActions`, which
validates the items, and `RunicGeneratePrivilegedActions`, which writes the
class. A project can run its own targets before or after them with
`BeforeTargets` and `AfterTargets`. The names of the other privileged-action
targets, in this package and in the platform packages, may change.

A platform package extends the generation with a target that depends on
`RunicValidatePrivilegedActions`, which validates the items once, and appends
that target to `RunicPrivilegedActionsDependsOn`; `RunicGeneratePrivilegedActions`
writes the class only after those targets succeeded. A platform package that
generates its own part of the class uses a reserved property name, and adds any
new one to `RunicPrivilegedActionsReservedNames`. These two properties are the
protocol between Runic.Platform and the platform packages.
