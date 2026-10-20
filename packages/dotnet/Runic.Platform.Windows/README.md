# Runic.Platform.Windows

Windows implementations of the Runic.Platform services for `win-x64` and
`win-arm64`: file dialogs, the Unicode text clipboard, settings, notifications,
inhibition, login startup, secret storage, trash, thumbnails, volumes, desktop
applications, file and URI opening, the status icon and window presentation.
`WindowsPlatformProvider` creates the application-scoped services, the status icon
included, which a tray-only application can create without a window. Every factory
is annotated `[SupportedOSPlatform("windows")]` and throws
`PlatformNotSupportedException` on another operating system or before Windows 8.
Notifications and login startup require the application ID: it is the
AppUserModelID and the Run value name, and Windows has no other identity for a
process outside an MSIX package (see [MSIX packages](#msix-packages)). The window
services (file dialogs, the clipboard and file handoff) belong to a window:
Runic.Application.Views.Desktop's `PlatformServices.ForWindow` and
`AddRunicPlatformServices()` create them for each window, whose owner dispatches
file dialogs on the window's STA and drains native callbacks before disposal. This
package does not discover providers or depend on Desktop.

File dialogs require an owner, use filesystem-only Common Item Dialogs and retain
the shared runtime's atomic replacement policy. Filters become the dialog's file type list, the first
selected; `OpenFilesAsync` uses `FOS_ALLOWMULTISELECT`; and the initial folder
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
before the package is signed. Login startup through the Run key reports
`Unavailable(BackendUnavailable)` in a package: Windows redirects the
package's writes to `HKCU` into the package, where Windows never reads them.
Declare a `desktop:StartupTask` extension in the package manifest instead.
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

## Trash

`WindowsPlatformProvider.CreateTrash()` moves items to the Recycle Bin through
the shell's `IFileOperation`, without a confirmation, error or progress dialog,
so Explorer can restore them. The shell would delete an item it cannot recycle,
such as one on a network share or larger than the Recycle Bin, permanently;
the provider refuses that deletion, and the request reports
`Unavailable(BackendUnavailable)` with the item in place. See the
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
removal, which is `Failed(ResourceBusy)`. A drive without a removable device,
and the disk the system runs from, report `Unavailable(BackendUnavailable)`.
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
points to `ms-settings:defaultapps`. See the
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

## Status icon and window presentation

`PlatformServices.CreateStatusIcon()` and `AddRunicPlatformServices()` in
`Runic.Application.Views.Desktop` select this package's notification-area icon
on Windows. It is a `Shell_NotifyIconW` icon with `NOTIFYICON_VERSION_4`, owned
by a hidden window on its own thread. A click or Space/Enter (`NIN_SELECT`,
`NIN_KEYSELECT`) raises `Primary`, a middle click `Secondary`, and the context
menu opens with `TrackPopupMenuEx` after the window takes the foreground, as the
shell requires. Menu commands keep their IDs across `ShowAsync` calls, and a
choice resolves against the menu that was shown. Activations arrive one at a
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

`PlatformServices.PresentAsync(owner)` restores a minimized Desktop window,
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
