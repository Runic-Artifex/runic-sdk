# Runic Desktop GTK 4

`Runic.Desktop.Gtk4` is the optional GTK 4/WebKitGTK 6 embedded-window provider.
Select it explicitly; it never loads beside the GTK 3 provider and is not
selected automatically. The provider is pinned to `GirCore.Gtk-4.0` and
`GirCore.WebKit-6.0` 0.8.1 (MIT). At runtime it needs GTK 4.12+ and WebKitGTK 6;
the package contains no native runtime. `DesktopWindow.AnnounceAsync` needs
GTK 4.14 (`gtk_accessible_announce`); with GTK 4.12 or 4.13 the window reports
no `DesktopWindowCapabilities.Announcements`.

WebKitGTK must be initialized on the Linux process main thread. Start the
application with `DesktopEventLoop.Run` directly from `Main`, before an await,
and keep every Desktop operation in the callback:

```csharp
using Runic.Desktop;
using Runic.Desktop.Gtk4;

return DesktopEventLoop.Run(new DesktopHostOptions().WithGtk4(), async host =>
{
    // Create surfaces and windows here.
    return 0;
});
```

`DesktopEventLoop.Run(options, application)` picks the loop for the platform
and the host. On Linux with `WithGtk4()` it runs the GTK 4 application lifetime. On
macOS it runs the AppKit loop. On Windows and Linux GTK 3 it waits for the work.
The same `Main` therefore works on every platform. It validates the options and
builds the host before GTK starts, so an options error does not use up the one
GTK lifetime per process. When the GTK 4 libraries are missing, or no display
can be opened (an SSH session, a headless machine, WSL without WSLg), it does
not start GTK. Instead it reports the reasons, including
`gtk4-display-unavailable`, to `DiagnosticSink` and the Desktop logger. The host
still runs, so a browser fallback can open. The host is disposed before the loop
ends. Only one Desktop event loop runs per process; a nested call throws.
`WithGtk4(applicationId)` validates the reverse-DNS ID on every OS. This is the
only entry point: the former `Gtk4Application.Run`, which skipped the option
validation, browser fallback, signal handling and host disposal, is internal
since 0.9.0. An application that started hosts itself inside it calls
`DesktopEventLoop.Run(new DesktopHostOptions().WithGtk4(), ...)` and creates them
in the callback.

`WithGtk4()` applies the GTK 4 profile in one step: it selects
`LinuxEmbeddedBackend.Gtk4WebKit6` and sets `WindowHostFactory` to a
`Gtk4WindowHostFactory`, so the two cannot disagree. The factory is a
[custom window host](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md#custom-window-hosts)
of Runic Desktop; its former experimental ID `RUNICDESK100` is retired since
0.10.0, so remove its suppressions. On Windows and macOS it
keeps the platform's own embedded host. It throws `ArgumentException` when the
options already carry another factory. When the profile cannot run,
`DesktopHost.Validate` and `DesktopPlatform.GetAvailability(options)` list every missing library
(`gtk4-runtime-missing`, `webkitgtk6-runtime-missing`) rather than only the
first. `dotnet runic doctor` adds a `gtk4-profile` check that also names a
missing `Runic.Application.Views.Desktop`, which creates the GTK 4 window services.

Pass an installed application identity with `WithGtk4("org.example.MyApp")`.
Inside Flatpak the runner automatically uses `FLATPAK_ID`;
an explicit ID must match it. Outside Flatpak the existing overload retains its
default identity.

The runner has one non-reentrant lifetime per process. It tracks live GTK4
hosts, closes and weak-finalizes their window and WebView objects when the
callback finishes, then exits GTK. This main-thread model avoids the current
WebKitGTK exit-time threading defect ([WebKit bug 284807](https://bugs.webkit.org/show_bug.cgi?id=284807)).
Each view owns a separate WebKit context.

GTK4 rejects centering, transparent, high-contrast, profile-path,
custom-argument, and icon-file options, and on Wayland a global position;
`DesktopHost.Validate` reports each one as a `window-option-unsupported`
error before a window opens. It ignores `ColorScheme`, because WebKitGTK follows
the GTK theme for every window, and reports it as a `color-scheme-unsupported`
warning. It supports independent minimum dimensions.
Runic.Application.Views.Desktop's `services.AddRunicPlatformServices()` selects the
GTK 4 window services when the process runs this backend: portal-first file dialogs,
parented through `Runic.Platform.Linux.Gtk4`, so KDE uses its own picker.

A window shows `DesktopHostOptions.MenuBar` above the page as a
`GtkPopoverMenuBar` built from a `GMenu`, with one action per item in the
window's `runic` action group, and reports `DesktopWindowCapabilities.MenuBar`
and `ContextMenu`. Runic does not use `gtk_application_set_menubar` or
application accelerators, which run before the page: an item's shortcut is
shown through the menu's `accel` attribute and answered only after the page left
the key alone. `DesktopWindow.ShowContextMenuAsync` shows a `GtkPopoverMenu` on
the web view. On X11 and on Wayland the bar goes into the desktop's global menu,
such as KDE Plasma's, while one is shown, and the in-window bar is hidden
meanwhile; on Wayland that needs KWin's `org_kde_kwin_appmenu`, which GTK 4
cannot set itself, so Runic sets it on the window's surface. A
Flatpak made by `dotnet runic package` includes `--talk-name=com.canonical.AppMenu.Registrar`
in its finish arguments for that; a hand-written manifest needs it too, and
without it the bar stays in the window. See
[Linux: GTK 3 and GTK 4](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md#linux-gtk-3-and-gtk-4)
and [the global menu](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md#linux-the-desktops-global-menu).

GTK 4 itself has no window position API. On X11 Runic reads and sets the
position of the window's X window through Xlib, as GTK 3 did: `X` and `Y` open
the window there, the window reports `DesktopWindowCapabilities.Move` and
`MoveAsync` moves it, and `DesktopWindow.State` and its `Placement` report the
position in logical pixels: the top-left corner of the frame the window manager
draws (its `_NET_FRAME_EXTENTS`, also when it does not reparent the window, as
KWin does for X11 windows on Wayland), or of GTK's own decorations without their
shadow. A window opening at a position, or a hidden window shown for the first
time, carries it in its `WM_NORMAL_HINTS` (`USPosition`, as GTK 3's
`gtk_window_move` set it), so the window manager keeps it instead of placing
the new window itself. The window manager may still adjust a requested
position, as with GTK 3. Whether a process uses X11 follows GDK: `GDK_BACKEND`,
else Wayland when `WAYLAND_DISPLAY` is set and X11 otherwise.

On Wayland the compositor places every window: an explicit `X` and `Y` are
rejected, the window has no `Move`, and the position in `State` is `null`.

The window reports `DesktopWindowCapabilities.State`: `DesktopWindow.State` has
the content size, maximized and full-screen state. `State.Placement` holds GTK's
default size, which GTK 4 updates when the user resizes the window and keeps
while it is maximized, so it is the restored (unmaximized) size to save.
Restoring `DesktopWindowOptions.Placement` applies its size and maximized state,
without the `window-option-unsupported` error an explicit `X` and `Y` cause on
Wayland, so a placement saved with GTK 3 or on another machine still opens. On
X11 its position is restored too, kept on a monitor's work area as GTK 3 does; a
maximized placement opens maximized and restores to the saved position and size.
On Wayland the position cannot be restored: Wayland gives clients no global
coordinates, GTK 4 cannot tell which monitor the compositor will choose, so the
position is ignored and the restored size is fitted to the largest monitor, and
a placement saved on Wayland has no position. See
[Window state and restore](../Runic.Desktop/README.md#window-state-and-restore).

`DesktopHost.Monitors` lists GDK's monitors in logical pixels while
`DesktopEventLoop.Run(options.WithGtk4(), …)` runs, and watches them through the
monitor list's `items-changed` and each monitor's notifications. A monitor's `Id`
is its connector, such as `HDMI-1`, and its name GDK's description. Its `Scale`
is fractional from GTK 4.14 (`gdk_monitor_get_scale`) and the integer scale
factor before. X11 reports the work area without panels and the primary monitor;
Wayland reports neither, so there `WorkArea` equals `Bounds` and no monitor is
primary. `DesktopWindowState.MonitorId` is the monitor GDK places the window on.

`DesktopWindow.SetFullScreenAsync` and a placement saved full-screen work on X11
and Wayland, on the monitor of `DesktopWindowOptions.MonitorId` or the last
`MoveToMonitorAsync` (`gtk_window_fullscreen_on_monitor`), else where the window
is; X11 needs a window manager for full screen. Opening on a monitor and
`MoveToMonitorAsync` (`DesktopWindowCapabilities.MonitorPlacement`) need X11; on
Wayland `Validate` reports `MonitorId` ignored except for a full-screen placement,
and whether the compositor honours the monitor for full screen is its choice.
`DesktopWindowOptions.LayoutDirection` sets the direction of the window, its
header bar and the view, and `Language` WebKitGTK's preferred languages
(`navigator.language`, `Accept-Language`) for the window's own web context.

WebKitGTK's spell checker is on in every window, with enchant dictionaries for
`DesktopHostOptions.SpellCheckingLanguages`, and the host implements
`IDesktopSpellCheckingWindowHost` for `DesktopHost.SetSpellCheckingLanguagesAsync`.
The checker and its languages belong to the process, whichever window's context
sets them. See [Spell checking](../Runic.Desktop/README.md#spell-checking).

The window reports `DesktopWindowCapabilities.FileDrop` once it finds WebKit's
own drop target on the view. WebKit's target claims a drop, so a second target
would leave the page without its drop event; Runic instead adds GLib emission
hooks for WebKit's target, reads the drop's `GdkFileList` when the drag is
accepted, and delivers the paths when the drop happens, while WebKit performs
the drop as before. See [File drops](../Runic.Desktop/README.md#file-drops).
The window also reports `DesktopWindowCapabilities.FileDrag`:
`DesktopWindow.StartDragAsync` starts `gdk_drag_begin` with the paths as a
`GdkFileList` while the primary button is held; on X11, GTK 4 cancels such a
drag only when it is dropped where nothing accepts it, not on Escape. See
[Dragging files out](../Runic.Desktop/README.md#dragging-files-out).

The native smoke runs GTK4/WebKit6 under isolated X11: bridge JavaScript,
close veto/retry, sequential DesktopHost windows, capability reporting, GTK
window/WebView weak finalization, GTK clipboard round-trip, and X11 portal
parent export. JIT and NativeAOT execution both pass under the locked Nix
environment (GTK 4.22.4 / WebKitGTK 6 2.52.6). Real KDE and GNOME Wayland
checks also covered lifecycle, parent export, clipboard and live/cold notification
focus. GNOME usability checks on 2026-09-10 covered native accessible controls,
keyboard navigation and real Pinyin at 100%, 200% and 150% desktop scaling.
The standard-runtime NativeAOT Flatpak additionally passed unattended grant,
cancellation, atomic-save rejection and picker-owner closure checks.

Both GNOME and KDE pass automated Orca label/role speech, PipeWire audio capture,
local transcription, real Pinyin composition, 100/150/200% scaling with pointer
targeting, sandboxed file pickers and live/cold notification focus. The keyboard
checks also traverse every fixture control forward and backward, require native
AT-SPI focus events, and check text insertion events, values and caret positions.
The managed KDE runner also verifies GTK X11 clients on Xwayland, including
XIM Pinyin, Orca, 100/150/200% pointer targeting and sandboxed picker flows.
Announcement quality, visual candidate placement, standalone Xorg sessions and
broader distribution coverage remain follow-ups. See the
[container automation guide](https://docs.runic-artifex.eu/guides/desktop/container-automation/)
for reproducible commands and assertion limits. GTK3 remains supported.

The isolated package consumer checks base Desktop dependency isolation, a packaged
GTK4 window lifecycle under Xvfb, and (on NixOS) the missing-native-runtime error.
After packing the current `Runic.Desktop` and `Runic.Desktop.Gtk4` candidates into
`artifacts/packages/nuget`, run from the SDK root:

```sh
direnv exec . python3 tests/fixtures/desktop/Runic.Desktop.PackageConsumer/verify-gtk4.py --missing-runtime
```

The script creates and removes its own consumer outside the checkout. Omit
`--missing-runtime` on systems with globally installed GTK4: clearing
`LD_LIBRARY_PATH` there does not hide the native libraries. This lifecycle check
is not an X11 accessibility certification. Tested native versions above describe
the locked NixOS environment; they do not establish support for every distribution.
