# Runic.Platform.Linux.Portal

Direct XDG desktop portal file dialogs and URI opening, independent of GTK.
The session portal chooses the desktop backend: a GTK Runic window can therefore
use KDE's file chooser on Plasma. This package uses the MIT-licensed
`Tmds.DBus.Protocol` 0.95.1 with a fixed, reflection-free protocol implementation.

```csharp
var parent = new Gtk3PortalWindowOwner(desktopNativeOwner); // Runic.Platform.Linux
// Or Gtk4PortalWindowOwner from Runic.Platform.Linux.Gtk4.
var files = PortalPlatformProvider.CreateFileDialogs(parent,
    diagnostic => Console.Error.WriteLine($"{diagnostic.Code}: {diagnostic.Remediation}"));
```

Presentation-bound requests require a valid exported X11/Wayland parent. Failure
to export never silently opens an unparented dialog. An application with no native
presentation can deliberately call `CreateUnparentedFileDialogs()`. Cancellation
and owner replacement close the portal request, discard late responses and release
the parent lease. The provider subscribes before invoking the portal, including
when an older portal returns a different request handle. Transport waits are bounded;
the user may keep a healthy chooser open until cancellation or owner closure.

Only one local `file://` selection is accepted. The portal owns persistent document
grants; disposing a file lease does not revoke the user's permission store.
Selections do not grant sibling creation/replacement. Consequently an atomic save
transaction reports `AtomicReplaceUnavailable` before changing the selected file.
An unsandboxed application needing the existing sibling-staging save path can
explicitly use `LinuxPlatformProvider.CreateGtkNativeFileDialogs(owner)` with GTK3.
There is no automatic fallback to toolkit dialogs, including when a portal fails.

`OpenUriAsync(owner, uri)` uses OpenURI for HTTP, HTTPS and mailto links. Local-file
opening requires passing a file descriptor and is not exposed by this URI helper.
Ordinary text clipboard access remains with the selected GTK provider; the Clipboard
portal is tied to Remote Desktop/Input Capture sessions. Background, Inhibit, Print
and ScreenCast permissions are not requested by a generic window or file service.

Install a session D-Bus service, `xdg-desktop-portal` and the backend appropriate to
your desktop (for example `xdg-desktop-portal-kde` on Plasma). The development flake
provides both GTK and KDE backend binaries without starting or reconfiguring the
user's portal services. Sandboxed packaging must expose the portal bus interface.

Protocol checks run with:

```sh
direnv exec . dbus-run-session -- dotnet run --project tests/dotnet/Runic.Platform.Linux.Portal.Tests -- --dbus
```
