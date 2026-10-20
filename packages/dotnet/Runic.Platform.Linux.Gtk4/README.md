# Runic.Platform.Linux.Gtk4

GTK 4 services for a Runic application with an explicitly selected GTK 4 desktop host.

The package has no public API. Runic.Application.Views.Desktop creates its services for each
GTK 4 window from `DesktopPlatformServices.ForWindow` and `services.AddRunicPlatformServices()`: the
clipboard, which keeps its work on the window's dispatcher, and the portal parent, which
exports only valid GTK 4 X11 or Wayland parent identifiers for `Runic.Platform.Linux.Portal`'s
file dialogs and file handoff. It never exposes a raw window pointer or silently changes an
owned request into an unparented request.

This package requires GTK 4.12 or later on Linux. On Wayland, it uses GTK's exported-handle
API and releases the handle when the portal request lease is disposed.
