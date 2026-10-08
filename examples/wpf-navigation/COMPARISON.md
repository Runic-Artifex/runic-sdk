# WPF navigation: Runic compared with Prism, ReactiveUI and CrissCross

The same four-scenario notes app was written with each library, then measured
for size and vocabulary and probed for what happens when Back is pressed
twice. The Runic app is [NotesNavigation](NotesNavigation/); the others, the
race probes and the counting script are in [comparison/](comparison/).

| Library | Version | Packages used |
| --- | --- | --- |
| Runic | 0.7.0-preview.4 (Runic.Navigation.Wpf is available from this release) | Runic.Navigation.Wpf (with Runic.Navigation), CommunityToolkit.Mvvm 8.4.2, Microsoft.Extensions.DependencyInjection 10.0.12 |
| Prism | 9.0.537 | Prism.DryIoc (with Prism.Wpf and Prism.Core) |
| CrissCross | 5.0.0 | CrissCross.WPF (with ReactiveUI 26.0.1) |
| ReactiveUI RoutingState | 26.0.1 | ReactiveUI.WPF |
| Sextant | 4.0.30 | Not built: a platform-neutral core plus MAUI and Avalonia hosts, and no WPF host |

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
   It takes 1 to 7 lines, so it is compared mainly by behaviour
   [below](#scenario-4-pressing-back-twice).

Each app is idiomatic for its library, following its documentation and
samples, and not golfed. Each uses the same ListBox, buttons and TextBox, and
plain property-change and command helpers: Prism's `BindableBase` and
`DelegateCommand`, ReactiveUI's `ReactiveObject` and `ReactiveCommand`, and
for Runic CommunityToolkit.Mvvm's `ObservableObject` and `RelayCommand`
without its source generators. In every app Open and Delete are disabled
without a selection, and Back is disabled without history. The CrissCross
shell's window template follows upstream's `SplitNavigationWindowStyle`
sample.

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
| Setup: bootstrap, shell, registrations | 33 / 8 / **41** | 26 / 9 / **35** | 29 / 27 / **56** | 29 / 8 / **37** |
| S1 master-detail with a guard | 52 / 16 / **68** | 64 / 16 / **80** | 69 / 18 / **87** | 70 / 18 / **88** |
| S2 confirm with a result | 35 / 12 / **47** | 29 / 11 / **40** | 25 / 11 / **36** | 25 / 11 / **36** |
| S3 nested tabs | 48 / 19 / **67** | 30 / 19 / **49** | 43 / 21 / **64** | 41 / 21 / **62** |
| S4 Back button | 0 / 1 / **1** | 6 / 1 / **7** | 1 / 1 / **2** | 5 / 1 / **6** |
| **Total** | **224** | **211** | **245** | **229** |

The totals are within about 15% of each other, so size alone doesn't separate
the libraries. Where the lines go does:

- **S1** is shortest in Runic, because `LeaveConfirmation.InDialog` is the
  guard, the dialog and the commit-bound discard in one call. The S1 lines
  also hide behaviour. Prism's guard covers every exit. CrissCross's guard is
  synchronous, so an async confirm must cancel and re-issue the navigation;
  this app re-issues only Back. RoutingState has no guard, so its app defines
  one that only its own Back command checks. The ReactiveUI-based apps also
  bind the selection to a property that Open and Delete act on, because
  `ReactiveCommand`'s CanExecute is an observable rather than a function of a
  command parameter.
- **S2** is longer in Runic than in the others. The dialog is a ViewModel and
  View in a dialog region, the same as any page, and answers with
  `entry.CompleteAsync(true)`. ReactiveUI's `Interaction<string, bool>` with a
  handler that opens a window is shorter.
- **S3** is longest in Runic, and Prism's is shortest: its TabControl is a
  region and the tabs are registered views. Runic's page owns a child region;
  selecting a tab replaces the region's entry, a rejected selection must
  re-sync the TabControl, and the TabControl's content is a `NavigationHost`.
  The child region pays off only when the tabs have guards or lifetimes of
  their own; for plain tabs it is ceremony. Runic has no TabControl or
  selector adapter, and no host for plain content without navigation, yet;
  both are planned (W240-015).
- **S4:** Prism's shell re-queries its Back command on each navigation, so
  that it is disabled without history.
- **Setup:** Runic's `AppRegions` service, which holds the window's regions,
  is in Setup. CrissCross needs a NavigationWindow template that hosts its
  router.

## Concepts

The API names and conventions a reader meets in the four scenarios, counting
the property-change base class and command types in every column. This is a
judgement-based grouping: each item separated by a semicolon counts once, and
only what the app uses is listed. Treat the counts as ±3.

| Library | Count | Concepts |
| --- | --- | --- |
| Runic | 18 | `AddRunicWpfNavigation` with `UseViewNamingConvention`; `RunicNavigator.CreateRegion` and its owner; `NavigationRegion<T>` (`PushAsync<T>`, `ResetAsync`, `ReplaceAsync`, `Current`, `PropertyChanged`); child regions that close with their page; `NavigationHost` with `NavigationCommands.BrowseBack`; `NavigationDialogHost`; `NavigationTarget.Create` / `Borrow`; `INavigationInitialize<TInput>`; `NavigationEntryContext` (`CompleteAsync`, `DismissAsync`); `INavigationResume`; `INavigationDepartureGuard`; `LeaveConfirmation.InDialog`; `PushForResult<T>` with `NavigationCompletion<T>`; `NavigationResult<T>`; `ValueTask` returns; Microsoft.Extensions.DependencyInjection; CommunityToolkit's `ObservableObject` with `SetProperty`; `RelayCommand` / `AsyncRelayCommand` |
| Prism | 25 | `PrismApplication` (`CreateShell`, `RegisterTypes`, `OnInitialized`); `IContainerRegistry`; `RegisterForNavigation`; `RegionManager.RegionName`; `IRegionManager` (`Regions`, `ContainsRegionWithName`); `RequestNavigate`; `NavigationParameters`; `NavigationContext`; `IRegionAware` (`OnNavigatedTo`, `OnNavigatedFrom`); `IConfirmNavigationRequest` with its continuation; `IRegionMemberLifetime` / `KeepAlive`; `IRegionNavigationJournal` (`GoBack`, `CanGoBack`); `IRegionNavigationService.Navigated`; view reuse through `IsNavigationTarget`; `RegisterViewWithRegion`; the TabControl region adapter with a `TabItem` header bound through `DataContext`; `IDialogService.ShowDialog` with its callback; `ShowDialogAsync`; `RegisterDialog`; `IDialogAware`; `DialogCloseListener`; `DialogParameters` / `IDialogParameters` with `ButtonResult`; `BindableBase` with `SetProperty`; `DelegateCommand` / `AsyncDelegateCommand` with `RaiseCanExecuteChanged`; `ViewModelLocator.AutoWireViewModel` |
| CrissCross, with the ReactiveUI it builds on | 17 | `RxAppBuilder` with `WithWpf` and `RegisterView`; Splat `AppLocator` and `SetupComplete`; ViewModel lifetimes (`RegisterLazySingleton` vs `Register`, because history stores types); `RxObject` with `RaiseAndSetIfChanged`; `WhenAnyValue`; `NavigationWindow` with its `HostName` template; `ViewModelRoutedViewHost`; `NavigationKeyRequest<T>` with `NavigationRequestOptions.Parameter`; `NavigateToView` / `NavigateBack` / `CanNavigateBack`; `WhenNavigatedTo` / `WhenNavigating`; `IViewModelNavigatingEventArgs.Cancel`; `ReactiveUserControl<T>` and `DataContext` wiring; `WhenActivated` with `MultipleDisposable`; `ReactiveCommand`; `Interaction<TIn, TOut>` with `RegisterHandler` and `Handle`; `ViewModelViewHost`; `RxVoid` |
| ReactiveUI RoutingState | 15, plus the app's own guard | `RxAppBuilder` with `WithWpf` and `RegisterView`; `IScreen`; `RoutingState` (`Navigate`, `NavigateBack`, `CanNavigateBack`, `GetCurrentViewModel`); `IRoutableViewModel` (`HostScreen`, `UrlPathSegment`); `RoutedViewHost`; `ViewModelViewHost`; `ReactiveObject` with `RaiseAndSetIfChanged`; `WhenAnyValue`; `ReactiveUserControl<T>` and `DataContext` wiring; `IActivatableViewModel` with `ViewModelActivator` and `WhenActivated`; `ReactiveCommand` and its CanExecute observable; `Interaction<TIn, TOut>` with `RegisterHandler` and `Handle`; awaiting an `IObservable` with `FirstAsync`; `MultipleDisposable`; `RxVoid`. Plus the app-defined leave-guard interface |

## Scenario 4: pressing Back twice

What happens when a second Back arrives while the first is under way: a fast
double click, code that calls Back twice, or a Back while the unsaved-changes
confirm is open.

How each library answers this is partly a design choice. Prism's and
RoutingState's Back commits synchronously inside the click when no guard is
pending, so there is no "while the first is under way" and each click is a
complete Back. Runic's navigation is asynchronous, so it disables Back from
the moment a Back is admitted and lets a second Back join the first.
Both are consistent; the table shows the consequences.

Every app shows its confirm modally (Prism's `DialogService` and the
ReactiveUI and CrissCross apps use `Window.ShowDialog`; Runic's `NavigationDialogHost` is
application-modal by default), so a second click can't reach Back while the
confirm is open. The confirm rows apply to a non-modal or overlay confirm, a
keyboard or mouse Back button, or code.

How the evidence was gathered:

- **[T] tested:** Runic by the example's [scenario tests](Tests/ScenarioTests.cs)
  on a WPF dispatcher, and by the engine's
  [Back race tests](../../tests/dotnet/Runic.Navigation.Tests/BackRaceTests.cs).
  ReactiveUI by a headless probe in [comparison/probes/](comparison/probes/)
  that runs its own `RoutingState` and `ReactiveCommand`, with the WPF
  dispatcher replaced by a posting queue.
- **[M] modelled:** Prism by the same probe running the real Prism.Core
  `RegionNavigationJournal` under a model of Prism.Wpf's
  `RegionNavigationService`, which needs WPF. The model is our own code,
  written from the request, confirm and commit order of the source lines it
  cites; it is not Prism's code.
- **[S] source reading only:** CrissCross, whose host needs a WPF window.

Run the probes with `dotnet run --project comparison/probes`.

| Case | Runic | Prism 9.0.537 | ReactiveUI RoutingState 26.0.1 | CrissCross 5.0.0 [S] |
| --- | --- | --- | --- | --- |
| Two clicks on Back, 3 pages deep, no guard pending | A second click before the first Back commits is ignored: Back can't execute from admission [T]. A click after the commit pops again. | Not reachable without a pending guard: Back commits inside the click, so a second click is a new Back [M, P1] | Not reachable without a pending guard: Back commits inside the click, so a second click is a new Back [T, R1, R2] | Two Backs before the posted work runs read the same stack, which becomes `[List, A, A]`: a duplicate entry instead of a pop |
| Back twice from code | Before the first commits: one pop, and both calls get the same result. After it: a second pop. With no history left, Back is rejected with `NoHistory` [T] | Two pops, each a complete Back [M, P1] | Two pops; at 2 pages deep the stack becomes empty, and the next Back throws `ArgumentOutOfRangeException` [T, R4] | As for two clicks |
| Back while the unsaved-changes confirm is open | The second Back joins the first: one confirm, and its answer settles both [T] | Two confirms; answering the older one first corrupts the journal: B is lost and A is duplicated, silently [M, P3, P4] (non-modal confirm or code only) | No guard built in. This app's guarded Back command shows 2 confirms and pops twice when both Backs arrive before the dispatcher runs [T, R6] (non-modal confirm or code only) | The guard is synchronous; see S1 |
| The guard of a page reached by Back | Asked [T] | Asked | App-defined | Not asked: the host keeps the departed ViewModel as active after a Back |
| A page's arguments and edits when it is shown again | Kept: the history holds the entry, with its ViewModel and input [T] | Arguments kept: the journal stores the URI and parameters. Edits kept only with `KeepAlive = true` and a matching `IsNavigationTarget`; this app uses `KeepAlive = false` | Kept: the history holds ViewModel instances | Arguments lost: the history holds types, and a Back passes no parameter, so `WhenNavigatedTo` must allow for none. Edits survive only because the ViewModel is a singleton |
| A confirm that is cancelled or superseded | The edits stay: the discard runs only when the departure commits [T] | The edits stay: this app discards in `OnNavigatedFrom`, which runs only once a navigation is confirmed and executes | App-defined | App-defined: this app re-issues only Back |

Sources, at the released versions:

- **Prism:** `RegionNavigationJournal.cs:69-78`, `:114-130`, `:142-149`
  (Prism.Core); `RegionNavigationService.cs:112`, `:130-150` (Prism.Wpf). Each
  `GoBack` sets `isNavigatingInternal` and resets it in its callback, so the
  callback of a superseded Back clears it while the next is still pending, and
  that one is recorded as a new navigation. The journal code is unchanged on
  Prism's master branch as of 2026-10-08.
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
synchronously, and the WPF host's Back checks it. Another Back to the same
destination joins the pending Back, including one with a cancellation token
from the ReactiveUI adapter's Back command. Each caller can cancel independently;
the shared transition cancels only when every caller cancels. Engine tests and
both ReactiveUI adapter flavors cover token-bearing joins
([#147](https://github.com/Runic-Artifex/runic-sdk/issues/147)). A Back after
the first has committed is a new request and pops again.

## Where the others are ahead

- **Platform reach.** Prism 9 ships `net462`, `net47` and `net6.0-windows`
  assets, and CrissCross ships `net472` and `net48` assets beside its .NET 8-10
  ones. Runic.Navigation.Wpf needs .NET 10.
- **Prism** is mature and widely deployed. Its modules and region adapters
  make regions a general composition tool: a TabControl becomes a region with
  one attached property, which is why its S3 is shortest. It has forward
  history, and `KeepAlive` keeps a page's View, so scroll position and
  selection survive Back; Runic keeps the ViewModel and builds a new View, so
  that state must live in the ViewModel. Its dialog service is mature.
- **ReactiveUI** is mature and widely used, with a large ecosystem. It makes a typed question to the View cheap with
  `Interaction<TIn, TOut>`, the shortest S2 here. Its VM-first navigation,
  `Navigate.Execute(new NoteDetailViewModel(...))`, makes the constructor the
  parameter contract, and `RoutingState` has no UI dependency, so it is
  testable without a dispatcher.
- **Cross-platform reach.** Prism also targets .NET MAUI and Uno Platform.
  ReactiveUI runs on WinUI, MAUI, Avalonia, Blazor and more. CrissCross has
  hosts for WPF, Avalonia, MAUI and WinForms, plus page transitions, WebView2
  integration and a Fluent UI kit. Runic.Navigation's engine is UI-free, but
  its only desktop host is WPF.
- **Runic's constraints.** Runic is a preview: its navigation API is
  experimental (`RUNICNAV001`). It requires Microsoft.Extensions.DependencyInjection.
  Its navigation is asynchronous, so a Back commits after the click returns,
  where Prism and RoutingState commit synchronously. It is a new project with
  a small user base and little third-party material: answers, samples and
  articles.

## Licences and status

- **Prism** is dual-licensed. Its
  [LICENSE at 9.0.537](https://github.com/PrismLibrary/Prism/blob/9.0.537/LICENSE)
  says: "To be qualified for the Prism Community License you must have an
  annual gross revenue of less than one (1) million U.S. dollars
  ($1,000,000.00 USD) per year or have never received more than $3 million USD
  in capital from an outside source, such as private equity or venture
  capital, and agree to be bound by Prism's terms and conditions." The latest
  public NuGet release is 9.0.537, from 2024-08-20; newer builds are on the
  paid Commercial Plus feed.
- **CrissCross** is MIT. 5.0.0 was released on 2026-10-05.
- **ReactiveUI** is MIT. 26.0.1 was released on 2026-10-04. Version 24.0.0
  moved it to ReactiveUI.Primitives, where `Unit` became `RxVoid` in the
  default packages; the `.Reactive` family keeps `Unit`.
- **Sextant** is MIT and built against ReactiveUI 22.3.1.
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
