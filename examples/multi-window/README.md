# Multi-window project

Several Windows on one project, with ReactiveUI ViewModels on Runic Desktop. A command opens another Window, a rename in one Window shows in every
Window, and the app's theme setting is saved in a settings file and can follow
the desktop's color scheme. The status line and the title's validation message
are shown in English or German, and switching the language changes them without
a call per string.

| File | What it does |
| --- | --- |
| [`ProjectServices.cs`](ProjectServices.cs) | Registers the shared singletons, the per-Window model context with `AddRunicReactiveModelContext()`, the ViewModel, `AddRunicPlatformServices()` and `AddRunicWindowService()`. |
| [`ProjectViewModel.cs`](ProjectViewModel.cs) | A `ReactiveObject` whose `ReactiveCommand`s take no scheduler. Opens a Window with `IRunicWindowService`, applies renames from other Windows with `TryPost`, and watches `IDesktopSettings`. |
| [`ProjectText.cs`](ProjectText.cs) | The status line and validation message as Runic Translations text references (`TranslationReference`): a key, typed arguments and an English fallback. |
| [`Frontend/src/translations.ts`](Frontend/src/translations.ts) | The page's English and German messages for those keys, passed to `configureTranslatedText`. |
| [`ProjectService.cs`](ProjectService.cs) | The shared, thread-safe project title, with an event after each rename. |
| [`SettingsStore.cs`](SettingsStore.cs) | The app's own settings, in a JSON file in the per-user application data folder, written atomically. |
| [`Program.cs`](Program.cs) | Opens the first Window; the application ends when it closes. |
| [`Tests/ProjectWindowTests.cs`](Tests/ProjectWindowTests.cs) | Drives two Windows without a browser, with a fake window service, a temporary settings file and fixed desktop preferences. |

Each Window opens in its own DI scope, so it has its own ViewModel and model
context. Two Windows can run commands at the same moment on two threads, so
the singletons guard their state, and each ViewModel commits changes that come
from other Windows, or from the desktop, in a turn of its own context.
The ViewModel sets the Window's language with `IRunicWindowLocale`, from the
language saved in the settings file, and again when the page's language list
calls `ChooseLanguage`. The page follows it: `resolveText` shows the `status`
member and the `text` of the title's validation error in that language, and
`subscribeText` renders again after a switch. An application generates both
sides from one Runic Translations catalog and passes the generated
`resolveTextReference` instead of the example's table; see
[Translated text](../../packages/dotnet/Runic.Application.Views/README.md#translated-text).

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
