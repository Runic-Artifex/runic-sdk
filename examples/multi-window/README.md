# Multi-window project

Several Windows on one project, with ReactiveUI ViewModels on Runic Desktop. A command opens another Window, a
document opens in a Window of its own or brings the open one to the front, a window-modal "choose account" dialog
returns the account chosen, a rename in one Window shows in every
Window, and the app's theme setting is saved in a settings file, switches every
Window between light and dark at once, and can follow the desktop's color scheme.
The application is translated into English, German and Arabic with one
[Runic Translations](https://github.com/Runic-Artifex/runic-translations-sdk)
catalog for the page and C#: the status line, the title's validation message,
the page's own labels and the native menu bar follow one application-wide
language, and Arabic lays the page out right to left.

| File | What it does |
| --- | --- |
| [`ProjectServices.cs`](ProjectServices.cs) | Registers the shared singletons, the per-Window model context with `AddRunicReactiveModelContext()`, the ViewModel, `AddRunicPlatformServices()` and `AddRunicWindowService()`. |
| [`ProjectViewModel.cs`](ProjectViewModel.cs) | A `ReactiveObject` whose `ReactiveCommand`s take no scheduler. Opens a Window with `IRunicWindowService`, applies renames from other Windows with `TryPost`, and watches `IDesktopSettings`. |
| [`Frontend/translations`](Frontend/translations) | The catalog: `runic.json` and the `en`, `de` and `ar` messages. `MultiWindow.csproj` names it, so the build generates `ProjectText` (the C# messages, the `References` factories and `ProjectTextCatalog.Registration`) and the page's ESM, which it copies to `Frontend/.runic/translations`. The build restores the `dotnet-runic-translations` tool of the repository's [`.config/dotnet-tools.json`](../../.config/dotnet-tools.json) for the ESM. |
| [`Frontend/src/app.ts`](Frontend/src/app.ts) | Connects the generated runtime with `configureTranslatedText`, `configureSharedLocaleResolver` and `syncDocumentLocale`, shows the ViewModel's references with `resolveText` and its own labels with `m.*()`. |
| [`DocumentWindow.cs`](DocumentWindow.cs) | A document Window. `ProjectViewModel.OpenDocumentCommand` opens it with `IRunicWindowService.FindOrOpenAsync`, keyed by its path, so a second request focuses it. |
| [`AccountWindow.cs`](AccountWindow.cs) | The "choose account" dialog. `ProjectViewModel.ChooseAccountCommand` opens it with `OpenForResultAsync`, owned by the project Window and window-modal to it; its ViewModel completes `IRunicWindowResult<string>`, and Cancel returns no result. |
| [`ProjectService.cs`](ProjectService.cs) | The shared, thread-safe project title, with an event after each rename. |
| [`SettingsStore.cs`](SettingsStore.cs) | The app's own settings, in a JSON file in the per-user application data folder, written atomically, with an event after each save. |
| [`Program.cs`](Program.cs) | Starts the language (`ProjectServices.StartLanguageAsync`: `RUNIC_LOCALE`, else the saved choice, else the operating system's languages), sets the translated menu bar again after each switch, applies the saved theme with `DesktopHost.SetColorSchemeAsync`, and each new choice as it is saved, then opens the first Window; the application ends when it closes. |
| [`Tests/ProjectWindowTests.cs`](Tests/ProjectWindowTests.cs) | Drives two Windows without a browser, with a fake window service, a temporary settings file and fixed desktop preferences, and checks the text a user sees by resolving the ViewModel's references with the application's `TranslationContext` in German and Arabic. |
| [`browser-smoke.mjs`](browser-smoke.mjs) | Runs the built application serve-only with `RUNIC_LOCALE=de-CH` in Chromium, checks the German text and the catalog's language list, switches to Arabic on the page and checks the right-to-left layout. CI runs it. |
| [`Tests/DocumentAndAccountWindowTests.cs`](Tests/DocumentAndAccountWindowTests.cs) | Opens the project Window with `RunicWindowTestHost.CreateWindowAsync`, so the real window service opens the document and account Windows headless: one Window per document, and a dialog that blocks the project and returns its choice. |

Each Window opens in its own DI scope, so it has its own ViewModel and model
context. Two Windows can run commands at the same moment on two threads, so
the singletons guard their state, and each ViewModel commits changes that come
from other Windows, or from the desktop, in a turn of its own context.
The language is the application's (`IRunicApplicationLocale`), and the C#
catalog's `TranslationContext` follows it from its registration on.
`StartLanguageAsync` sets it at start, and `ChooseLanguageCommand`, which the
page's language list and the menu bar's Language items run, switches it and
then saves the choice. Both match a tag to the catalog's languages (`de-CH`
is `de`) and skip one that matches none, so only the catalog's languages are
saved and a mistyped `RUNIC_LOCALE` or setting never stops the start. The
page's list and the menu show the catalog's languages, from `runic.json`. A switch reaches the C#
catalog first, then every Window, then `LocaleChanged`, where `Program.cs` sets
the menu bar again in the new language. The page follows its Window: `resolveText`
shows the `status` member and the `text` of the title's validation error, `m.*()`
its labels, and `syncDocumentLocale` sets `<html lang dir>`. See
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
RUNIC_LOCALE=ar dotnet run --project examples/multi-window -c Release   # start in Arabic
dotnet run --project examples/multi-window/Tests -c Release
node examples/multi-window/browser-smoke.mjs                            # after a Release build
```

The example builds the Runic packages from source; replace its
`ProjectReference` and `Import` lines with `Runic.Application.Views.Desktop` and
`Runic.Application.Views.ReactiveUI` package references to copy it out, and run
`dotnet tool restore` once instead of its `RestoreTranslationsTool` target. A
page built with Vite uses the Translations Vite plugin instead of the ESM the
.NET build writes, as `dotnet new runic-app --translations` does.
