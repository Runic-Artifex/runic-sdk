# Runic.Platform.Linux

Portal file dialogs with GTK3 parenting and text clipboard for Linux x64. Register this provider
only in the Linux Desktop host; the package references the shared runtime and no
Desktop, ASP.NET Core, Windows or macOS assemblies. Factory creation does not
initialize GTK or load native libraries. Supply the verified GTK owner dispatcher.

`LinuxPlatformProvider.CreateFileDialogs(owner)` uses direct XDG portal dialogs,
with GTK3 X11/Wayland parent export. The desktop chooses the portal backend, so
Plasma can show KDE's picker. A failed parent export never opens an unparented
window, and a missing portal never triggers an implicit toolkit fallback.

`CreateGtkNativeFileDialogs(owner)` explicitly selects the previous GTK-native
chooser for an unsandboxed compatibility application. Flatpak and Snap must use
the portal path. Portal selections reject sibling staging before an atomic save
transaction changes a file; see [portal behavior](../Runic.Platform.Linux.Portal/README.md).
A session bus, GTK3, a display and the appropriate portal backend must be installed.

`CreateTextClipboard(owner)` serves UTF-8 text with GTK selection ownership.
Read limits bound managed decoding and string allocation; GTK itself receives the
native selection before reporting its byte length. No offered UTF8_STRING target
returns null, while an offered empty target returns an empty string. A failed
advertised transfer reports IoError. GTK does not expose a separate permission code
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
