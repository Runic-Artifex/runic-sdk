# Runic.Platform.Linux.Portal

Direct XDG desktop portal file dialogs and URI opening, independent of GTK.
The session portal chooses the desktop backend: a GTK Runic window can therefore
use KDE's file chooser on Plasma. This package uses the MIT-licensed
`Tmds.DBus.Protocol` 0.95.1 with NativeAOT-compatible bindings; Registry and
Notification bindings are generated from pinned upstream XML.

```csharp
// Configure once at application composition. Install the matching .desktop file.
var portals = new PortalApplication("org.example.MyApp",
    diagnostic => Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Remediation}"));
var parent = new Gtk3PortalWindowOwner(desktopNativeOwner); // Runic.Platform.Linux
// Or Gtk4PortalWindowOwner from Runic.Platform.Linux.Gtk4.
var files = portals.CreateFileDialogs(parent);
var launcher = portals.CreateFileLauncher(parent);
// Register these as application-scoped services and dispose them at shutdown.
var settings = portals.CreateSettings();
var notifications = portals.CreateNotifications();
```

`PortalApplication` is immutable and has no connection or disposal requirement.
Its settings, notifications, file dialogs, file handoffs and URI opening all use
one application ID, registering each owned host connection before portal calls.
Existing static factories remain available for desktop-inferred identity.
Runic.Application.Views.Desktop's `services.AddRunicPlatformServices()` uses those static
factories for GTK 4 windows; compose the services yourself to pass a `PortalApplication`.
It also registers notifications with `PortalPlatformProvider.CreateNotifications`,
with the application ID passed to `AddRunicPlatformServices(applicationId)`, or
desktop-inferred identity without one.
Its inhibition comes from `PortalApplication.CreateUnparentedInhibition()`: an
application-scoped Inhibit request with the empty parent window identifier, so
it works without a window and is not tied to one. `CreateInhibition(owner)`
associates each request with a presentation instead.

Calls are bound to a specific portal service owner. An interrupted operation
returns unavailable; it is never replayed automatically. Notifications detect
portal replacement and recreate/register their connection before the next call.
File operations open a fresh identified connection per request. Settings keep one
connection and watch `SettingChanged` signals, reconnecting after portal replacement.
The appearance comes from the portal's `org.freedesktop.appearance` keys, so it is
only as complete as the portal and its backend. Ubuntu 24.04 (xdg-desktop-portal 1.18
with the GNOME backend 46) publishes the colour scheme and contrast, so the accent
colour and reduced motion stay null there. A newer portal whose backend implements
them publishes all four; CI checks all four against Fedora 44's (xdg-desktop-portal
1.22 with xdg-desktop-portal-gnome 50), changed through GSettings as GNOME Settings
does.
Only the notification service owns the application's activation bus name.

Presentation-bound requests require a valid exported X11/Wayland parent. Failure
to export never silently opens an unparented dialog. An application with no native
presentation can deliberately call `CreateUnparentedFileDialogs()`. Cancellation
and owner replacement close the portal request, discard late responses and release
the parent lease. The provider subscribes before invoking the portal, including
when an older portal returns a different request handle. Transport waits are bounded;
the user may keep a healthy chooser open until cancellation or owner closure.

Only one local `file://` selection is accepted. The portal owns persistent document
grants; disposing a file lease does not revoke the user's permission store.

Saves are atomic, and `platform.files.save` is available. The provider classifies
each save selection:

- **Host path.** An unsandboxed application, or a sandbox whose filesystem access
  already covers the path, receives the real path. The save stages
  `.runic-<guid>.tmp` beside the final target of a symbolic link, copies the
  permission bits, synchronizes it and renames it over the target.
- **Document view.** Otherwise the portal returns a document below the mount that
  `org.freedesktop.portal.Documents.GetMountPoint` reports (or `/run/flatpak/doc`),
  confirmed as the FUSE filesystem. The save does not resolve links there. It stages
  `.runic-<guid>.tmp` in the document directory, which the document portal stores as
  a private host tempfile `.xdp-.runic-<guid>.tmp-XXXXXX` beside the real file, and
  renames it onto the exact document name. The portal performs that rename with the
  host's `renameat2`. This is how GLib's `g_file_replace` saves from a sandbox.

On Linux the commit is a direct `renameat2`: a plain rename over an existing file,
and `RENAME_NOREPLACE` for a new one, so a name that appeared since selection is a
`Conflict`. A refused rename (`EXDEV`, `EBUSY`, for example a bind-mounted file) is
`NotCommitted` and is never replaced by a copy. A read-only or revoked document
grant makes `BeginWriteAsync` return `Unavailable(AtomicReplaceUnavailable)` before
it changes anything. In every refusal the file is unchanged; offer Save As.

Through the document view, extended attributes and ACLs are not carried over and
the directory sync is a no-op: after a crash just after the rename, the previous
content may reappear, but never a partial file. If the application or portal stops
mid-save, a hidden `.xdp-*` tempfile can remain beside the document. Document
portals older than 1.14 reject `RENAME_NOREPLACE`; a new file is then created after
re-checking that the name is still absent.

`LinuxPlatformProvider.CreateGtkNativeFileDialogs(owner)` remains an explicit GTK3
opt-in for unsandboxed applications and saves the same way. There is no automatic
fallback to toolkit dialogs, including when a portal fails.

File dialog filters become FileChooser `filters` (case-insensitive glob patterns,
such as `*.[pP][nN][gG]`) with the first as `current_filter`; the initial folder
becomes `current_folder`, the folder's bytes with a trailing NUL; and
`OpenFilesAsync` sets `multiple` and accepts every returned local file URI. A
dialog without these options sends the same request as before. Portals before
FileChooser version 4 ignore `current_folder` when opening files.

Directory selection uses `OpenDirectoryAsync` through the same providers for
GTK3 and GTK4. It sends FileChooser `OpenFile` with `directory=true`, requiring
FileChooser version 3 or newer. The provider refuses older portals before showing
a chooser and accepts one exact local directory URI. Its directory lease retains
presentation ownership; persistent portal document permissions remain owned by
the user's permission store.

`OpenUriAsync(owner, uri)` uses OpenURI for HTTP, HTTPS and mailto links; the file
launcher's `LaunchUriAsync(uri)` sends the same request. Relative URIs, paths and other
schemes throw `ArgumentException` before any D-Bus call, and cancelling the portal's
application chooser is `Failed(UserDismissed)`. Local-file opening uses the file
launcher's `LaunchAsync` and passes a retained file descriptor.
Ordinary text clipboard access remains with the selected GTK provider; the Clipboard
portal is tied to Remote Desktop/Input Capture sessions. Background, Inhibit, Print
and ScreenCast permissions are not requested by a generic window or file service.

Install a session D-Bus service, `xdg-desktop-portal` and the backend appropriate to
your desktop (for example `xdg-desktop-portal-kde` on Plasma). The development flake
provides both GTK and KDE backend binaries without starting or reconfiguring the
user's portal services. Sandboxed packaging must expose the portal bus interface.

Protocol checks are explicit TUnit cases (category `DBus`) that need a session bus. Run them on an
isolated bus:

```sh
direnv exec . dbus-run-session -- dotnet run --project tests/dotnet/Runic.Platform.Linux.Portal.Tests -- --treenode-filter "/*/*/*/*[Category=DBus]"
```

The suite's default run includes a real document-portal save through the session bus's portal. Give
it a private runtime directory, so an isolated bus never touches the desktop session's document
mount: `XDG_RUNTIME_DIR=$(mktemp -d) XDG_DATA_HOME=$XDG_RUNTIME_DIR/data dbus-run-session -- ...`.
Never start a second document portal against your own `XDG_RUNTIME_DIR`.

The GTK4 production-provider directory journey uses a real native window and
exported parent with a controlled FileChooser service on an isolated session bus:

```sh
direnv exec . dbus-run-session -- env GDK_BACKEND=x11 xvfb-run -a dotnet run --project tests/native/Runic.Desktop.Gtk4.Smoke -- --directory
```

It covers exact directory access, dismissal, caller cancellation and native owner
closure. It does not automate a desktop backend's chooser UI.

## Desktop services

Application-scoped appearance and notification providers, and owned file opening,
application choice and reveal, are described in the
[desktop services guide](https://docs.runic-artifex.eu/guides/desktop-services/). It includes
composition, native API choices, installation/activation requirements, retained
file access and the per-platform verification status.

## Status icon

`CreateStatusIcon()` on `PortalApplication` or `PortalPlatformProvider` shows an
application's icon in the desktop's status area, with a tooltip, an attention
state and a menu. Status icons have no portal: the icon is a StatusNotifierItem
with a `com.canonical.dbusmenu` menu, registered with
`org.kde.StatusNotifierWatcher`. Plasma, Waybar's tray and GNOME with the
AppIndicator extension provide that watcher.

```csharp
await using var icon = portals.CreateStatusIcon();
icon.Activated += (_, activation) => { /* worker thread: dispatch to the application */ };
await icon.ShowAsync(new DesktopStatusIconState(new DesktopIcon { ThemeName = "applications-system" }, "My app")
{
    Menu = [new DesktopMenuCommand("open", "Open window"), new DesktopMenuSeparator(), new DesktopMenuCommand("quit", "Quit")],
});
```

Without a watcher or a status area showing icons, `ShowAsync` returns
unavailable and reports `status-icon-no-watcher` or `status-icon-no-host`. The
icon stays ready and registers once a watcher starts, and again after it
restarts. `HideAsync` releases the icon's bus name, which removes it. A sandbox
must allow owning `org.kde.StatusNotifierItem-*` names and talking to the watcher.

Plasma fills a submenu when it first opens, so the first hover over a submenu
can show nothing. The VM checks in `nixos/status-icon` drive the icon through
Plasma 6 and through Waybar's tray.

## Login startup

`CreateLoginStartup(applicationId)` on `PortalApplication` or
`PortalPlatformProvider` starts the application when the user logs in. Outside a
sandbox it writes `~/.config/autostart/<applicationId>.desktop`, which every
XDG desktop reads at login, and marks it with `X-Runic-Application`. It never
changes or removes an entry it did not write, and an entry the user turned off
(`Hidden=true` or `X-GNOME-Autostart-enabled=false`) stays off: enabling reports
`PermissionDenied`. Disabling removes the entry, and with it the user's choice.

Inside Flatpak or Snap the entry is out of reach, so `EnableAsync` and
`DisableAsync` ask the Background portal, which may ask the user and passes the
request's `Reason`. The portal cannot report whether autostart is on, so
`IsEnabledAsync` returns unavailable there. Without an ID the provider works only
in a sandbox, where the portal knows the application.

```csharp
var startup = portals.CreateLoginStartup();
await startup.EnableAsync(new LoginStartupRequest(["--background"]) { Reason = "Keep watching for updates." });
```

## Secret storage

`CreateSecretStore()` on `PortalApplication` or `PortalPlatformProvider` keeps
secrets in the desktop's Secret Service (`org.freedesktop.secrets`): GNOME
Keyring, KeePassXC or KWallet. Secrets travel over an encrypted
`dh-ietf1024-sha256-aes128-cbc-pkcs7` session; a service without it gets a
`plain` session and the `secret-service-plain-session` diagnostic. Items use
libsecret's generic schema (`service` and `account` attributes), so
`secret-tool lookup service <service> account <account>` reads them, and are
labelled `<service> (<account>)`. A locked keyring is unlocked through the
service's own prompt; without a prompter the result is `Failed(Locked)`.

Without a Secret Service, requests report `Unavailable(BackendUnavailable)` and
the `secret-service-unavailable` diagnostic. Inside Flatpak, add
`--talk-name=org.freedesktop.secrets` to the finish arguments; inside Snap,
connect the `password-manager-service` interface. The Secret portal is not used
yet. See the [secret storage design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/secret-storage.md).

## Trash

`CreateTrash()` on `PortalApplication` or `PortalPlatformProvider` follows the
freedesktop.org Trash specification, as GIO and KIO do, so Files, Dolphin and
`gio trash --list` show and restore the items. Outside a sandbox it moves an
item into `$XDG_DATA_HOME/Trash`, or, on another file system, into the trash at
the top of its mount (`$topdir/.Trash/$uid` or `$topdir/.Trash-$uid`), with an
info file recording where it was. It renames and never copies, so a location
without a usable trash reports `Unavailable(BackendUnavailable)` and the item
stays. Inside Flatpak or Snap it asks the Trash portal, which trashes what the
application can write; a symbolic link cannot be trashed there. See the
[trash design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/trash.md).

## Volumes

`PortalPlatformProvider.CreateVolumes()` lists, mounts, unmounts and ejects
volumes through UDisks2 (`org.freedesktop.UDisks2`) on the system bus, as GNOME
Files and Dolphin do; it needs no portal, no window and no GTK. UDisks2 asks
Polkit, whose authentication agent may prompt; without an agent, a request
that needs one is `Failed(PermissionDenied)`. UDisks2 chooses the mount point,
such as `/run/media/<user>/<label>`. Creating the volumes connects to nothing;
the first request opens the system bus, and disposal closes it. Without
UDisks2 every request is `Unavailable(BackendUnavailable)`. Inside Flatpak, add
`--system-talk-name=org.freedesktop.UDisks2` to the finish arguments; inside
Snap, connect the `udisks2` interface. See the
[volumes design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/volumes.md).

## Desktop applications

`CreateApplications()` on `PortalApplication` or `PortalPlatformProvider` reads
the desktop's application database without GIO: desktop entries from the XDG
data directories, the shared MIME-info database (globs, magic, aliases,
subclasses, icons and descriptions), the MIME Apps `mimeapps.list` files and
`mimeinfo.cache`, and icon themes. `GetContentTypeAsync` with `NameOnly` never
touches the file. `LaunchAsync` starts a desktop entry's `Exec` without a
shell, expanding `%f %F %u %U %i %c %k` into whole arguments, or calls
`org.freedesktop.Application` for a `DBusActivatable` entry through
Runic.Platform.Linux.DBus; a `Terminal=true` entry reports
`Unavailable(BackendUnavailable)`. `SetDefaultApplicationAsync` changes one key
of `$XDG_CONFIG_HOME/mimeapps.list` and keeps every other byte. The icon theme
comes from the Settings portal, then KDE's `kdeglobals`, then `hicolor`. Inside
Flatpak or Snap only content types and icons work. See the
[desktop applications design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/desktop-applications.md).

## Thumbnails

`CreateThumbnails()` on `PortalApplication` or `PortalPlatformProvider` reads
the freedesktop.org thumbnail cache, `$XDG_CACHE_HOME/thumbnails`, which GNOME
Files, Dolphin and Thunar fill. An entry counts only when its `Thumb::URI` and
`Thumb::MTime` (and `Thumb::Size`, if present) match the file; a matching entry
in `fail/` means no thumbnail. On a miss it asks the session's
`org.freedesktop.thumbnails.Thumbnailer1` service, such as Tumbler, to make one
and reads the cache again. GNOME and KDE run no such service, so there only
thumbnails their file managers already made are returned; running a desktop's
`.thumbnailer` programs itself is not supported. Inside Flatpak, add
`--filesystem=xdg-cache/thumbnails:ro` and
`--talk-name=org.freedesktop.thumbnails.Thumbnailer1` to the finish arguments;
without them requests return no thumbnail and the diagnostic sink reports
`thumbnail-cache-unavailable` or `thumbnailer-access-denied`. See the
[thumbnails design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/thumbnails.md).
