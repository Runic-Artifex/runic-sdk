# Runic.Platform.Linux

Explicit GTK 3 file dialogs and text clipboard for Linux x64. Register this provider
only in the Linux Desktop host; the package references the shared runtime and no
Desktop, ASP.NET Core, Windows or macOS assemblies. Factory creation does not
initialize GTK or load native libraries. Supply the verified GTK owner dispatcher.

`LinuxPlatformProvider.CreateFileDialogs(owner)` uses an owned modal native chooser.
GTK handles X11 and Wayland parenting. Flatpak, Snap and `GTK_USE_PORTAL=1` require a
responsive session desktop portal. Portal-backed selections reject sibling staging
before a save transaction can change a file. A session bus, GTK 3, a display and an
appropriate portal backend must be installed by the application environment.

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
