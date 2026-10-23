# Runic.Platform.Linux.Portal

The Linux provider of the application-scoped Runic.Platform services, built on XDG
desktop portals, D-Bus services and freedesktop.org specifications, independent of
GTK. It also serves the portal file dialogs, file handoff and URI opening that
Runic's hosts create for each window. The session portal chooses the desktop
backend: a GTK Runic window can therefore use KDE's file chooser on Plasma. This
package makes its D-Bus calls through Runic.Platform.Linux.DBus, with NativeAOT-compatible
proxies generated at build time from pinned upstream portal XML.
Every type is annotated `[SupportedOSPlatform("linux")]`, and `PortalApplication`
throws `PlatformNotSupportedException` on another operating system.

```csharp
// Configure once at application composition. Install the matching .desktop file.
var portals = new PortalApplication("org.example.MyApp",
    diagnostic => Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Remediation}"));
// Register these as application-scoped services and dispose them at shutdown.
var settings = portals.CreateSettings();
var notifications = portals.CreateNotifications();
var volumes = portals.CreateVolumes();
```

`PortalApplication` is the package's entry point. It is immutable and has no
connection or disposal requirement. Its services all use one application ID,
registering it with the portal before the first portal call, and report setup
problems to one [diagnostic sink](#diagnostics). Windows and macOS have static
providers instead (`WindowsPlatformProvider`, `MacOSPlatformProvider`), because
there the process carries its identity (the AppUserModelID or the bundle); the
portals identify an application by the desktop ID it registers on its connection.

File dialogs, file handoff and URI opening belong to a window. Runic.Application.Views.Desktop's
`DesktopPlatformServices.ForWindow` and `services.AddRunicPlatformServices()` create them for
each GTK 3 or GTK 4 window, parented to it, with desktop-inferred identity.
`AddRunicPlatformServices(applicationId)` creates the notifications with the
application ID, or desktop-inferred identity without one. Its inhibition comes from
`PortalApplication.CreateInhibition()`: an application-scoped Inhibit request with
the empty parent window identifier, so it works without a window and is not tied to
one, as the Windows and macOS inhibition is. An application without a window uses
`CreateUnparentedFileDialogs()` for dialogs that the portal shows without a parent.

The services of a process share one session-bus connection per desktop identity:
the application's ID, or desktop-inferred identity for the window services that
Runic's hosts create (inside Flatpak and Snap the sandbox supplies the identity, so
there is one). Volumes use one system-bus connection. A connection that closes is
replaced on the next request; the shared connections stay open for the process.
Calls are bound to a specific portal service owner. An interrupted operation
returns unavailable; it is never replayed automatically, and a pending request ends
as unavailable when the portal service is replaced or exits. After a portal restart
the next call registers the application ID with the new portal first. Settings
watch `SettingChanged` signals and follow a replaced portal.
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

There is no automatic fallback to GTK's own dialogs, including when a portal fails.

File dialog filters become FileChooser `filters` (case-insensitive glob patterns,
such as `*.[pP][nN][gG]`, and `(1, type)` for each MIME type) with the first as
`current_filter`; the response's `current_filter` is matched back to the
request's filters as the result's `SelectedFilter`; the initial folder
becomes `current_folder`, the folder's bytes with a trailing NUL; and
`OpenFilesAsync` sets `multiple` and accepts every returned local file URI. A
dialog without these options sends the same request as before. Portals before
FileChooser version 4 ignore `current_folder` when opening files.

Directory selection uses `OpenDirectoryAsync` through the same providers for
GTK3 and GTK4. It sends FileChooser `OpenFile` with `directory=true`, requiring
FileChooser version 3 or newer. The provider refuses older portals before showing
a chooser and accepts one exact local directory URI; `OpenDirectoriesAsync` adds
`multiple` and accepts every returned one. Its directory lease retains
presentation ownership; persistent portal document permissions remain owned by
the user's permission store.

The file launcher's `LaunchUriAsync(uri)` uses OpenURI for HTTP, HTTPS and mailto links. A valid Windows Settings URI
(`ms-settings:<page>`) returns `Unavailable(BackendUnavailable)` without a D-Bus call. Relative URIs, paths and other
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

## Diagnostics

`PortalApplication` passes each setup message to its diagnostic sink as a
`PortalDiagnostic` with a `Code`, a `Message` and an optional `Remediation`, the
same three fields as `RunicWindowDiagnostic` and `DesktopDiagnostic`, and a
`Severity`: `Warning` for a problem, `Information` for what a service did. They are
for logs only: the codes and text may change in any release, so branch on the
services' results, not on these. Use `Severity` to choose a log level; Runic
Desktop logs `Warning` entries as warnings and `Information` entries at debug
level. A sink receives informational entries too, and is called on whichever
thread the service is on; an exception it throws is ignored.

| Code | Severity | Meaning |
| --- | --- | --- |
| `portal-identity-registered` | Information | The portal connection registered the application ID. |
| `portal-identity-registration-failed` | Warning | The portal rejected the application ID; install a matching desktop entry. |
| `portal-request-unavailable` | Warning | A portal request could not be made. |
| `portal-parent-unavailable` | Warning | The window could not be exported as the portal dialog's parent. |
| `portal-notification-request-accepted` | Information | The portal accepted a notification; delivery is not confirmed. |
| `portal-notification-action-received` | Information | The desktop invoked a notification action. |
| `portal-notification-activation-unmatched` | Information | An activation names a notification this process did not show, for example after a restart; it is untrusted. |
| `portal-notification-owner-changed` | Information | The notification portal was replaced; the service registers again. |
| `portal-notification-identity-unspecified` | Warning | Notifications without an application ID may lose actions or history. |
| `portal-notification-permission-denied` | Warning | The desktop refused notifications. |
| `portal-notification-backend-unavailable` | Warning | The notification portal is missing or failed. |
| `status-icon-no-watcher` | Warning | No StatusNotifierWatcher runs, so no status area shows the icon. |
| `status-icon-no-host` | Warning | The watcher has no status area showing icons. |
| `status-icon-register-failed` | Warning | Registering the icon failed; it registers again later. |
| `status-icon-bus-unavailable` | Warning | The session bus could not be reached. |
| `status-icon-bus-lost` | Information | The session bus connection ended; the icon registers again. |
| `status-icon-activations-dropped` | Warning | Activations arrived faster than the `Activated` handler returned; those beyond the queue were dropped. |
| `login-startup-identity-unspecified` | Warning | Login startup outside a sandbox needs an application ID. |
| `login-startup-foreign-entry` | Warning | Another autostart entry uses the application's name. |
| `login-startup-portal-unavailable` | Warning | The Background portal is missing or failed. |
| `open-registration-foreign-entry` | Warning | A desktop entry or MIME definition of the application's ID that Runic did not write exists; nothing was registered. |
| `dbus-activation-name-taken` | Warning | Another process owns the application's ID on the session bus, so D-Bus activation is not served. |
| `secret-service-unavailable` | Warning | No Secret Service answers on the session bus. |
| `secret-service-plain-session` | Information | The Secret Service offered only an unencrypted session. |
| `trash-portal-unavailable` | Warning | The Trash portal could not move the item. |
| `desktop-applications-sandboxed` | Information | Inside Flatpak or Snap the host's applications are out of reach. |
| `desktop-application-unknown` | Warning | No usable desktop entry has the requested ID. |
| `desktop-application-not-associated` | Warning | The application does not open the content type. |
| `desktop-application-terminal` | Warning | The application runs in a terminal, which the provider does not start. |
| `desktop-application-exec-invalid` | Warning | The desktop entry's `Exec` key cannot be parsed. |
| `desktop-application-local-files-only` | Warning | The application opens only local files. |
| `desktop-application-activation-failed` | Warning | D-Bus activation of the application failed. |
| `mimeapps-write-failed` | Warning | The user's `mimeapps.list` could not be written. |
| `thumbnail-cache-unavailable` | Information | The host's thumbnail cache is not visible inside the sandbox. |
| `thumbnailer-access-denied` | Warning | The sandbox does not allow talking to the thumbnailer. |
| `network-monitor-unavailable` | Warning | Neither the NetworkMonitor portal nor NetworkManager answers, so network status is unavailable. |

## Desktop services

Application-scoped appearance and notification providers, and owned file opening,
application choice and reveal, are described in the
[desktop services guide](https://docs.runic-artifex.eu/guides/desktop-services/). It includes
composition, native API choices, installation/activation requirements, retained
file access and the per-platform verification status.

## Status icon

`PortalApplication.CreateStatusIcon()` shows an
application's icon in the desktop's status area, with a tooltip, an attention
state and a menu. Status icons have no portal: the icon is a StatusNotifierItem
with a `com.canonical.dbusmenu` menu, registered with
`org.kde.StatusNotifierWatcher`. Plasma, Waybar's tray and GNOME with the
AppIndicator extension provide that watcher.

```csharp
await using var icon = portals.CreateStatusIcon();
icon.Activated += (_, activation) => { /* worker thread: dispatch to the application */ };
await icon.ShowAsync(new DesktopStatusIconState(new DesktopIcon { IconName = "applications-system" }, "My app")
{
    Menu = [new DesktopMenuItem("Open window", "open"), new DesktopMenuSeparator(), new DesktopMenuItem("Quit", "quit")],
});
```

Without a watcher or a status area showing icons, `ShowAsync` returns
unavailable and reports `status-icon-no-watcher` or `status-icon-no-host`. The
icon stays ready and registers once a watcher starts, and again after it
restarts. `HideAsync` releases the icon's bus name, which removes it. A sandbox
must allow owning `org.kde.StatusNotifierItem-*` names and talking to the watcher.

The status area talks to the icon over the session bus, which does not say
which program is calling. Any program in the user's session can therefore
activate the icon or click a menu item, as the status area does. Do not bind a
destructive or irreversible action to a menu item without a confirmation in the
application's own window. The icon checks every call's signature and bounds
what calls can cost. The item and the menu each answer one call at a time, in the
order the status area sent them, and refuse calls beyond a short queue with
`org.freedesktop.DBus.Error.LimitsExceeded`. `Activated` runs one activation at a
time, and up to 32 more wait while a handler runs; later ones are dropped and
reported once as `status-icon-activations-dropped`, so return from the handler
quickly. A menu call takes at most 1024 entry IDs or events, and repeated IDs are
answered once. A second icon shown at the same time uses a session-bus connection
of its own, since status areas look for each icon at fixed object paths.

Plasma fills a submenu when it first opens, so the first hover over a submenu
can show nothing. The VM checks in `nixos/status-icon` drive the icon through
Plasma 6 and through Waybar's tray.

## Login startup

`PortalApplication.CreateLoginStartup()` starts the application with the
`PortalApplication`'s ID when the user logs in. Outside a
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

## Registration and D-Bus activation

`PortalApplication.CreateOpenRegistration()` registers the application's URL
schemes and file types for the current user, for an application that installs
itself (an AppImage, a tarball, `dotnet run`). Outside a sandbox it writes
`$XDG_DATA_HOME/applications/<applicationId>.desktop` with
`Exec="<executable>" %U`, `MimeType=` (`x-scheme-handler/<scheme>` and the
file types) and `X-Runic-Registered=true`, and, for a file type whose extension
the shared MIME-info database does not give its `MimeType`, a definition with
the extension's glob in `$XDG_DATA_HOME/mime/packages/<applicationId>.xml`.
`update-mime-database` and `update-desktop-database` run when they are
installed; GIO also watches the directories. A file type without a `MimeType`
uses the database's type for its extension. A registration replaces the
application's earlier one and never sets a default (`mimeapps.list`).

An entry of the same ID that the application's package installed in
`XDG_DATA_DIRS`, or one in `XDG_DATA_HOME` without the marker, is refused with
`Conflict` and `open-registration-foreign-entry`, and nothing is written.
Inside Flatpak or Snap the manifest exports the entry and the MIME definitions,
so every request reports `Unavailable(BackendUnavailable)`.

`PortalApplication.ExportApplicationAsync(instance)` serves D-Bus activation
for a desktop entry with `DBusActivatable=true`: it owns the application ID on
the session bus and answers `org.freedesktop.Application` at
`/org/example/Notes` (the ID with `.` as `/` and `-` as `_`). `Activate`
becomes a launch of the running instance without arguments, `Open` a launch
with the URIs, which the instance's open requests deliver, and
`ActivateAction` a launch with the `Exec` arguments of the entry's
`[Desktop Action <name>]` (listed in `Actions`); an `activation-token` or
`desktop-startup-id` becomes the launch's `PlatformContext`. Another process
owning the name is `ResourceBusy` with `dbus-activation-name-taken`.
Notifications of the same ID share the name and the object, which answers
their actions. The desktop starts the application through a D-Bus service
file (`share/dbus-1/services/<applicationId>.service`, `Exec=` the program);
GNOME and KDE then deliver later opens to the running process without
starting another.

```csharp
var claim = await ApplicationInstance.ClaimAsync(id, args);
if (claim is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Primary { Instance: var instance } })
{
    await using var opens = DesktopPlatformServices.CreateOpenRequests(new(["org.example.notes"]) { IncludeFiles = true }, instance, launch);
    var exported = await new PortalApplication(id).ExportApplicationAsync(instance);
}
```

## Secret storage

`PortalApplication.CreateSecretStore()` keeps
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

`PortalApplication.CreateTrash()` follows the
freedesktop.org Trash specification, as GIO and KIO do, so Files, Dolphin and
`gio trash --list` show and restore the items. Outside a sandbox it moves an
item into `$XDG_DATA_HOME/Trash`, or, on another file system, into the trash at
the top of its mount (`$topdir/.Trash/$uid` or `$topdir/.Trash-$uid`), with an
info file recording where it was. It renames and never copies, so a location
without a usable trash reports `Unavailable(BackendUnavailable)` and the item
stays. A trashed directory's size goes to the trash's `directorysizes`.
`ListAsync`, `RestoreAsync` and `EmptyAsync` read the home trash and the
topdir trashes this user owns on every mounted file system; a restore never
replaces what is at the original path and recreates missing parents. Inside
Flatpak or Snap it asks the Trash portal, which trashes what the application can
write; it is sent a symbolic link as the link itself, which xdg-desktop-portal
1.22.1 refuses, so a link reports `Unavailable(BackendUnavailable)` there, as do
listing, restoring and emptying.
See the
[trash design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/trash.md).

## Volumes

`PortalApplication.CreateVolumes()` lists, mounts, unmounts and ejects
volumes through UDisks2 (`org.freedesktop.UDisks2`) on the system bus, as GNOME
Files and Dolphin do; it needs no portal, no window and no GTK. UDisks2 asks
Polkit, whose authentication agent may prompt; without an agent, a request
that needs one is `Failed(PermissionDenied)`. UDisks2 chooses the mount point,
such as `/run/media/<user>/<label>`. Creating the volumes connects to nothing;
the first request opens the system bus, and disposal closes it. Without
UDisks2 every request is `Unavailable(BackendUnavailable)`. Inside Flatpak, add
`--system-talk-name=org.freedesktop.UDisks2` to the finish arguments; inside
Snap, connect the `udisks2` interface. `UnlockAsync(id, passphrase, token)`
unlocks a LUKS container (`Encrypted.Unlock`) and returns its cleartext
filesystem, a volume with an `Id` of its own; `MountAsync(id, options, token)`
passes the options, with `ro` for read-only, as UDisks2's `options`. See the
[volumes design](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/platform/volumes.md).

## Network status

`PortalApplication.CreateNetworkStatus()` asks the NetworkMonitor portal
(`org.freedesktop.portal.NetworkMonitor`), which xdg-desktop-portal implements
itself from GLib's network monitor, so it answers sandboxed and unsandboxed
applications on every desktop that runs the portal: `GetStatus` from version 3,
`GetAvailable`, `GetMetered` and `GetConnectivity` in version 2, and the `changed`
signal. Its connectivity levels 1 to 4 are `Local`, `Limited`, `CaptivePortal` and
`Internet`. Where no portal answers, such as under a minimal window manager, it
reads NetworkManager's `State`, `Connectivity` and `Metered` on the system bus and
follows their `PropertiesChanged`: `Connectivity` 2 is a captive portal, `Metered`
1 and 3 (yes, guessed yes) are metered and 2 and 4 are not, and a connected
`State` without a connectivity check is `Local` or `Unknown`. When neither
answers, reads are `Unavailable(BackendUnavailable)` and the
`network-monitor-unavailable` diagnostic is reported. A watch reports
`Unavailable` when its source stops, looks for one again every few seconds and
reports a fresh state once one answers. It needs no window and no GTK. Inside
Flatpak the portal needs no permission; NetworkManager needs
`--system-talk-name=org.freedesktop.NetworkManager`.

## Desktop applications

`PortalApplication.CreateApplications()` reads
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

`PortalApplication.CreateThumbnails()` reads
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
