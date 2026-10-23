# Runic.Platform.Windows

Windows implementations of the Runic.Platform services for `win-x64` and
`win-arm64`: file dialogs, the Unicode text clipboard, settings, notifications,
inhibition, login startup, secret storage, trash, thumbnails, volumes, desktop
applications, file and URI opening, message boxes, network status, the status icon
and window presentation.
`WindowsPlatformProvider` creates the application-scoped services, the status icon
included, which a tray-only application can create without a window. Every factory
is annotated `[SupportedOSPlatform("windows")]` and throws
`PlatformNotSupportedException` on another operating system or before Windows 8.
Notifications and login startup require the application ID: it is the
AppUserModelID and the Run value name, and Windows has no other identity for a
process outside an MSIX package (see [MSIX packages](#msix-packages)). The window
services (file dialogs, the clipboard, file handoff and message boxes) belong to a window:
Runic.Application.Views.Desktop's `DesktopPlatformServices.ForWindow` and
`AddRunicPlatformServices()` create them for each window, whose owner dispatches
file dialogs on the window's STA and drains native callbacks before disposal. This
package does not discover providers or depend on Desktop.

File dialogs require an owner, use filesystem-only Common Item Dialogs and retain
the shared runtime's atomic replacement policy. Filters become the dialog's file type list, the first
selected, and the type the user left selected (`GetFileTypeIndex`) is the result's `SelectedFilter`;
MIME types are ignored, since the dialog filters by extension. `OpenFilesAsync` uses `FOS_ALLOWMULTISELECT`
and `OpenDirectoriesAsync` `FOS_PICKFOLDERS` with it; and the initial folder
uses `SetFolder`, which takes precedence over the folder Windows remembers for the
application. Clipboard reads distinguish absent
text (`null`) from empty text, reject over-limit payloads and return typed busy,
permission and I/O failures. Text containing NUL is invalid. Cancellation before
native execution prevents the operation; after execution starts, the actual write
outcome is returned. Win32 can clear the previous clipboard before a subsequent
write fails; failures must not be interpreted as preservation of previous text.

Win32 and COM declarations are private CsWin32-generated unmanaged bindings; the
Windows Runtime projection uses raw ABI vtables. Both preserve NativeAOT. The
[interop inventory](docs/interop-inventory.md) records each retained declaration. Windows JIT,
NativeAOT and actual interactive selection acceptance are release gates; portable
conformance alone does not certify the Windows environment.

## Desktop services

Application-scoped appearance and notification providers, and owned file opening,
application choice and reveal, are described in the
[desktop services guide](https://docs.runic-artifex.eu/guides/desktop-services/). It includes
composition, native API choices, installation/activation requirements, retained
file access and the per-platform verification status.

## MSIX packages

A process installed from an MSIX package has the package's identity, and
Windows registers its AppUserModelID (`PackageFamilyName!App`) with the
package. `WindowsPlatformProvider.CreateNotifications` then shows toasts under
that ID and ignores the one the application passes, which it cannot know
before the package is signed. Windows redirects the package's writes to
`HKCU` into the package, where Windows never reads them, so login startup in a
package uses the manifest's `desktop:StartupTask` with `TaskId="LoginStartup"`
(`Windows.ApplicationModel.StartupTask`) instead of the Run key: `EnableAsync`
turns the task on (the manifest's arguments apply, not the request's), a task
the user turned off in Settings or Task Manager reports `PermissionDenied` and
stays off, and a package without the task reports `Unavailable`. The runic-app
template declares it. `CreateOpenRegistration` reports
`Unavailable(BackendUnavailable)` in a package, whose manifest declares its
schemes and file types.

`WindowsPlatformProvider.GetLaunch(args)` reads a packaged process's activation
(`AppInstance.GetActivatedEventArgs`, through raw WinRT calls that NativeAOT
keeps): a protocol activation's URI and a file activation's files become the
launch's arguments, so `CreateOpenRequests` delivers them. With
`MultiSelectModel="Player"` on the file type, several files the user opens at
once reach one process. Outside a package it is `ApplicationLaunch.FromProcess`.

An unpackaged application (a ZIP or an installer) registers its
AppUserModelID itself, with a Start menu shortcut whose
`System.AppUserModel.ID` property is the ID.

## Opening files and URLs

The shell starts a registered application with the URL or file as an argument;
`ApplicationInstance.CreateOpenRequests` in Runic.Platform.Runtime forwards it to
the running instance. The application's installer registers the URL scheme and
file types per user under `HKCU\Software\Classes`, with the command
`"C:\…\App.exe" "%1"` quoted so a URL cannot add arguments, or declares them in
an MSIX manifest. The [opening files and URLs design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/open-activation.md) lists the keys.

An application without an installer registers itself with
`WindowsPlatformProvider.CreateOpenRegistration(applicationId)`: a URL protocol
key per scheme and a ProgID `<applicationId>.<extension>` per file type, both
with that command, the extension's `OpenWithProgids` entry, and `Capabilities`
listed under `RegisteredApplications`, so Settings, Default apps, shows the
application. The extension's default stays the user's choice. Each key carries
the application ID, so registering again (for example from a new folder)
updates it; a key in the user's or the machine's classes that names another
command is `Conflict`, checked for every item before anything is written.
`UnregisterAsync` removes only the application's own keys. Both notify the
shell with `SHChangeNotify(SHCNE_ASSOCCHANGED)`.

`https` links to `DesktopOpenOptions.WebLinkHosts` arrive from a package's
`uap3:AppUriHandler` once each host serves `/.well-known/windows-app-web-link`
naming the package; verify that with your own domain.

## Trash

`WindowsPlatformProvider.CreateTrash()` moves items to the Recycle Bin through
the shell's `IFileOperation`, without a confirmation, error or progress dialog,
so Explorer can restore them. The shell would delete an item it cannot recycle,
such as one on a network share or larger than the Recycle Bin, permanently;
the provider refuses that deletion, and the request reports
`Unavailable(BackendUnavailable)` with the item in place. `ListAsync` reads the
Recycle Bin's shell folder, every drive's bin, with each item's original path,
size and deletion time from its `$I` record; `RestoreAsync` runs the item's own
`undelete` verb without UI after checking that nothing is at the original path
(`Failed(Conflict)` otherwise), recreating a missing parent; `EmptyAsync` is
`SHEmptyRecycleBinW` without confirmation, progress or sound. See the
[trash design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/trash.md).

## Thumbnails

`WindowsPlatformProvider.CreateThumbnails()` returns the thumbnail Explorer
shows, from the shell's thumbnail providers and cache, through
`IShellItemImageFactory` with `SIIGBF_THUMBNAILONLY`: a file of a type without a
thumbnail provider gets no thumbnail rather than its icon. WIC encodes it as
PNG. See the
[thumbnails design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/thumbnails.md).

## Volumes

`WindowsPlatformProvider.CreateVolumes()` lists the volumes with a filesystem
Windows recognizes (`FindFirstVolumeW`, `GetVolumeInformationW`), with their
drive letters and mounted folders as mount paths. `Id` is the volume's
`\\?\Volume{GUID}\` path and `DriveId` its disk's device instance ID. Windows
mounts every volume itself: `MountAsync` returns a mounted volume as it is,
`UnmountAsync` succeeds for a volume without a mount path, and both otherwise
report `Unavailable(BackendUnavailable)`. `EjectAsync` stops the drive's
removable device as "Safely Remove Hardware" does (`CM_Request_Device_EjectW`),
which dismounts its volumes first; a program with a file open on it vetoes the
removal, which is `Failed(ResourceBusy)`. An optical drive without a removable
device opens its tray (`FSCTL_LOCK_VOLUME`, `FSCTL_DISMOUNT_VOLUME`,
`IOCTL_STORAGE_EJECT_MEDIA`). Another drive without a removable device, and the
disk the system runs from, report `Unavailable(BackendUnavailable)`. A locked
BitLocker volume with a drive letter is listed `IsLocked` without a mount path,
from the shell's `System.Volume.BitLockerProtection`, which also sets
`IsEncrypted`; `MountAsync` reports it `Failed(Locked)`, and `UnlockAsync(id)`
opens Explorer's BitLocker prompt (the drive's `unlock-bde` verb), waits for it
and reports `Failed(UserDismissed)` when the volume stays locked. Unlocking with
a passphrase needs an administrator and reports `Unavailable(BackendUnavailable)`,
as mount options do.
A watch hears volume arrivals and removals (`CM_Register_Notification`) and
lists again every two seconds for drive letters, labels and media changes that
have no notification. See the
[volumes design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/volumes.md).

## Desktop applications

`WindowsPlatformProvider.CreateApplications()` is the shell's association
database behind `IDesktopApplications`: a file's content type from the registry
(`Content Type` and `PerceivedType` of its extension, or urlmon's content
sniffing when the name does not decide), the applications that open a type from
`SHAssocEnumHandlers` with the default from `AssocQueryString` first, launches
through the application's own registration for the type (`ShellExecuteEx` with
its class, or `IAssocHandler` for URIs), the Start menu's applications
(`shell:AppsFolder`) and icons as PNG. A content type is its MIME type when the
registry's MIME database maps it back to the same extension, else the extension
(`.txt`); `x-scheme-handler/<scheme>` names a URL protocol. An application is
the path of its executable or, for a packaged application, its AppUserModelID.
Only a registered handler of every item's type starts, never a path or a
command line. Windows lets only the user choose defaults, so
`SetDefaultApplicationAsync` reports `PermissionDenied` with a diagnostic that
points to `ms-settings:defaultapps`; the window's file launcher opens that page
with `LaunchUriAsync(new Uri("ms-settings:defaultapps"))`. See the
[desktop applications design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/desktop-applications.md).

## Secret storage

`WindowsPlatformProvider.CreateSecretStore()` keeps secrets as generic
credentials in the user's Credential Manager vault, persisted for this user on
this computer. The target is `<service>/<account>`, with `/` and `%` in the
service percent-encoded, and the user name is the account, so Control Panel's
Credential Manager lists them under Windows Credentials. A process without a
logon session's vault, such as a service, reports
`Unavailable(BackendUnavailable)`. See the
[secret storage design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/secret-storage.md).

## Message boxes

A window's `MessageBoxes` are task dialogs (`TaskDialogIndirect`) owned by the
window and shown on its thread, as the file dialogs are. The message is the main
instruction, the detail the content, and the kind the warning, error or
information icon; the caption is `Title`, or the owner window's title. The
buttons are custom task dialog buttons in Windows' order (default first, cancel
last), with `&` marking the access key; the cancel button is `IDCANCEL`, so Esc,
Alt+F4 and the close button choose it, and only a box with a cancel button, or
without buttons of the application, can be closed that way. A box without buttons
shows the localized OK button. Windows has no destructive button style. A window
with a right-to-left layout (`WS_EX_LAYOUTRTL`, as Runic Desktop's right-to-left
windows have) gets a mirrored dialog (`TDF_RTL_LAYOUT`). Cancellation clicks a
button of the dialog through its window and throws.

Task dialogs exist only in Common Controls version 6, which Runic's hosts do not
declare in a manifest. The provider activates an activation context that does
around each dialog: the manifest resource shell32.dll embeds for its own dialogs
(resource 124), or else `WindowsShell.Manifest` in the Windows directory, and
loads `comctl32.dll` version 6 inside it once for the process. Where neither
works, every box reports `Unavailable(BackendUnavailable)`. An application whose
own manifest already declares Common Controls 6 gets the same version.

## Network status

`WindowsPlatformProvider.CreateNetworkStatus()` reads the Network List
Manager's connectivity hint (`GetNetworkConnectivityHint`) and watches it with
`NotifyNetworkConnectivityHintChange`, whose callback reports the current hint
first. The API needs Windows 10 version 2004 (build 19041); an older Windows
reports `Unavailable(BackendUnavailable)`. The levels map to `None`, `Local`,
`Internet` and, for constrained internet access, `CaptivePortal`: Windows reports a
captive portal, and a network whose internet check failed otherwise, as
constrained. A hidden or unknown level is `Unknown`, available when .NET reports a
network. A fixed or variable cost, a connection over its data limit and roaming
are metered, an unrestricted one is not, and an unknown cost is `null`.

CI's `Native` jobs run the `NativeMessageBoxes` and `NativeNetwork` categories of
`Runic.Platform.Windows.Tests` with JIT and NativeAOT.

## Status icon and window presentation

`DesktopPlatformServices.CreateStatusIcon()` and `AddRunicPlatformServices()` in
`Runic.Application.Views.Desktop` select this package's notification-area icon
on Windows. It is a `Shell_NotifyIconW` icon with `NOTIFYICON_VERSION_4`, owned
by a hidden window on its own thread. A click or Space/Enter (`NIN_SELECT`,
`NIN_KEYSELECT`) raises `Primary`, a middle click `Secondary`, and the context
menu opens with `TrackPopupMenuEx` after the window takes the foreground, as the
shell requires. Menu commands, an item's `CommandId` with its
`CommandParameter`, keep their IDs across `ShowAsync` calls, and a choice
resolves against the menu that was shown and reports both. Activations arrive one at a
time, in order, on a worker thread.

The icon is drawn from the state's RGBA images with `CreateIconIndirect`, using
the smallest image at least as large as the small-icon size. Theme names are
ignored; an icon without images shows the application's icon. Windows has no
attention state, so `NeedsAttention` shows the attention icon, and `Passive`
looks like `Active`. The tooltip is the title and the tooltip text on the next
line, cut to `szTip`'s 127 characters. The window is a hidden top-level window
rather than a message-only one, because message-only windows do not receive the
`TaskbarCreated` broadcast: after Explorer restarts, the icon is added again.
Without a taskbar, `ShowAsync` returns unavailable and the icon appears when
one starts. Applications report that Enter can reach an icon as two
`NIN_KEYSELECT` notifications, so a repeat within 250 ms raises nothing.

The icon belongs to the application: only `HideAsync` and `DisposeAsync`
remove it. Its window ignores `WM_CLOSE` from other processes (`taskkill`
without `/F`, Restart Manager). When the session ends (`WM_ENDSESSION`), or the
window is destroyed some other way, the icon is removed and the window's
thread ends. A later `ShowAsync` starts a new window.

`DesktopPlatformServices.PresentAsync(owner)` restores a minimized Desktop window,
shows a hidden one (as a close-to-tray application leaves it), and calls
`SetForegroundWindow` on the window's thread. Windows grants the
foreground only to the process the user just started, so
`ApplicationInstance.ClaimAsync` calls `AllowSetForegroundWindow` for the
running instance before it forwards a launch. When Windows still refuses, the
taskbar button flashes (`FlashWindowEx`) and the result is
`Failed(PermissionDenied)` with a `SetForegroundWindow` diagnostic.

CI's `Native / win-x64` job runs the `NativeStatusIcon` and
`NativePresentation` categories of `Runic.Platform.Windows.Tests` with JIT and
NativeAOT on the runner's desktop session.

WPF windows have no `DesktopNativeOwner`, so they have no `PresentAsync` path
yet ([#267](https://github.com/Runic-Artifex/runic-sdk/issues/267)).
