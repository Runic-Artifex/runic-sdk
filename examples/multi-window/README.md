# Multi-window project

Several Windows on one project, with CommunityToolkit.Mvvm ViewModels on Runic
Desktop. A command opens another Window, a rename in one Window shows in every
Window, and the app's theme setting is saved in a settings file and can follow
the desktop's color scheme.

| File | What it does |
| --- | --- |
| [`ProjectServices.cs`](ProjectServices.cs) | Registers the shared singletons, the per-Window model context and ViewModel, `AddRunicPlatformServices()` and `AddRunicWindowService()`. |
| [`ProjectViewModel.cs`](ProjectViewModel.cs) | Opens a Window with `IRunicWindowService`, applies renames from other Windows with `TryPost`, and watches `IDesktopSettings`. |
| [`ProjectService.cs`](ProjectService.cs) | The shared, thread-safe project title, with an event after each rename. |
| [`SettingsStore.cs`](SettingsStore.cs) | The app's own settings, in a JSON file in the per-user application data folder, written atomically. |
| [`Program.cs`](Program.cs) | Opens the first Window; the application ends when it closes. |
| [`Tests/ProjectWindowTests.cs`](Tests/ProjectWindowTests.cs) | Drives two Windows without a browser, with a fake window service, a temporary settings file and fixed desktop preferences. |

Each Window opens in its own DI scope, so it has its own ViewModel and model
context. Two Windows can run commands at the same moment on two threads, so
the singletons guard their state, and each ViewModel commits changes that come
from other Windows, or from the desktop, in a turn of its own context.
`IRunicWindowService` is a singleton; it releases each Window it opened, with
its scope, once the user closes it.

```sh
dotnet run --project examples/multi-window -c Release
dotnet run --project examples/multi-window/Tests -c Release
```

The example builds the Runic packages from source; replace its
`ProjectReference` and `Import` lines with a `Runic.Application.Views.Desktop`
package reference to copy it out.
