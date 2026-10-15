# Status icon

An application that starts without a window: only its icon in the desktop's
status area. A click on the icon, or **Open window** in its menu, opens the
counter from [First Window on Runic Desktop](../first-window-desktop/README.md);
closing the window leaves the application running, and **Quit** ends it.
**Start at login** starts it again after the next login, and launching it while
it runs brings the counter forward instead of starting a second copy.

| File | What it does |
| --- | --- |
| [`Program.cs`](Program.cs) | Claims the single instance, takes `IDesktopStatusIcon` and `IDesktopLoginStartup` from `AddRunicPlatformServices(id)`, shows the icon, and opens, reuses or presents the Window as activations and launches arrive. |
| [`CounterViewModel.cs`](CounterViewModel.cs), [`CounterWindow.cs`](CounterWindow.cs) | The counter Window, unchanged from First Window on Runic Desktop. |

```sh
dotnet run --project examples/status-icon -c Release
```

`ApplicationInstance.ClaimAsync` makes the first launch the primary instance. A
later launch forwards its arguments, working directory and activation token, and
exits; the primary instance raises `IApplicationInstance.Launched`, and
`PlatformServices.PresentAsync` brings the open Window forward with that token.

`IDesktopStatusIcon.Activated` and `Launched` are raised on worker threads. The
sample hands each to the host's application loop through a channel, so Windows
open and close only there. The application lives as long as that loop: closing
the last Window does not end it. The sample sets
`DesktopHostOptions.HandlesShutdownWithoutWindow`, so Ctrl+C, SIGTERM
(`systemctl --user stop`, logging out) and SIGHUP cancel
`host.Desktop.ShutdownRequested` also while no window is open. That completes
the channel, so the loop ends as **Quit** does: the counter closes, the icon is
hidden and the single-instance claim is released.

On Linux the icon is a StatusNotifierItem, shown by Plasma, Waybar's tray, or
GNOME with the AppIndicator extension. Without a status area, `ShowAsync`
reports unavailable and the icon appears once one starts. Login startup writes
an XDG autostart entry, or asks the Background portal inside Flatpak or Snap.
On Windows the icon is a notification-area icon (`Shell_NotifyIcon`). The
sample's state names only a theme icon, which Windows ignores, so the icon is
the application's own; supply RGBA images in `DesktopIcon.Images` for your own.
Login startup uses the user's Run key. A second launch lets the running instance
take the foreground before it forwards; if Windows still refuses, the
counter's taskbar button flashes.

On macOS the icon is an `NSStatusItem` in the menu bar. The sample's icon names
only a theme icon, which macOS lacks, so the menu bar shows its title as text;
supply `Images` to show an icon. A click opens the counter, and a right click or
a Control-click opens the menu. Without a bundle the sample runs without a Dock
icon until it opens the counter; a bundle that never shows one sets
`LSUIElement` (see [Runic.Platform.MacOS](../../packages/dotnet/Runic.Platform.MacOS/README.md#status-icon)).
**Start at login** needs a signed application bundle and macOS 13. Under
`dotnet run`, login startup reports unavailable and the item changes nothing. A
second launch brings the counter forward without an activation token.
