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
  tabs. `NavigationSelector.Region` connects the TabControl to borrowed
  replacement, restores selection on rejection, and supplies the entry host.
  The region closes with the page.
- **Back** (`Shell/MainWindow.xaml`). The Back button sends
  `NavigationCommands.BrowseBack` to the `NavigationHost`, which can't execute
  while a Back is in flight.

[NotesNavigation.ReactiveUI](#the-reactiveui-variant) is the same app with
ReactiveUI ViewModels and the Runic.Navigation.ReactiveUI adapter.

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
edits. Cancelling the native Toolkit Delete command must close its prompt,
keep the note, and ignore a late answer.

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
`NotesNavigation.csproj` with the following. Runic.Navigation.Wpf is available
from 0.7.0-preview.4.

```xml
<PackageReference Include="Runic.Navigation.Wpf" Version="0.7.0-preview.4" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.12" />
```

`NotesNavigation/Domain/` is the notes store the comparison apps share; it
stands in for your own model.

## The ReactiveUI variant

[NotesNavigation.ReactiveUI/](NotesNavigation.ReactiveUI/) is the same app
for a ReactiveUI team. It uses the same regions and hosts, and
[Runic.Navigation.ReactiveUI](../../packages/dotnet/Runic.Navigation.ReactiveUI/README.md)
where ReactiveUI has its own idiom:

- **The leave guard asks through an `Interaction`.** `NoteDetailViewModel`
  builds its guard with `ReactiveLeaveConfirmation.WithInteraction`, and
  `NoteDetailView` answers `ConfirmDiscard` with a `MessageBox` in
  `WhenActivated`. As with `LeaveConfirmation.InDialog`, the discard runs
  only when the departure commits.
- **The confirm answers with result commands.** `ConfirmViewModel` takes its
  `NavigationEntryContext` and its message through its constructor. Its Yes
  is `entry.CreateCompleteCommand(...)` and its No is
  `entry.CreateDismissCommand()`. Delete awaits the `Completion` of
  `PushForResult<bool>` with the command's token, so disposing a Delete
  execution dismisses the prompt.
- **The list reloads in `WhenNavigatedTo`.** Its commands are
  `ReactiveCommand`s whose `CanExecute` observes the bound selection.
- **No scheduler in user code.** `RxAppBuilder` with `WithWpf()` makes the
  dispatcher ReactiveUI's main-thread scheduler, and the dispatcher is also
  the navigator's model context.

The files that don't depend on the MVVM library are linked from
`NotesNavigation/` rather than copied: the domain, `Shell/`, `ConfirmView`
and the settings Views. The two apps differ only where the libraries do.
`NoteDetailView` derives from a one-line `NoteDetailViewBase`. XAML in a
`Runic.Navigation.*` namespace resolves `ReactiveUI` to
`Runic.Navigation.ReactiveUI`, so it can't name `ReactiveUserControl<T>`.

[Tests.ReactiveUI/](Tests.ReactiveUI/) reuses the harness in `Tests/` and
answers the interaction with its own handler. CI runs it on Windows from
source:

```sh
dotnet run --project examples/wpf-navigation/NotesNavigation.ReactiveUI
dotnet run --project examples/wpf-navigation/Tests.ReactiveUI -c Release
```

In your own project, reference the packages instead of the projects:

```xml
<PackageReference Include="Runic.Navigation.Wpf" Version="0.7.0-preview.4" />
<PackageReference Include="Runic.Navigation.ReactiveUI" Version="0.7.0-preview.4" />
<PackageReference Include="ReactiveUI.WPF" Version="26.0.1" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.12" />
```

## Native Toolkit commands

Navigation uses one shared engine. Toolkit commands use its task-based API
directly; presentation hosts and MVVM frameworks are independent choices.
The list's `AsyncRelayCommand` delegates accept and forward cancellation tokens,
so `DeleteCommand.Cancel()` dismisses its pending result prompt without deleting.
The same convention works with Toolkit's source generator:

```csharp
[RelayCommand(IncludeCancelCommand = true)]
private async Task OpenSettingsAsync(CancellationToken token)
{
    var result = await regions.Main.PushAsync<SettingsViewModel>(cancellationToken: token);
    HandleNavigationOutcome(result);
}
```

`HandleNavigationOutcome` is application policy: handle `Rejected`, `Failed` and
`Superseded` as well as `Committed`. Navigation cancellation is a core result,
not an exception. Forward tokens to `PushForResult` too; they dismiss a prompt
even after its push commits. Toolkit's generated commands remain native Toolkit
commands when the ViewModel is later presented through Runic's web bridge.
