# WPF navigation: Runic compared with Prism, ReactiveUI and CrissCross

The same four-scenario notes app was written with each library, then measured
for size and vocabulary and probed for what happens when Back is pressed
twice. The Runic app is [NotesNavigation](NotesNavigation/); the others, the
race probes and the counting script are in [comparison/](comparison/).

| Library | Version | Packages used |
| --- | --- | --- |
| Runic | 0.7.0-preview.4 | Runic.Navigation.Wpf (with Runic.Navigation), CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.DependencyInjection 10.0.12 |
| Prism | 9.0.537 | Prism.DryIoc (with Prism.Wpf and Prism.Core) |
| CrissCross | 5.0.0 | CrissCross.WPF (with ReactiveUI 26.0.1) |
| ReactiveUI RoutingState | 26.0.1 | ReactiveUI.WPF |
| Sextant | 4.0.30 | Not built: it has no WPF host (packages for netstandard2.0, MAUI and Avalonia only) |

All apps target .NET 10 on Windows and were measured on 2026-10-08. CrissCross
must target `net10.0-windows10.0.19041.0`; with `net10.0-windows` NuGet falls
back to its .NET Framework asset (NU1701).

## The scenarios

The apps share one domain: the notes and the store in
[comparison/shared/Domain.cs](comparison/shared/Domain.cs) (a copy in
`NotesNavigation/Domain/`), not counted.

1. **S1, master-detail with a guard.** A list opens a note by ID. Editing the
   title and leaving asks "Discard unsaved changes?"; Cancel stays with the
   edits, OK discards them.
2. **S2, a modal confirm with a result.** Delete asks "Delete X?" and deletes
   only on OK.
3. **S3, nested tabs.** A settings page with General and Advanced tabs; a
   button on General selects Advanced.
4. **S4, Back.** A shell Back button that is enabled while there is history.
   Its size barely differs between libraries, so it is compared by behaviour
   [below](#scenario-4-pressing-back-twice).

Each app is idiomatic for its library, following its documentation and
samples, and not golfed. Each uses the same ListBox, buttons and TextBox, and
plain property-change and command helpers: Prism's `BindableBase` and
`DelegateCommand`, ReactiveUI's `ReactiveObject` and `ReactiveCommand`, and
for Runic CommunityToolkit.Mvvm's `ObservableObject` and `RelayCommand`
without its source generators.

## Size

Counted by [comparison/count-loc.sh](comparison/count-loc.sh):

```sh
cd examples/wpf-navigation/comparison
./count-loc.sh ../NotesNavigation; ./count-loc.sh prism; ./count-loc.sh crisscross; ./count-loc.sh routingstate
```

The rules:

- **What counts:** non-blank lines of `.cs` and `.xaml`, except comment-only
  lines, `using` and `namespace` lines, and XAML lines that only declare an
  `xmlns`.
- **Which scenario:** a file's folder decides: `S1/`, `S2/` and `S3/`, which
  the Runic app names `Notes/`, `Dialogs/` and `Settings/`. A trailing
  `// [Sn]` or `<!-- [Sn] -->` moves one line of a shared file to that
  scenario. Other lines of `App.*` and `Shell/` are Setup.
- **Excluded:** the shared domain, `bin/` and `obj/`, and the Runic app's
  tests.
- Layout XAML that is the same in every app is still counted, so compare the
  differences rather than the totals.

Lines as C# / XAML / **total**:

| Part | Runic | Prism | CrissCross | ReactiveUI RoutingState |
| --- | --- | --- | --- | --- |
| Setup: bootstrap, shell, registrations | 33 / 8 / **41** | 24 / 9 / **33** | 29 / 27 / **56** | 29 / 8 / **37** |
| S1 master-detail with a guard | 52 / 16 / **68** | 70 / 16 / **86** | 65 / 18 / **83** | 67 / 18 / **85** |
| S2 confirm with a result | 35 / 12 / **47** | 29 / 11 / **40** | 24 / 11 / **35** | 24 / 11 / **35** |
| S3 nested tabs | 40 / 19 / **59** | 30 / 19 / **49** | 43 / 21 / **64** | 41 / 21 / **62** |
| S4 Back button | 0 / 1 / **1** | 2 / 1 / **3** | 1 / 1 / **2** | 5 / 1 / **6** |
| **Total** | **216** | **211** | **240** | **225** |

The totals are within about 15% of each other, so size alone doesn't separate
the libraries. Where the lines go does:

- **S1** is shortest in Runic, because `LeaveConfirmation.InDialog` is the
  guard, the dialog and the commit-bound discard in one call. The S1 lines also
  hide behaviour: Prism's guard covers every exit, CrissCross's only Back (its
  guard is synchronous, so an async confirm must cancel and re-issue the
  navigation), and RoutingState has no guard, so its app defines one that only
  its own Back command checks.
- **S2** is longest in Runic. The dialog is a ViewModel and View in a dialog
  region, the same as any page, and answers with
  `entry.CompleteAsync(true)`. ReactiveUI's `Interaction<string, bool>` with
  a handler that opens a window is shorter.
- **S3:** Prism's TabControl region and registered tab views take the least
  code. Runic's page owns a child region; selecting a tab replaces the region's
  entry, and the TabControl's content is a `NavigationHost`.
- **Setup:** Runic's `AppRegions` service, which holds the window's regions,
  is in Setup. CrissCross needs a NavigationWindow template that hosts its
  router.

## Concepts

The API names and conventions a reader meets in the four scenarios. This is a
judgement-based count, not a mechanical one; treat it as ±3.

| Library | Count | Concepts |
| --- | --- | --- |
| Runic | ~16 | `AddRunicWpfNavigation` with `UseViewNamingConvention`, `RunicNavigator.CreateRegion` and its owner, `NavigationRegion<T>` (`PushAsync<T>`, `ResetAsync`, `ReplaceAsync`, `Current`), child regions that close with their page, `NavigationHost` with `NavigationCommands.BrowseBack`, `NavigationDialogHost`, `NavigationTarget.Create` / `Borrow`, `INavigationInitialize<TInput>`, `NavigationEntryContext` (`CompleteAsync`, `DismissAsync`), `INavigationResume`, `INavigationDepartureGuard`, `LeaveConfirmation.InDialog`, `PushForResult<T>` with `NavigationCompletion<T>`, `ValueTask` returns; from outside Runic, Microsoft.Extensions.DependencyInjection and CommunityToolkit's `ObservableObject` and `RelayCommand` / `AsyncRelayCommand` |
| Prism | ~22 | `PrismApplication` (`CreateShell`, `RegisterTypes`), `IContainerRegistry`, `RegisterForNavigation`, `RegionManager.RegionName`, `IRegionManager`, `RequestNavigate`, `NavigationParameters`, `NavigationContext`, `IRegionAware`, `IConfirmNavigationRequest` with its continuation, `IRegionMemberLifetime` / `KeepAlive`, `IRegionNavigationJournal`, view reuse through `IsNavigationTarget`, `RegisterViewWithRegion`, the TabControl region adapter, `IDialogService`, `RegisterDialog`, `IDialogAware`, `DialogCloseListener`, `IDialogParameters` / `IDialogResult` / `ButtonResult`, `ShowDialogAsync`, `DelegateCommand` / `AsyncDelegateCommand`, `ViewModelLocator` conventions |
| CrissCross, with the ReactiveUI it builds on | ~20 | `RxAppBuilder` with `WithWpf` and `RegisterView`, Splat `AppLocator` and `SetupComplete`, `RxObject`, `NavigationWindow` with its `HostName` template, `ViewModelRoutedViewHost`, `NavigationKeyRequest<T>` and `NavigationRequestOptions.Parameter`, `NavigateToView` / `NavigateBack` / `CanNavigateBack`, `WhenNavigatedTo` / `WhenNavigating` / `WhenNavigatedFrom`, `ViewModelNavigatingEventArgs.Cancel`, ViewModel lifetimes (history stores types), `ReactiveUserControl<T>` and `DataContext` wiring, `WhenActivated` with `MultipleDisposable`, `ReactiveCommand`, `Interaction<TIn, TOut>`, `ViewModelViewHost`, `RxSchedulers.MainThreadScheduler`, `IViewFor`, `RxVoid` |
| ReactiveUI RoutingState | ~16, plus the app's own guard | `RxAppBuilder` with `WithWpf` and `RegisterView`, `IScreen`, `RoutingState` (`Navigate`, `NavigateBack`, `NavigationStack`), `IRoutableViewModel`, `RoutedViewHost`, `ViewModelViewHost`, `ReactiveUserControl<T>` and `DataContext` wiring, `IActivatableViewModel` with `WhenActivated`, `ReactiveCommand` and its CanExecute observable, `Interaction<TIn, TOut>`, awaiting an `IObservable` with `FirstAsync`, `MultipleDisposable` / `RxVoid`, and an app-defined leave-guard interface |

## Scenario 4: pressing Back twice

What happens when a second Back arrives before the first finishes: a fast
double click, code that calls Back twice, or a Back while the unsaved-changes
confirm is open. A modal confirm blocks a second click, but a non-modal or
overlay confirm, a keyboard or mouse Back button, or code doesn't.

How the evidence was gathered:

- **[T] tested:** Runic by the example's [scenario tests](Tests/ScenarioTests.cs)
  on a WPF dispatcher, and by the engine's
  [Back race tests](../../tests/dotnet/Runic.Navigation.Tests/BackRaceTests.cs).
  ReactiveUI and Prism by headless probes in
  [comparison/probes/](comparison/probes/), which run the libraries' own
  `RoutingState`, `ReactiveCommand` and `RegionNavigationJournal`. Prism's
  `RegionNavigationService` needs WPF, so the probe drives the real journal
  with a model of its request, confirm and commit order; the model is our own
  code, written from the source lines it cites. Run the probes with
  `dotnet run --project comparison/probes`.
- **[S] source reading only:** CrissCross, whose host needs a WPF window.

| Case | Runic | Prism 9.0.537 | ReactiveUI RoutingState 26.0.1 | CrissCross 5.0.0 [S] |
| --- | --- | --- | --- | --- |
| Two clicks on Back before the first Back finishes, 3 pages deep | One pop: Back can't execute from the moment the first is admitted [T] | Two pops: without a pending guard, Back commits inside the click [T, P1] | Two pops: CanExecute flips only after a dispatcher post [T, R2] | Two Backs read the same stack, which becomes `[List, A, A]`: a duplicate instead of a pop |
| Back twice from code | Before the first commits: one pop, and both calls get the same result. After it: a second pop. With no history left, Back is rejected with `NoHistory` [T] | Two pops [T, P1] | Two pops; at 2 pages deep the stack becomes empty and the next Back throws `ArgumentOutOfRangeException` [T, R4] | As for two clicks |
| Back while the unsaved-changes confirm is open | The second Back joins the first: one confirm, and its answer settles both [T] | Two confirms; answering the older one first corrupts the journal: B is lost and A is duplicated, silently [T, P3, P4] | No guard built in. A guarded Back command shows 2 confirms and pops twice when both clicks arrive before the dispatcher runs [T, R6] | The guard is synchronous; see S1 |
| The guard of a page reached by Back | Asked [T] | Asked | App-defined | Not asked: the host keeps the departed ViewModel as active after a Back |
| A page's arguments and edits when it is shown again | Kept: the history holds the entry, with its ViewModel and input [T] | Kept (`KeepAlive`) | Kept: the history holds ViewModel instances | Lost unless the ViewModel is a singleton: the history holds types |
| A confirm that is cancelled or superseded | The edits stay: the discard runs only when the departure commits [T] | The discard runs before the navigation commits | App-defined | App-defined |

A click after the first Back has finished is a new request, and every library,
Runic included, pops again, as a stack should. Whether a fast double click
lands before or after the first Back finishes depends on whether WPF runs the
posted navigation work before the second input; usually it does, so the first
row matters most for slow guards, resume hooks and keyboard repeat.

Sources, at the released versions:

- **Prism:** `RegionNavigationJournal.cs:69-78`, `:114-130`, `:142-149`
  (Prism.Core); `RegionNavigationService.cs:112`, `:130-150` (Prism.Wpf). Each
  `GoBack` sets `isNavigatingInternal` and resets it in its callback, so the
  callback of a superseded Back clears it while the next is still pending, and
  that one is recorded as a new navigation. The journal code is unchanged on
  the 10.0 preview branch. Prism's own `DialogService` uses
  `Window.ShowDialog`, which prevents the second click; a non-modal or
  overlay confirm, or code, doesn't.
- **ReactiveUI:** `ReactiveCommand{TParam,TResult}.cs:206-220`, `:500-515`
  (`Execute` doesn't check CanExecute, and CanExecute changes after a post to
  the output scheduler); `RoutingState.cs:224-250` (`NavigateBack` pops
  synchronously); `WpfReactiveUIBuilderExtensions.cs:26`, `:63` and
  ReactiveUI.Primitives `DispatcherSequencer.cs:161-165` (the WPF scheduler
  always posts).
- **CrissCross:** `ViewModelRoutedViewHost.cs:85-91`, `:89`, `:153`, `:301-331`,
  `:382-385`, `:433`, `:517-534`.

Runic's guarantees, from the
[Runic.Navigation README](../../packages/dotnet/Runic.Navigation/README.md):
a region admits one request at a time and sets `IsTransitioning`
synchronously, and the WPF host's Back checks it. A plain Back while another
plain Back is pending joins it. A Back with a cancellation token, such as the
ReactiveUI adapter's Back command, supersedes the pending one instead, so
cancelling one caller's Back can't cancel another's. A Back after the first
has committed is a new request and pops again.

## Where the others are ahead

- **Prism** has forward history, and `KeepAlive` keeps a page's View, so
  scroll position and selection survive Back. Runic keeps the ViewModel and
  builds a new View, so that state must live in the ViewModel. Prism's regions
  are also a general composition tool: a TabControl becomes a region with one
  attached property, which is why its S3 is shortest. Its dialog service is
  mature.
- **ReactiveUI** makes a typed question to the View cheap with
  `Interaction<TIn, TOut>`, the shortest S2 here. Its VM-first navigation,
  `Navigate.Execute(new NoteDetailViewModel(...))`, makes the constructor the
  parameter contract.
- **CrissCross** has page transitions, WebView2 integration and a Fluent UI
  kit. Runic.Navigation has none of these.
- **ReactiveUI and CrissCross** are mature, widely used projects; Runic's
  navigation API is experimental (`RUNICNAV001`).

## Licences and status

- **Prism** has a dual licence. The Community License is free only for
  organisations with annual gross revenue under USD 1M and less than USD 3M
  of outside capital; others need a commercial licence. The latest public
  NuGet release is 9.0.537, from 2024-08-20, with assets up to
  `net6.0-windows`; newer builds are on the paid Commercial Plus feed.
- **CrissCross** is MIT. 5.0.0 was released on 2026-10-05.
- **ReactiveUI** is MIT. 26.0.1 was released on 2026-10-04; version 26 moved
  to ReactiveUI.Primitives (`Unit` became `RxVoid`).
- **Sextant** is MIT, built against ReactiveUI 22.3.1, and has no WPF host.
- **Runic** is MIT.

The competitor apps in [comparison/](comparison/) are our own code written
against each library's public API; no library source is copied. They restore
from nuget.org with their own `NuGet.config` and pinned versions, outside
the repository's solution and CI:

```sh
cd examples/wpf-navigation/comparison
dotnet build prism/Comparison.Prism.csproj      # also crisscross/, routingstate/
dotnet run --project probes                     # the scenario 4 probes
```
