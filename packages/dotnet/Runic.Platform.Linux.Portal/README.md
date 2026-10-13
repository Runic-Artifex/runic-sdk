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

Calls are bound to a specific portal service owner. An interrupted operation
returns unavailable; it is never replayed automatically. Notifications detect
portal replacement and recreate/register their connection before the next call.
File operations open a fresh identified connection per request. Settings keep one
connection and watch `SettingChanged` signals, reconnecting after portal replacement.
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

Directory selection uses `OpenDirectoryAsync` through the same providers for
GTK3 and GTK4. It sends FileChooser `OpenFile` with `directory=true`, requiring
FileChooser version 3 or newer. The provider refuses older portals before showing
a chooser and accepts one exact local directory URI. Its directory lease retains
presentation ownership; persistent portal document permissions remain owned by
the user's permission store.

`OpenUriAsync(owner, uri)` uses OpenURI for HTTP, HTTPS and mailto links. Local-file
opening uses the separate `CreateFileLauncher(owner)` service and passes a retained file descriptor.
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
