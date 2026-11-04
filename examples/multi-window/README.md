# Multi-window project

Several Windows on one project, with ReactiveUI ViewModels on Runic Desktop. A command opens another Window, a
document opens in a Window of its own or brings the open one to the front, a window-modal "choose account" dialog
returns the account chosen, a rename in one Window shows in every
Window, and the app's theme setting is saved in a settings file, switches every
Window between light and dark at once, and can follow the desktop's color scheme.

| File | What it does |
| --- | --- |
| [`ProjectServices.cs`](ProjectServices.cs) | Registers the shared singletons, the per-Window model context with `AddRunicReactiveModelContext()`, the ViewModel, `AddRunicPlatformServices()` and `AddRunicWindowService()`. |
| [`ProjectViewModel.cs`](ProjectViewModel.cs) | A `ReactiveObject` whose `ReactiveCommand`s take no scheduler. Opens a Window with `IRunicWindowService`, applies renames from other Windows with `TryPost`, and watches `IDesktopSettings`. |
| [`DocumentWindow.cs`](DocumentWindow.cs) | A document Window. `ProjectViewModel.OpenDocumentCommand` opens it with `IRunicWindowService.FindOrOpenAsync`, keyed by its path, so a second request focuses it. |
| [`AccountWindow.cs`](AccountWindow.cs) | The "choose account" dialog. `ProjectViewModel.ChooseAccountCommand` opens it with `OpenForResultAsync`, owned by the project Window and window-modal to it; its ViewModel completes `IRunicWindowResult<string>`, and Cancel returns no result. |
| [`ProjectService.cs`](ProjectService.cs) | The shared, thread-safe project title, with an event after each rename. |
| [`SettingsStore.cs`](SettingsStore.cs) | The app's own settings, in a JSON file in the per-user application data folder, written atomically, with an event after each save. |
| [`Program.cs`](Program.cs) | Applies the saved theme with `DesktopHost.SetColorSchemeAsync`, and each new choice as it is saved, then opens the first Window; the application ends when it closes. |
| [`Tests/ProjectWindowTests.cs`](Tests/ProjectWindowTests.cs) | Drives two Windows without a browser, with a fake window service, a temporary settings file and fixed desktop preferences. |
| [`Tests/DocumentAndAccountWindowTests.cs`](Tests/DocumentAndAccountWindowTests.cs) | Opens the project Window with `RunicWindowTestHost.CreateWindowAsync`, so the real window service opens the document and account Windows headless: one Window per document, and a dialog that blocks the project and returns its choice. |

Each Window opens in its own DI scope, so it has its own ViewModel and model
context. Two Windows can run commands at the same moment on two threads, so
the singletons guard their state, and each ViewModel commits changes that come
from other Windows, or from the desktop, in a turn of its own context.
`IRunicWindowService` is a singleton; it releases each Window it opened, with
its scope, once the user closes it. `AddRunicReactiveModelContext()` makes each
`ReactiveCommand` deliver its notifications on the model context of the Window
that runs it, so the commands are created without a scheduler argument, and
the generated Bridge logs failures a command does not declare.
The [template](https://docs.runic-artifex.eu/getting-started/) starts with the
same ReactiveUI setup; CommunityToolkit.Mvvm ViewModels work the same way, as in
the [CommunityToolkit Notes](../notes-view-first/README.md) example.

```sh
dotnet run --project examples/multi-window -c Release
dotnet run --project examples/multi-window/Tests -c Release
```

The example builds the Runic packages from source; replace its
`ProjectReference` and `Import` lines with `Runic.Application.Views.Desktop` and
`Runic.Application.Views.ReactiveUI` package references to copy it out.
