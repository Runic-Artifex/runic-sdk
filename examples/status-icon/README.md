# Status icon

An application that starts without a window: only its icon in the desktop's
status area. A click on the icon, or **Open window** in its menu, opens the
counter from [First Window on Runic Desktop](../first-window-desktop/README.md);
closing the window leaves the application running, and **Quit** ends it.

| File | What it does |
| --- | --- |
| [`Program.cs`](Program.cs) | Takes `IDesktopStatusIcon` from `AddRunicPlatformServices()`, shows the icon, and opens or reuses the Window as activations arrive. |
| [`CounterViewModel.cs`](CounterViewModel.cs), [`CounterWindow.cs`](CounterWindow.cs) | The counter Window, unchanged from First Window on Runic Desktop. |

```sh
dotnet run --project examples/status-icon -c Release
```

`IDesktopStatusIcon.Activated` is raised on a worker thread. The sample hands
each activation to the host's application loop through a channel, so Windows
open and close only there.

On Linux the icon is a StatusNotifierItem, shown by Plasma, Waybar's tray, or
GNOME with the AppIndicator extension. Without a status area, `ShowAsync`
reports unavailable and the icon appears once one starts. Windows and macOS have
no provider yet: the application runs, but shows no icon.
