# Linux GTK4 and portal integration — unreleased

Linux embedded applications now select `Gtk3WebKit41` or `Gtk4WebKit6` explicitly
in `DesktopHostOptions.Linux`. GTK4 uses the optional `Runic.Desktop.Gtk4` factory
and `Runic.Platform.Linux.Gtk4` services. Browser-only hosts require no selection.
GTK4 applications enter through `Gtk4Application.Run` on the OS main thread
before any top-level `await`; the provider README contains a complete example.
Discovery can inspect both runtimes without loading either; a process cannot
switch toolkits after native initialization. GTK3 now requires WebKitGTK 4.1;
the old implicit WebKitGTK 4.0 fallback is removed.

The optional `Runic.Platform.Linux.Portal` package calls XDG FileChooser directly,
so desktop portal configuration chooses the chooser independently of the window's
toolkit. It also supports OpenURI for HTTP, HTTPS and mailto. GTK3/GTK4 owner
adapters export X11 or Wayland parents and requests close on cancellation, owner
replacement or portal disconnection. Unparented dialogs require an explicit
hostless API. Text clipboard operations stay with the selected GTK provider.

`LinuxPlatformProvider.CreateFileDialogs` now uses the portal. Unsandboxed
applications that require GTK-native dialogs can call `CreateGtkNativeFileDialogs`
explicitly. Portal file grants do not imply sibling-directory access: atomic-save
transactions report `AtomicReplaceUnavailable` before modifying the destination.
The document/customer migration examples retain the explicit GTK-native path for
their existing atomic-save workflow. There is no hidden fallback inside a sandbox.

The locked Nix shell includes both toolkit/WebKit runtimes, TLS/GIO modules,
GStreamer codecs and GTK/KDE portal binaries. It does not start or reconfigure the
user's desktop portal services.

GTK3 remains supported. GTK4 native desktop coverage and any unsupported window
operations are described in the provider README; this follow-up does not claim
comprehensive accessibility, IME or distribution certification. No new SDK version
has been assigned or published by this change.
