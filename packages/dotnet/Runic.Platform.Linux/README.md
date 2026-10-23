# Runic.Platform.Linux (GTK 3)

The GTK 3 window services for Linux x64: portal file dialogs and file handoff with
GTK 3 parenting, the GTK 3 text clipboard, and message boxes. The package has
no public API. Runic.Application.Views.Desktop selects it for GTK 3 windows from
`DesktopPlatformServices.ForWindow` and `services.AddRunicPlatformServices()`, and never in a
GTK 4 process, which `Runic.Platform.Linux.Gtk4` serves. The package references the
shared runtime and no Desktop, ASP.NET Core, Windows or macOS assemblies; creating its
services does not initialize GTK or load native libraries.

The file dialogs are direct XDG portal dialogs with GTK 3 X11/Wayland parent export.
The desktop chooses the portal backend, so Plasma can show KDE's picker. A failed
parent export never opens an unparented window, and a missing portal never triggers
an implicit toolkit fallback.

The portal dialogs also serve Flatpak and Snap applications. Saves are atomic, including
through a Flatpak document view; see [portal behavior](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform.Linux.Portal/README.md).
A session bus, GTK3, a display and the appropriate portal backend must be installed.

Message boxes are `GtkMessageDialog`s, modal, transient for the window and destroyed
with it, shown with `gtk_widget_show` and answered through their `response` signal
(never `gtk_dialog_run`, so the window's loop keeps running). The message and detail
are the dialog's text and secondary text, the kind its message type, `Title` its title,
and the window's text direction becomes the dialog's. Buttons are added in GNOME's
order (others, then Cancel, then the default button) with `_` mnemonics; the default
button is the default response with the `suggested-action` style, a destructive one
has `destructive-action`, and the cancel button answers `GTK_RESPONSE_CANCEL`. Esc and
the close button choose the cancel button, or answer `null` without one. A box without
buttons shows GTK's OK button. Cancellation destroys the dialog. GTK runs in the
application's process, also inside Flatpak: there is no portal for message dialogs.
The `NativeMessageBoxes` category of `tests/dotnet/Runic.Platform.Linux.Tests` checks
them under Xvfb with JIT and NativeAOT.

The clipboard serves UTF-8 text with GTK selection ownership.
Read limits bound managed decoding and string allocation; GTK itself receives the
native selection before reporting its byte length. No offered UTF8_STRING target
returns null, while an offered empty target returns an empty string. A failed
advertised transfer reports Other. GTK does not expose a separate permission code
for transfer refusal. Concurrent access reports ResourceBusy. A successful write
means GTK acquired ownership; it does not promise clipboard-manager persistence
when the application exits. Write cancellation is checked before acquisition and
cannot replace the actual result once acquisition starts.

Dispose the clipboard while the presentation dispatcher still runs. Disposal drains
pending selection callbacks and releases only this instance's current ownership.
Cancellation drains GTK's callback before completing; it does not revoke native
callback memory. The embedding host must keep its GTK event loop alive until drain
finishes. External clipboard owners can delay responses according to GTK's native
selection timeout.

Xvfb tests cover X11 mechanics only. Actual selected-file, Wayland/portal, keyboard,
Orca, IME, scaling and installed-app evidence is required separately; this package
does not claim those gates passed from a headless test. Directory selection and
persisted portal grants are outside the preview contract.
