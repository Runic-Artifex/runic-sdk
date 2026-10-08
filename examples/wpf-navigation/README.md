# WPF navigation

A plain WPF notes app that navigates with
[Runic.Navigation.Wpf](../../packages/dotnet/Runic.Navigation.Wpf/README.md)
and nothing else from Runic: no Views, web View, generator or build assets.
Its other packages are CommunityToolkit.Mvvm, used without its source
generators, and Microsoft.Extensions.DependencyInjection.

It covers four scenarios, each in its own folder:

- **Master-detail with a guard** (`Notes/`). The list opens a note by ID with
  `PushAsync<NoteDetailViewModel, int>`. Leaving a note with unsaved edits asks
  in a dialog through `LeaveConfirmation.InDialog`; the edits are discarded
  only when the departure commits.
- **A confirm with a result** (`Dialogs/`). Delete pushes `ConfirmViewModel`
  into the dialog region with `PushForResult<bool>` and deletes only on
  `Completed(true)`. The dialog is a ViewModel and a View like any page.
- **Nested tabs** (`Settings/`). The settings page owns a child region for its
  tabs. Selecting a tab replaces the region's entry, and the region closes
  with the page.
- **Back** (`Shell/MainWindow.xaml`). The Back button sends
  `NavigationCommands.BrowseBack` to the `NavigationHost`, which can't execute
  while a Back is in flight.

[COMPARISON.md](COMPARISON.md) measures the same app written with Prism,
ReactiveUI and CrissCross: lines, concepts, and what each does when Back is
pressed twice. The [Runic.Navigation.Wpf quick start](../../packages/dotnet/Runic.Navigation.Wpf/README.md#quick-start)
walks through the same code.

## Run it

On Windows, from the repository root:

```sh
dotnet run --project examples/wpf-navigation/NotesNavigation
```

## Tests

[Tests/](Tests/) starts the app's own container and window off-screen on an
STA thread and drives it: typing, clicking Back, and answering the dialog
windows. Besides the four scenarios, it checks the failure cases the
comparison found in other libraries: two Backs from code, Back while the
confirm is open, the guard of a page reached by Back, a page keeping its
argument and edits when it is shown again, and a superseded Back keeping the
edits.

```sh
dotnet run --project examples/wpf-navigation/Tests -c Release
```

WPF runs only on Windows; on Linux and macOS the projects build. CI builds and
runs the tests on Windows against the packed packages, the way an app
consumes them:

```sh
bun run pack
bun examples/wpf-navigation/package-smoke.mjs
```

## Copy it into your own project

Inside this repository the app references Runic.Navigation.Wpf as a project.
In your own project, replace the conditional references in
`NotesNavigation.csproj` with:

```xml
<PackageReference Include="Runic.Navigation.Wpf" Version="0.7.0-preview.4" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.12" />
```

`NotesNavigation/Domain/` is the notes store the comparison apps share; it
stands in for your own model.
