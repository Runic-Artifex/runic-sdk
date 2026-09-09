# Runic.Platform.Linux.Gtk4

GTK 4 services for a Runic application with an explicitly selected GTK 4 desktop host.

`Gtk4PlatformProvider.CreateTextClipboard(owner)` keeps clipboard work on the verified
presentation dispatcher. `CreatePortalWindowOwner(owner)` exports only valid GTK 4 X11 or
Wayland parent identifiers for `Runic.Platform.Linux.Portal`; it never exposes a raw window
pointer or silently changes an owned request into an unparented request.

This package requires GTK 4.12 or later on Linux. On Wayland, it uses GTK's exported-handle
API and releases the handle when the portal request lease is disposed.
