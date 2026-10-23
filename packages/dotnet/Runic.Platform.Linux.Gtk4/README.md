# Runic.Platform.Linux.Gtk4

GTK 4 services for a Runic application with an explicitly selected GTK 4 desktop host.

The package has no public API. Runic.Application.Views.Desktop creates its services for each
GTK 4 window from `DesktopPlatformServices.ForWindow` and `services.AddRunicPlatformServices()`: the
clipboard, which keeps its work on the window's dispatcher, and the portal parent, which
exports only valid GTK 4 X11 or Wayland parent identifiers for `Runic.Platform.Linux.Portal`'s
file dialogs and file handoff. It never exposes a raw window pointer or silently changes an
owned request into an unparented request.

The clipboard is also an `IDesktopClipboard`. HTML and RTF are read as their MIME
types' bytes through `gdk_clipboard_read_async`, in 64 KiB chunks up to the bound;
images through `gdk_clipboard_read_texture_async`, which converts every image
format GDK reads, saved as PNG (`gdk_texture_save_to_png_bytes`, GTK 4.6); files
through `gdk_clipboard_read_value_async` with `GDK_TYPE_FILE_LIST`, which also
takes the FileTransfer portal's `application/vnd.portal.filetransfer` in a
Flatpak. A write sets one union content provider of every format, files as a
`GDK_TYPE_FILE_LIST` value that GDK offers as `text/uri-list`,
`x-special/gnome-copied-files` and through the portal. The watch is the clipboard's
`changed` signal.

Message boxes are `GtkAlertDialog`s (GTK 4.10 and later), modal for the window and
answered through `gtk_alert_dialog_choose` with a `GCancellable`, which cancellation
cancels. Its buttons take `_` mnemonics, in GNOME's order; the default and cancel
buttons are the dialog's, so Return and Esc choose them, and Esc without a cancel
button dismisses the box with `null`. An alert dialog has no title, icon or destructive
style and follows the application's text direction. The GTK 4 smoke's `--message-boxes`
mode checks them under Xvfb with JIT and NativeAOT.

This package requires GTK 4.12 or later on Linux. On Wayland, it uses GTK's exported-handle
API and releases the handle when the portal request lease is disposed.
