# Runic.Navigation

Typed, host-neutral navigation for .NET ViewModels. `RunicNavigator` owns
navigation regions with stable entry ids, awaited departure guards,
initialize-once and resume-on-return hooks, navigated-to and navigated-from
notifications, results from an entry, and owned or borrowed content. `IRunicModelContext` serializes the ViewModel changes that
navigation commits. The types are in the `Runic.Navigation` namespace.

The package has no build targets, generator, Node or UI host, and it supports
trimming and NativeAOT. Its only dependencies are
`Microsoft.Extensions.DependencyInjection.Abstractions` and
`Microsoft.Extensions.Logging.Abstractions`.
[Runic.Application.Views](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md)
depends on it and presents regions in web Views.

## Install

```sh
dotnet add package Runic.Navigation --prerelease
```

Runic.Application.Views apps already have it through that package.

## One engine, idiomatic integrations

`RunicNavigator` is the navigation implementation for every consumer. Guards,
history, outcomes, cancellation and entry ownership have one set of semantics.
Presentation hosts and MVVM integrations are independent choices:

| Choice | Integration |
| --- | --- |
| WPF presentation | `Runic.Navigation.Wpf` presents regions with native Views and a dispatcher context. |
| Web presentation | `Runic.Application.Views` presents the same regions through generated Window/View bridges. |
| ReactiveUI ViewModels | `Runic.Navigation.ReactiveUI` (or its System.Reactive flavor) provides region observables, a Back command, `WhenNavigatedTo`, result commands and a leave confirmation asked through an `Interaction`; its [walkthrough](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md#walkthrough) covers pages, guards, results and the window close. Model-context scheduling comes from `Runic.Application.Views.ReactiveUI` in a Views app and from ReactiveUI.WPF in WPF. Compose native ReactiveUI commands for other operations. |
| CommunityToolkit ViewModels | Use the core's async operations directly with `AsyncRelayCommand` or `[RelayCommand]`, forwarding the command's cancellation token. No additional navigation adapter is needed. |

An integration can optimize its framework's API without duplicating navigation
behavior. Replacing WPF Views with web Views can retain the navigation engine
and ViewModels. Keep native UI activation and dispatcher concerns in the
presentation integration; retained entry lifetime belongs to navigation.

The [Toolkit WPF example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/wpf-navigation)
and [ReactiveUI recipes](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md#native-command-composition)
show each framework's native command style.

## Navigator

`RunicNavigator` owns typed navigation regions. Each region gives its entries
stable ids, and it supports awaited departure guards, initialize-once and
resume-on-return hooks, and owned or borrowed content. The navigator replaces
ReactiveUI's `RoutingState`; it does not wrap one. See the
[ReactiveUI adapter](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md)
for observables and a back command. WPF apps present regions with
[Runic.Navigation.Wpf](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.Wpf/README.md).

```csharp
using Runic.Navigation;

services.AddRunicNavigation(); // one model context and navigator per window scope

var main = navigator.CreateRegion<IMainViewModel>(this, NavigationTarget.Borrow<IMainViewModel>(home));
// The navigator constructs the document from its services, and owns it.
var result = await main.PushAsync<DocumentViewModel>();
if (result is NavigationResult<IMainViewModel>.Committed) { /* main.Current is the document */ }
await main.BackAsync(); // the document retires and is disposed; home resumes
```

`AddRunicNavigation()` registers a scoped `IRunicModelContext` and a scoped
`RunicNavigator` that uses it. Dispose window scopes with `DisposeAsync`
(`CreateAsyncScope`). The navigator is also `IDisposable` for containers that
only dispose synchronously: `Dispose` starts the same shutdown and returns
without waiting for it. Without DI, construct the navigator with
`RunicNavigatorOptions` (`ModelContext`, `Services`, `CreateEntryScopes`,
`LoggerFactory`, `TimeProvider`, `CloseTimeout`), and use the same model
context for the window's session.

### The root page

Give a region its root page once, where that page exists. When the region's
owner has it, pass it as the initial target of `CreateRegion`, as above. When
the root page itself navigates the region, for example a list that pushes its
detail page, it depends on the service that creates the region, so that
service creates the region empty and the window's ViewModel starts it:

```csharp
public sealed class AppNavigation // registered per window scope
{
    public AppNavigation(RunicNavigator navigator) => Main = navigator.CreateRegion<IPage>(this);

    public NavigationRegion<IPage> Main { get; }
}

public sealed class ListViewModel(AppNavigation navigation) : IPage { /* pushes into navigation.Main */ }

public sealed class ShellViewModel
{
    public ShellViewModel(AppNavigation navigation, ListViewModel list)
    {
        Main = navigation.Main;
        Main.Start(NavigationTarget.Borrow<IPage>(list)); // or NavigationTarget.Create<ListViewModel>()
    }

    public NavigationRegion<IPage> Main { get; }
}
```

`Start(target)` takes the same path as an initial target: it commits
synchronously, and the entry's `StateChanged` and `OnNavigatedTo(Reset)`, then
the region's `PropertyChanged`, run on the caller's thread before it returns.
So a window opened through `IRunicWindowService` shows its root page as soon as
`OpenWindowAsync` returns, with no startup hook. Unlike a navigation request,
`Start` throws: an exception from the target's factory or constructor
propagates from the ViewModel's constructor, the window's scope is released,
and `OpenWindowAsync` fails. `Start` throws `InvalidOperationException` when the
region already has its initial entry (a second `Start`, or a region created
with an initial target) or already received any navigation request; use
`ResetAsync` from then on. Don't start a region with a discarded
`_ = Main.ResetAsync(...)`: a failure would only be logged (event 1061), and
the window would show an empty region until a later turn commits it.

With `CreateEntryScopes`, a `Create` target resolves scoped services from its
own entry scope, so `Create<ListViewModel>()` would receive a second
`AppNavigation`. Start such a region with `Borrow`, as above.

### Operations and results

| Operation | Effect |
| --- | --- |
| `PushAsync` | The current entry is retained, and the new entry becomes current. |
| `BackAsync` | The current entry retires, and the top retained entry resumes. With fewer than two entries the request is `Rejected(NoHistory)`. |
| `BackToAsync(id)` | Every entry above the target retires, and the target resumes. |
| `ReplaceAsync` | The current entry retires, and the history is unchanged. |
| `ReplaceBorrowedAsync` on `NavigationRegion` | For presentation adapters, so hidden from IntelliSense: a host selects an existing model without knowing the generic content type. The model is type-checked, borrowed, and replaced through the same guards and engine. |
| `ResetAsync` | Every entry retires, and the new entry becomes the root. |
| `ClearHistoryAsync` | The retained entries retire, and the current entry stays. |
| `ClearAsync` | Every entry retires, and the region becomes empty. |

`PushAsync`, `ReplaceAsync` and `ResetAsync` take a target, or a ViewModel
type that the navigator constructs: `PushAsync<DocumentViewModel>()`, or
`PushAsync<DocumentViewModel, NoteId>(id)` for a ViewModel initialized with
input. `PushForResult<ConfirmViewModel, bool>()` does the same for a result.
See [Container-resolved targets](#container-resolved-targets).

- Every operation returns a `NavigationResult<TContent>`. The outcomes are
  returned, never thrown:
  - `Committed`, with the new current entry and the retired entries, and,
    when an entry it made current has an arrival hook, how that ended
    (`Arrival`, `ArrivalError`; see [After the commit](#after-the-commit-arrival)).
  - `Rejected`, with a `Reason`: `Guard`, `Cancelled`, `Closed`, `Reentrant`,
    `NoHistory`, `NotCurrent`, `EntryNotFound` or `Busy` (see
    [Rejecting requests while busy](#rejecting-requests-while-busy)).
  - `Failed`, with the `Phase` (`Guarding`, `Preparing` or `Committing`) and
    the exception (`Error`).
  - `Superseded`.

  Operations throw only for argument and ownership errors.

  `IsCommitted` tells code after the request whether to continue, without a
  pattern match on the content type. `ThrowIfFailed()` rethrows a `Failed`
  outcome's `Error`, or a `Committed` outcome's `ArrivalError`, with its
  original stack trace and returns the result otherwise, so a caller that
  treats failures as bugs can write
  `if ((await main.PushAsync<EditorViewModel>()).ThrowIfFailed().IsCommitted) { ... }`.
- `result.Outcome` is a `NavigationOutcome`: the `Kind` (`Committed`,
  `Rejected`, `Superseded` or `Failed`), for a rejection its `Rejection`
  reason, and for a commit its `Arrival` state (`None`, `Completed`,
  `Failed` or `Cancelled`, as in `Committed.Arrival`). It holds no .NET
  content or exception, so a ViewModel can return it to a Runic View, which
  cannot receive a `NavigationResult<TContent>`.
- `NavigationRequestOptions.ExpectedCurrent` makes a request conditional. It
  is rejected as `NotCurrent` if that entry is not current at admission or at
  commit. Use it, or `NavigationEntryContext.BackAsync`, for work that
  completes late, such as a double click or a dialog that answers after the
  user moved on.
- A region exposes `Current`, `CurrentEntry`, `History`, `CanGoBack`,
  `IsTransitioning` and `IsBusy`. It raises `PropertyChanged` for them inside a
  model turn, the commit turn for a navigation.

### Entry lifecycle

An entry starts `Pending` and becomes `Active` when its push commits. It then
moves between `Active` and `Retained` as pushes and Backs commit, and ends
`Retired`. Every state can retire:

```text
Pending ──commit──▶ Active ◀──Push / Back(To)──▶ Retained
   │                  │                              │
   │ not committed    │ Back, Replace,               │ BackTo, Reset,
   │                  │ Reset, Clear                 │ ClearHistory, Clear
   ▼                  ▼                              ▼
                    Retired
```

A closing parent region or navigator disposal also retires entries in any
state.

| State | Meaning |
| --- | --- |
| `Pending` | A push is preparing the entry. It is not in the region yet. |
| `Active` | The entry is the region's `Current`. |
| `Retained` | The entry is in the history below `Current` and keeps its content, draft included. |
| `Retired` | The entry has left the region, or a push of it never committed. It never becomes active again. |

**The entry's context.** Each entry has a `NavigationEntryContext`: its `Id`,
`State`, `Services`, `Region`, `Retirement` token, and the operations it
performs on itself (`BackAsync`, `CompleteAsync`, `DismissAsync`). Content
that the navigator constructs receives it as a constructor parameter, so it
holds its entry from the start:

```csharp
public sealed class ConfirmViewModel(NavigationEntryContext entry) : ObservableObject
{
    public Task Confirm() => entry.CompleteAsync(true).AsTask();
    public Task Cancel() => entry.DismissAsync().AsTask();
}

await dialog.PushForResult<ConfirmViewModel, bool>().Completion; // the navigator passes the dialog its own entry
```

- `Create<T>()`, `Create<T, TInput>(input)` and the region methods that wrap
  them (`PushAsync<T>()`, `ReplaceAsync<T>()`, `ResetAsync<T>()`,
  `PushForResult<T, TResult>()`) pass the new entry's context to a constructor
  parameter of type `NavigationEntryContext`. A factory target takes it as its
  second argument: `NavigationTarget.Create<IDialogViewModel>((services, entry) => new ConfirmViewModel(entry))`.
  Initial targets of `CreateRegion` receive it the same way.
- The content is constructed while its entry is still `Pending`, during the
  preparation; the entry becomes `Active` when the transition commits. Keep
  the context for commands rather than navigating from the constructor.
- Only that content receives the context. A service resolved from the
  container can't: with `AddRunicNavigation()`, resolving
  `NavigationEntryContext` from the container throws an
  `InvalidOperationException` that says where it is available. Pass the context
  on from the content to the objects that need it.
- Content created with `Own` or `Borrow` exists before its entry, so it
  receives the context in `INavigationInitialize.InitializeAsync`.

**Navigated to and from.** Content that implements `INavigationAware` is told
when its entry becomes current and when it stops being current:

| Call | When | Arguments |
| --- | --- | --- |
| `OnNavigatedTo(NavigationArrival)` | The entry becomes `Active`: its first commit (`Push`, `Replace` or `Reset`; `Reset` for an initial target) and every resume (`Back` or `BackTo`). | The entry id and the operation. |
| `OnNavigatedFrom(NavigationExit)` | The entry stops being `Active`: it is retained (`Kind` is `Retain`) or it retires (`Retire`). A region that clears or closes reports `Clear`. | The entry id, the operation and the kind. |

`NavigationEntryContext.StateChanged` reports every state change, for content
of any kind and for the objects it passes the context to. Its arguments carry
the previous state, the new state and the operation. A retained entry that
retires reports `Retained` to `Retired` without another `OnNavigatedFrom`, and
a pending entry whose transition didn't commit reports `Pending` to `Retired`.

- Both are synchronous and run in the commit turn: after the new navigation
  state is applied and after the departure's `OnCommitted` actions, before the
  regions raise `PropertyChanged`. The departing entry is notified first, then
  entries that retire without having been current, then the arriving entry.
  For each entry, `StateChanged` runs before the `INavigationAware` call. So
  `OnNavigatedTo` and `OnNavigatedFrom` alternate for an entry, starting with
  `OnNavigatedTo`, and `State` is already the new state in both.
- A transition that is rejected, superseded, cancelled or fails never calls
  `OnNavigatedTo` or `OnNavigatedFrom`, and it doesn't change the state of the
  current entry. Its pending entry reports `Pending` to `Retired` in a later
  turn, if it has `StateChanged` handlers.
- An initial target reports `Pending` to `Active` and `OnNavigatedTo(Reset)`
  inline in `CreateRegion` or `NavigationRegion<T>.Start`, so it doesn't run
  in a model turn unless that call was made in one.
- Each handler is called separately. One that throws is logged (event 1075),
  and the other handlers still run; the change stands. Handlers must not pump
  or await navigation; start navigation after the turn instead.
- `INavigationInitialize` and `INavigationResume` remain the asynchronous
  preparation hooks: they run before the commit, outside turns, and can fail
  the transition. `OnNavigatedTo` runs after `ResumeAsync`, once the resume
  commits.

### After the commit: arrival

`OnNavigatedTo` runs inside the commit turn and must not await. Work that
needs the page presented first, such as loading its data while it shows a
loading state, goes to `INavigationArrived`:

```csharp
public sealed partial class OrdersViewModel(IOrderStore store) : ObservableObject, INavigationArrived
{
    public async ValueTask OnArrivedAsync(NavigationEntryContext entry, NavigationArrival arrival, CancellationToken token)
    {
        var orders = await store.LoadAsync(token);
        await entry.ModelContext.InvokeAsync(() => Orders = orders, token); // presented state: commit in a turn
    }
}

var result = await Main.PushAsync<OrdersViewModel>(); // returns once the orders are loaded
```

- The navigator calls it for each entry whose `OnNavigatedTo` the commit
  called: the new entry of a Push, Replace or Reset, the entry a Back or BackTo
  resumes, and a child region's root that a `ResetToRoot` policy makes current
  again; one after another, in that order. ClearHistory and Clear call
  it for no entry. It runs again on every resume, so make it repeatable.
- Order: the commit turn (with `OnNavigatedTo`), then the retirement of the
  departed entries, then, with a model context that implements
  `IRunicModelPresentationYield` (WPF's `DispatcherModelContext`), a wait
  until the new view was laid out and rendered, then the hook. The request
  returns `Committed` after it. `IsTransitioning` is already `false` while it
  runs; `IsBusy` and `WhenIdleAsync()` include it.
- A hook that throws is logged (event 1076) and gives
  `Committed { Arrival = Failed, ArrivalError = ... }`; the entry stays
  current. A hook cancelled through its token gives `Cancelled`. Without
  hooks `Arrival` is `None`.
- Its token is cancelled when the entry stops being current (a later commit
  retains or retires it), when it retires, and when the navigator closes; not
  by the caller's token. A later request is admitted as usual and, if it moves
  the entry, cancels the hook. Retirement doesn't wait for the hook: check the
  token after each await. `RunicNavigator.HookCancellationToken` is this token
  while the hook runs.
- The hook may navigate. Its requests are admitted, also into its own region,
  to redirect with `ReplaceAsync` or give up with `entry.BackAsync()`, and
  into the regions its page owns; a region that rejects requests while busy
  admits them too. It must not await `WhenIdleAsync()`, which waits for it.
- `INavigationArrived<TInput>` receives the input of `Create<T, TInput>(input)`
  and the region methods that wrap it, instead of `INavigationArrived`. The
  input targets accept content that implements `INavigationInitialize<TInput>`,
  `INavigationArrived<TInput>` or both; both receive the same input.
- An initial target's hook starts after `CreateRegion` or `Start` returned. The
  region is busy, and the navigator not idle, until it ends; a failure is
  logged. `INavigationInitialize` stays forbidden on initial targets.
- Without a hook scheduler (`RunicModelContext`), the content is already
  presented, so change its state through `entry.ModelContext.InvokeAsync`.
  With `DispatcherModelContext` the hook runs on the UI thread and may set
  properties directly.

Retirement runs once per entry, whichever path reaches it first: commit
cleanup, a closing parent, a failed preparation or navigator disposal. It runs
these steps in order, and each step runs even if an earlier one failed:

1. `NavigationEntryContext.Retirement` is cancelled.
2. The child regions that the entry's content owns close. Their in-flight
   transitions end as `Rejected(Closed)`, and their entries retire depth-first.
3. Owned content only: its presentations are forgotten.
4. Owned content only: the content is disposed, outside model turns. Then
   the entry's service scope, if the navigator created one, is disposed.
5. Owned content only: its model-context lease is released.

A failed step is logged as event 1064.

### Transitions

| Phase | Where | What happens |
| --- | --- | --- |
| Admission | Synchronously, in the call | The request is rejected at once for these reasons: `Closed`; `Reentrant`, when the caller is inside a hook of a transition in this region, an ancestor or a descendant, for example a `CurrentPane` guard that calls `Main.BackAsync()`; `NotCurrent`; `NoHistory`; `Cancelled`, for a token that is already cancelled; or `Busy`, in a region that rejects requests while busy. Otherwise the request supersedes every earlier request of the region that has not started committing, unless it is a Back that joins a pending Back (see below). It also supersedes those of the child regions its plan affects. |
| Guarding | Outside model turns | The request first waits until earlier requests of the region, and those of the child regions in its plan, have ended. Then departure guards run one at a time, deepest first, also when a push only retains the current entry (`NavigationDepartureKind.Retain`), followed by the region and navigator guards; those registered with `Order = BeforeEntries` run before the departure guards instead (see [Guards for a whole region](#guards-for-a-whole-region)). `false` rejects the request as `Guard`. A guard that throws fails it (event 1060). |
| Preparing | Outside model turns | A new entry runs its factory, the ownership check, model-context binding and `InitializeAsync`, exactly once. A resumed entry runs `ResumeAsync`, which runs again on a retry. A step that throws fails the request (event 1061), and its pending entry retires. |
| Committing | One model turn | The turn re-checks supersession, `ExpectedCurrent`, the target and the versions of the affected regions. Then it applies the new stacks and child policies, runs the departure's `OnCommitted` actions, raises the entries' `StateChanged` and `INavigationAware` notifications (event 1075 for a handler that throws), and raises `PropertyChanged`. A `PropertyChanged` handler that throws is logged (event 1063; its `Property` names the region property), and the commit stands. If the turn cannot run, for example because the model context throws, the request fails as `Failed(Committing)` (event 1062), nothing changes, and its pending entry retires. If the model context is already closed, the request is `Rejected(Closed)` instead, without event 1062, and the navigator starts closing. |
| Committed | Outside model turns | Admission is released in the commit turn (`IsTransitioning` turns `false`), departing entries retire, and the arrival hooks of the entries the commit made current run. The returned `ValueTask` completes after them; the region stays `IsBusy` until then. |

- A `BackAsync()` that arrives while the region's
  latest request is another Back that hasn't committed, and neither the
  region nor the child regions the departing entry owns have changed since
  that Back was admitted, **joins** it instead of superseding it: interested callers
  get the same result, the departure guard runs once, and a confirm dialog
  stays open (event 1074). Back from code while the guard asks therefore pops
  once and asks once. A Back that starts after the first one committed is a
  new request and pops again; that is stack semantics. Token-bearing Backs
  join too: cancelling one caller before commit returns `Rejected(Cancelled)`
  for that caller immediately. The shared Back is cancelled only when every
  caller has cancelled; a caller without a cancellable token keeps it alive.
  Once every caller has cancelled, the region is no longer busy because of
  that Back, even while its guard still runs, and the Back never commits (see
  [Rejecting requests while busy](#rejecting-requests-while-busy)).
  Cancellation after commit does not change the outcome. A return from a
  result entry (`CompleteAsync`, `DismissAsync`) is never joined.
- `IsTransitioning` is `true` from admission, synchronously. A command that
  reads it in `CanExecute`, such as `NavigationHost`'s `BrowseBack` in
  Runic.Navigation.Wpf, is disabled before the Back call returns, so a double
  click pops once.
- A dialog answers its guard with `CompleteAsync` or `DismissAsync`. Code run
  from the confirm dialog that awaits `BackAsync()` on the guarded region
  before answering joins the Back that waits for that answer, so it doesn't
  complete until the dialog closes another way.
- A request is superseded only before it commits, never after. A cancelled
  token gives `Rejected(Cancelled)` and navigator disposal gives
  `Rejected(Closed)`, in any phase before the commit. So does the close of
  the request's region: a parent's Back that retires a child region
  supersedes the child requests pending at its admission, and a child
  request admitted after it is `Rejected(Closed)` when the parent commits,
  never `Superseded`, whichever phase it is in.
- Hook tokens are cancelled when the request is superseded, cancelled or
  closed. A superseded request that is still running 5 seconds later is logged
  once (event 1067). The next request waits for it.
- Admission inside a model turn does not block. The transition continues on
  the thread pool, so no hook runs in the caller's turn. Don't block on its
  result in that turn.
- Admission is per region, so a guard in one region may await a request in a
  sibling region, as the confirm dialog below does. Parent and child regions
  coordinate through the plan: a child commit during the parent's guard
  supersedes the parent's request.
- One entry's guard never runs twice at the same time. It can still run again
  for the same departure: a parent transition that retires a child region asks
  the child's guards even when a child transition already asked them. Write
  guards to be repeatable. A `LeaveConfirmation` keeps its yes while the
  transition it allowed is unsettled and passes it to the transition that
  supersedes it, so the rerun doesn't ask again. When a change in another
  region, rather than a later request, superseded the transition, the yes
  ends, and the user may be asked twice.

### Rejecting requests while busy

By default a later request supersedes the earlier ones that haven't
committed. A region whose page loads data after a click, and that must not
start a second navigation meanwhile, can refuse requests instead:

```csharp
Main = navigator.CreateRegion<IPage>(this, initial,
    new NavigationRegionOptions { Admission = NavigationAdmission.RejectWhileBusy });

// Elsewhere, while a request of Main is still running:
var result = await Main.PushAsync<DetailViewModel>(); // Rejected(Busy)
```

- A region is busy (`IsBusy`) from a request's admission until its task
  completes: the guards, the preparation, the commit and the retirement of the
  departed entries. `IsTransitioning` ends earlier, in the commit turn.
- With `RejectWhileBusy`, a request to a busy region is `Rejected(Busy)` at
  admission, after the other admission checks. A Back that can join the
  pending Back still joins it, so a double click asks once and gets one
  result. Returns from a result entry (`CompleteAsync`, `DismissAsync`, the
  Back of a cancelled `PushForResult` caller) are refused like any other
  request; repeat them once the region is idle.
- The region's task is removed from the busy set before its result is set, so
  a request made after `await region.PushAsync(...)` resumed is never `Busy`
  because of that push.
- This holds for a cancelled Back too. A token-bearing `BackAsync` returns
  `Rejected(Cancelled)` as soon as its token is cancelled, before its guard
  has returned. When that was the last caller of the Back, the Back leaves the
  busy set before that result is set, so the user's next Back after
  dismissing a leave confirmation is admitted. The abandoned Back never
  commits. The next request waits until its guard has returned, as it waits
  for any earlier request; a guard that ignores its token delays it, but
  doesn't refuse it. While another caller of the joined Back is still
  waiting, the region stays busy for that caller.
- `NavigationRequestOptions.Admission` overrides the region's policy for one
  request. Pass `NavigationAdmission.Supersede` for a request that must win,
  such as the clear of a window close: the WPF window close guard and dialog
  host, and Runic.Application.Views' `CanCloseAsync`, do. Pass
  `RejectWhileBusy` to refuse one request in a region that otherwise
  supersedes.
- The policy covers requests made to the region. A parent request whose plan
  resets or closes a busy child region still supersedes the child's requests.
- `IsBusy` is raised in a model turn, like `IsTransitioning`. The change to
  `false` is posted, so the notification can trail the request's task; the
  property itself is always current. Back-to-back requests can keep it `true`
  without a `false` in between.
- `region.WhenIdleAsync()` completes when the region is not busy. Unlike
  `RunicNavigator.WhenIdleAsync()`, it doesn't wait for other regions, nor for
  the guard of a Back whose callers have all cancelled; the navigator's does.

### Ownership

| Target | Who creates the content | On retirement |
| --- | --- | --- |
| `Borrow(content)` | The caller or a container | Nothing: the content is never disposed or forgotten, and the regions it owns are left alone. |
| `Own(content)` | The caller, with `new`, handing it over | Child regions close, presentations are forgotten, the content is disposed and its lease is released. |
| `Create(factory)` / `Create(factory, input)` | The factory, with `new`, when the transition prepares; a `(services, entry)` factory also receives the entry's context | As `Own`, then the entry's scope is disposed. |
| `Create<T>()` / `Create<T, TInput>(input)` | The navigator, with `ActivatorUtilities`, when the transition prepares | As `Own`, then the entry's scope is disposed. |
| `Share(content)` | The caller (a new instance), or a live `Own`, `Create` or `Share` entry of this navigator | Nothing while another entry holds the content; the last holder releases it as `Own`, including the scope of created content. |

- Content resolved from a container (a window scope, the root provider or
  Splat) belongs to that container, so `Borrow` it. Use `Own` and `Create` only
  for content constructed with `new` or by the navigator. A factory receives
  the entry's `IServiceProvider` to resolve constructor dependencies. Those
  dependencies stay container-owned. Without entry scopes, don't resolve
  disposable transients for it: the provider keeps them until it is disposed.
- An instance can be owned once. `Own` of an instance the navigator has
  already owned, whether live or retired, throws `InvalidOperationException`.
  A `Create` factory that returns one gives `Failed(Preparing)`.
- Present owned content only through its region's slot. Retirement forgets
  every presentation of the instance.
- `Share` lets several entries hold one instance, as when the next page takes
  over the editor of the current one:
  `ReplaceAsync(NavigationTarget.Share(current))` retires the current entry
  and disposes nothing. A Push with `Share` keeps the content in both entries.
  The content is forgotten and disposed when the last entry that holds it
  retires; a pending `Share` push holds it from admission, so the entry that
  handed it over can retire first. Each entry is its own entry: its
  `INavigationInitialize` and `INavigationArrived` hooks run, and its
  `NavigationEntryContext` is its own. Content that kept the context it was
  constructed with still has its first entry's, so take the context in a hook
  if the content moves between entries.
- A departure that retires an entry whose content another entry keeps has
  `NavigationDeparture.ContentContinues`; `LeaveConfirmation` treats it like a
  `Retain` departure and doesn't ask. `OnNavigatedFrom` and `StateChanged`
  still report the entry's retirement.
- `Share` throws `InvalidOperationException` for an instance that was owned
  and fully released, that another navigator holds, or that owns regions;
  `Own` of shared content throws too. Shared content can't own regions
  (`CreateRegion` with it as owner throws), and a shared target can't be an
  initial target: start with `Own` or `Create`, then share. Don't also `Borrow`
  shared content, since a borrowing entry can outlive its disposal.
- Borrowed selection hosts use caller/container-owned models. Do not re-borrow
  content from a navigator-owned entry into another entry; retirement of its
  owning entry still disposes that content.
- Owned content is disposed outside model turns, on the model's thread when the
  context schedules hooks (see Threading). A `Dispose` that touches
  shared model state uses `ModelContext.InvokeAsync`, and it must not await
  navigation on the same navigator.
- A region's owner can be any object. The regions an entry's content owns are
  that entry's child regions. Those of owned content close when the entry
  retires. `NavigationRegionOptions.WhileParentRetained` decides what they do
  while the entry is retained: `Keep` (the default), `ResetToRoot` or `Clear`.
  Those of borrowed content keep their entries when the entry retires, because
  the content lives on. Regions owned by the root, or by an object that is no
  entry's content, are nobody's child regions.
- An initial target (`CreateRegion(owner, initial)` or `region.Start(target)`)
  becomes current without a transition. Its content must not implement `INavigationInitialize` or
  `INavigationInitialize<TInput>`, and `Create(factory, input)` and
  `Create<T, TInput>(input)` cannot be initial targets.

#### Which departures ask a child region

A departure asks the guards in the departing entry's child regions, and in
theirs, deepest first and before the entry's own guard:

| The entry | Its content | Its child regions | Guards asked in them |
| --- | --- | --- | --- |
| Retires (`Back`, `BackTo`, `Replace`, `Reset`, `Clear`, `ClearHistory`) | Owned | Close | Every entry, as `Retire` |
| Retires, as the tab that a selector leaves (`ReplaceBorrowedAsync`) | Borrowed | Keep their entries | The current entry of each, as `Retain` |
| Is retained (`Push`) | Owned | Follow `WhileParentRetained` | The entries `ResetToRoot` or `Clear` removes, as `Retire`; none with `Keep` |
| Is retained (`Push`) | Borrowed | Keep their entries | None |

- A guard that rejects keeps the whole departure: the entry stays current,
  so a tab switch leaves the tab selected, and no region changes.
- A commit in an asked child region while the departure is guarding
  supersedes the departure, as for owned children.
- A tab that only refers to a region of the root asks nothing when it is
  left. Create a tab's page regions with the tab's model as their owner:
  `navigator.CreateRegion<IPage>(this, ...)` in the tab's constructor.
- A window's guarded close (`GuardClose`, `GuardWindowClose`, see "Window
  close and disposal") clears the regions it guards, so every entry in them
  and in their owned child regions is asked as `Retire`, whichever tab is
  selected.
- `LeaveConfirmation` allows a `Retain` departure without asking, because a
  page kept in its region keeps its draft. Pass `askOnRetain: true` to ask
  before the tab is left too; a confirmed switch then runs `discard`.

### Container-resolved targets

`NavigationTarget.Create<T>()` and `Create<T, TInput>(input)` let the navigator
construct the ViewModel with `ActivatorUtilities.CreateInstance` from the
entry's service provider. The constructor's parameters come from the
container; the ViewModel itself is never resolved, so the navigator owns it.
The region methods `PushAsync<T>()`, `PushAsync<T, TInput>(input)`, the same
forms of `ReplaceAsync` and `ResetAsync`, and `PushForResult<T, TResult>()`
wrap these targets. `T` must be a `TContent`, checked at compile time.

```csharp
await main.PushAsync<DocumentViewModel>();                      // DocumentViewModel(EditorViewModel editor, ...)
await main.PushAsync<NoteViewModel, NoteId>(id);                // NoteViewModel : INavigationInitialize<NoteId>
var confirm = dialog.PushForResult<ConfirmViewModel, bool>();
var picked = dialog.PushForResult<bool>(NavigationTarget.Create<ConfirmViewModel, string>("Discard?")); // input and a result
```

- Constructors are kept for trimming and NativeAOT
  (`[DynamicallyAccessedMembers(PublicConstructors)]` on `T`).
  `[ActivatorUtilitiesConstructor]` picks one of several constructors.
- A navigator without `Services` uses an empty provider. A constructor that
  needs services then fails as `Failed(Preparing)` (event 1061), like a
  throwing factory.
- A constructor parameter of type `NavigationEntryContext` receives the new
  entry's context; see [The entry's context](#entry-lifecycle). It is never
  resolved from the container, and a service that depends on it fails to
  resolve.
- `NavigationEntry.Services` and `NavigationEntryContext.Services` return the
  provider that built the entry. After an entry with its own scope retires,
  resolving from it throws `ObjectDisposedException`.

**Entry scopes.** With `RunicNavigatorOptions.CreateEntryScopes`, the navigator
creates one `IServiceScope` for each entry that a `Create` target builds,
initial targets included. The factory and `ActivatorUtilities` resolve from
it, and the entry's `Services` returns it. Retirement disposes the content,
then the scope, so scoped and disposable transient dependencies live exactly
as long as the entry. A construction that fails disposes the scope at once.
`Borrow` and `Own` entries never get a scope.

Turn entry scopes on only for a navigator whose `Services` is the root
provider, for example a singleton navigator. A navigator resolved from a scope,
such as the one `AddRunicNavigation()` registers, must leave it off:
`CreateScope` on a scoped provider makes a sibling scope of the root, so the
entry's scoped dependencies would silently differ from the navigator's. The
navigator can't tell the two providers apart. `AddRunicNavigation()` leaves
it off; a window scope already bounds those dependencies.

### Presenting regions

A host presents a region's `Current`. In Runic.Application.Views, a get-only
`NavigationRegion<TContent>` property of a ViewModel is a content slot that
the generated Bridge presents in the window's web View; see
[Content slots](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#content-slots).
Other hosts bind `NavigationRegion`, the non-generic base class of every
`NavigationRegion<TContent>`, and `NavigationEntry`, the base class of every
`NavigationEntry<TContent>`, like `Task` and `Task<T>`: its `CurrentEntry` and
`History` return the region's typed entries, and its `BackAsync` and
`ClearAsync` return a `NavigationResult<object>`. Only the navigator creates
regions and entries; the base classes can't be derived from outside
Runic.Navigation. `Runic.Navigation.Wpf` provides WPF hosts and a dispatcher
model context. `RunicNavigator.AttachPresentation` is for presentation
integrations only: retirement calls `INavigationPresentation.Forget` once for
each attached presentation and owned entry.

### Results from an entry

`PushForResult<TResult>(target)` pushes an entry and returns a
`NavigationResultRequest`:

- `Transition` is the push's result.
- `Completion` ends `Completed(value)` only when the entry calls
  `NavigationEntryContext.CompleteAsync(value)` and the Back that it issues
  commits.
- `Completion` ends `Dismissed` on every other path: the push does not commit,
  the entry retires another way, the caller's token is cancelled after the
  commit (which also goes back from the entry), or the navigator closes.

A Back from `CompleteAsync` or from caller cancellation may leave the region
empty, so a dialog region can start empty. A push onto an entry whose caller
cancelled retires that entry instead of retaining it. That Back can still be
rejected, for example when a push over the entry is already committing or the
entry is no longer on top. Then the dismissed entry stays in the history: a
later Back resumes it, and its `CompleteAsync` goes back, possibly emptying the
region, and drops the value (event 1071).

A rejected `CompleteAsync`, for example one vetoed by a guard, leaves the
request open, and the entry can try again. `CompleteAsync` accepts any value of
the request's result type at run time, so `true` completes a `bool?` request
and `5` an `object` request. Completions run their continuations
asynchronously, after the commit turn. A completion is set when the entry
starts retiring, so it can be observed while the entry's owned content is
still being disposed.

`NavigationEntryContext.DismissAsync()` leaves an entry without a result. For
the current entry it ends an open request `Dismissed` at once and goes back,
which may leave the region empty, like a cancelled caller token. Called from
the entry's own `InitializeAsync`, it cancels the push that creates the entry
(`Rejected(Cancelled)`) and returns at once; don't await that push from the
hook. For a retained entry it ends the request and leaves the entry in place;
for a retired one it does nothing. Both return `Rejected(NotCurrent)`. A
dialog's Cancel uses it, so Cancel always ends the question, even when its
Back is rejected because something was pushed over the dialog.

### Leaving with unsaved changes

A guard's `true` is not a commit: a later request can still supersede the
departure, another guard can veto it, or it can fail. Work that must happen
only when the departure happens goes to `NavigationDeparture.OnCommitted`:

```csharp
public async ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken token)
{
    if (!await departure.ModelContext.InvokeAsync(() => Editor.IsDirty, token)) return true; // guards run outside turns
    if (!await AskAsync(token)) return false;
    departure.OnCommitted(Editor.DiscardChanges);
    return true;
}
```

- Actions run in the commit turn of the departure, in registration order,
  after the new navigation state is applied and before the regions raise
  `PropertyChanged`. They don't run when the transition is superseded,
  rejected, cancelled or fails.
- Register while the guard runs. `OnCommitted` throws
  `InvalidOperationException` after the guard returned.
- An action that throws is logged (event 1072); later actions still run, and
  the commit stands. Actions must not pump or await navigation.

`LeaveConfirmation` packages the whole pattern. It allows a departure that only
retains the entry (unless `askOnRetain`), allows when there are no unsaved
changes, asks otherwise, and discards with `OnCommitted`. With `askOnRetain`,
a confirmed push over the entry discards too, although the entry stays in
the history. Its yes stands while
the departure it allowed is unsettled: when the guard reruns for the same
entry, for a parent transition or a request that superseded the one it
allowed, it answers without asking again, and `discard` still runs at most
once. `LeaveConfirmation.InDialog` asks with a dialog pushed for a `bool`
result into another region; only `Completed(true)` confirms, and the guard's
token dismisses the dialog when the departure is superseded or the navigator
closes. It takes `askOnRetain` like the constructor. The dialog region must
not be the guarded region, one of its ancestors or one of its descendants:
that push would be `Rejected(Reentrant)`.

### Guards for a whole region

An entry's guard answers for its own content. A guard that answers for a
whole region, whatever its pages are, such as "discard the unsaved work of
this window?", is an `INavigationRegionGuard` registered on the region, or on
the navigator for every region:

```csharp
public sealed class UnsavedWorkGuard(Workspace workspace) : INavigationRegionGuard
{
    public async ValueTask<bool> CanDepartAsync(NavigationRegionDeparture departure, CancellationToken token)
    {
        if (!await departure.ModelContext.InvokeAsync(() => workspace.HasUnsavedWork, token)) return true;
        if (!await workspace.AskDiscardAsync(token)) return false;
        departure.OnCommitted(workspace.Discard);
        return true;
    }
}

_registration = Main.AddDepartureGuard(new UnsavedWorkGuard(workspace));
// or, for every region: navigator.AddDepartureGuard(guard)
```

- A region guard is asked once per transition that departs from an entry of
  its region: a request to the region, a parent request that resets or closes
  it, or the departure of borrowed content that leaves it, as a tab switch
  does. A push into an empty region or a `BackTo` the current entry asks none.
- Region guards run after the entries' guards, region by region, deepest
  first, each region's guards in registration order, then the navigator's
  guards once. `NavigationRegionGuardOptions.Order` moves a guard before the
  entries' guards (see [Preconditions and last questions](#preconditions-and-last-questions)). `NavigationRegionDeparture` names the `Region`, the departing
  `Entries` (current first), the `Operation`, and the `Kind`: `Retire` when one
  of them retires, otherwise `Retain`. A navigator guard gets the region the
  request was made to, and the entries and kind of the whole transition.
- `NavigationRegionGuardOptions` decides when a guard is asked. By default it
  is skipped when the transition only retains the region's entries
  (`AskOnRetain`), and for `ClearHistory` (`AskOnClearHistory`), also for the
  child regions a ClearHistory closes. The entries' own guards are asked
  either way.
- `false` gives `Rejected(Guard)` with `By` = `null`. A guard that throws fails
  the request as `Failed(Guarding)` (event 1060, with the guard's type as
  `EntryType`). `OnCommitted` actions run in asking order, so after those of
  the entries' guards unless the guard is asked `BeforeEntries`.
- They run like entry guards: outside turns, on the model's thread with a hook
  scheduler, never two of one region at once, with the hook's token, and a
  request from one into its region, an ancestor or a descendant is
  `Rejected(Reentrant)`. A joined Back asks them once.
- The guards are read when the request starts guarding. Disposing the
  registration removes the guard; one removed before its turn is not asked.
  Navigator disposal asks none.
- A window close that clears its regions one after another, such as WPF's
  `GuardWindowClose` or a Runic View's `CanCloseAsync`, asks a navigator guard
  once per region with entries. Register an "unsaved anywhere" guard on the
  window's main region instead, or let it remember its answer. A
  `BeforeEntries` guard that vetoes is asked for the first region with
  entries, before any page, so the window stays open with every region as it
  was.

#### Preconditions and last questions

A region or navigator guard is asked after the entries' guards by default
(`NavigationRegionGuardOrder.AfterEntries`): the page answers for itself
first, and a window-wide question comes last. A guard that checks a
precondition, such as "no navigation while an operation runs", should refuse
before any page asks the user a question whose answer would not matter.
Register it with `Order = NavigationRegionGuardOrder.BeforeEntries`:

```csharp
public sealed class OperationGuard(Operations operations) : INavigationRegionGuard
{
    public ValueTask<bool> CanDepartAsync(NavigationRegionDeparture departure, CancellationToken token) =>
        departure.ModelContext.InvokeAsync(() => !operations.IsRunning, token);
}

// A precondition: refuses before the pages are asked, also when a push only retains the current page.
_operation = navigator.AddDepartureGuard(new OperationGuard(operations),
    new NavigationRegionGuardOptions { Order = NavigationRegionGuardOrder.BeforeEntries, AskOnRetain = true });
// A last question: asked after the pages' own "discard the changes?" (AfterEntries is the default).
_unsaved = Main.AddDepartureGuard(new UnsavedWorkGuard(workspace));
```

A transition asks, in this order: the `BeforeEntries` region guards (region
by region, deepest first, each region's in registration order), the
`BeforeEntries` navigator guards, the entries' departure guards (deepest
first), the `AfterEntries` region guards and the `AfterEntries` navigator
guards. The first veto ends the transition as `Rejected(Guard)` with no `By`
entry and asks no further guard. `Order` doesn't change whether a guard is
asked: `AskOnRetain` and `AskOnClearHistory` apply either way, and a
transition without a departure asks none. A guard runs inside the request's
transition, so, like any guard veto, a request it refuses has already
superseded an earlier pending request of the region.

`LeaveConfirmation` keeps the caller's context, so its confirm delegate stays
on the UI thread. Without a hook scheduler the guard can run in the frame of
the call that started the navigation. If that call blocks a UI thread on the
navigation's result (for example with `GetAwaiter().GetResult()`), the guard's
continuation can never run there, and the two deadlock. Await navigation
results; don't block on them, as with any asynchronous code.

The [Notes example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-view-first)
asks this way. Here the navigator constructs the confirm dialog and passes
it its entry:

```csharp
// The document: the guard forwards to a LeaveConfirmation.
_leave = LeaveConfirmation.InDialog(navigation.Dialog,
    () => NavigationTarget.Create<IDialogViewModel>((_, entry) =>
        new ConfirmNavigationViewModel(entry, "Discard the unsaved edits?")),
    () => Editor.IsDirty, Editor.DiscardChanges);

public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken token) =>
    _leave.CanDepartAsync(departure, token);

// The confirm answers with the entry it was constructed for.
public sealed partial class ConfirmNavigationViewModel(NavigationEntryContext entry, string message)
    : ObservableObject, IDialogViewModel
{
    public string Message => message;
    [RelayCommand] private Task Confirm() => entry.CompleteAsync(true).AsTask();
    [RelayCommand] private Task Cancel() => entry.DismissAsync().AsTask();
}
```

ReactiveUI apps can ask through an `Interaction` instead of a dialog region, with
`ReactiveLeaveConfirmation.WithInteraction` from
[Runic.Navigation.ReactiveUI](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md#leave-confirmation-through-an-interaction).

Work that a hook starts on a presentation's side, such as a question shown in a
browser, should end with the hook. `RunicNavigator.HookCancellationToken`
returns the token of the hook whose code runs on the calling asynchronous flow
(the guard's `token`, or an arrival hook's), or `CancellationToken.None`
outside hooks.
`Runic.Application.Views` ends a browser interaction request with it, so a
superseded guard aborts the browser handler's `signal`.

### Window close and disposal

DI disposes the navigator before the model context it depends on. Its
`DisposeAsync`:

1. Refuses new requests and dismisses every open `PushForResult` request, so
   guards that await a result unblock.
2. Cancels in-flight transitions and waits for them, and for running cleanup,
   up to `CloseTimeout` (10 seconds on its `TimeProvider`). A late transition
   never commits.
3. Retires every remaining entry, region by region, pending entries included.
   Clearing a closing region normally runs in a model turn. These turns share
   one `CloseTimeout`, so disposal takes about twice `CloseTimeout` at most,
   plus owned content disposal. If a turn does not run in time, the stack is
   cleared outside a turn (event 1068).

Disposal does not run guards. Disposing a navigator cancels window-owned
operations that its owned content started, and then disposes that content.
To ask the guards before a user closes the window, opt in on the window:
`GuardClose` on a `RunicWindow<T>`
([Runic.Application.Views](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#guarding-a-user-close),
Runic Desktop's embedded window) or `GuardWindowClose` on a WPF
`NavigationHost` (Runic.Navigation.Wpf). Either clears the window's regions
before it closes, and a guard that rejects or fails keeps the window open.

`WhenIdleAsync()` waits until no transition or retirement is running;
`NavigationRegion<T>.WhenIdleAsync()` waits only for the requests of one region.

### Threading

Without a scheduler, as with `RunicModelContext`, hooks (factories, guards,
initialize and resume) run outside turns on the thread that runs the
transition, and owned content is disposed there too. A model context whose
model has thread affinity, such as a UI dispatcher context, can also implement
`IRunicModelHookScheduler`. The navigator then runs each hook and each owned
content disposal as its own operation on the model's thread: never inline in
the caller, never inside a turn. Hook operations don't count as turns. Once
the context is closed, disposal runs on the thread pool. A decorator that
wraps a scheduling context must implement and forward the interface too.

An initialize hook may set the state of content created for its entry
(`NavigationTarget.Create`, or a new instance handed over with `Own`) directly,
also after `await`: nothing presents that content before the entry commits,
and the navigator commits in a turn after the awaited hook. Commit with
`ModelContext.InvokeAsync` state that is already presented or that other work
also writes, such as a `Borrow` target, a service or another page, and read or
write state that way in guards and resume hooks, which run while the content
may be presented.

A context that can close on its own, for example when its UI thread shuts
down, can also implement `IRunicModelContextLifetime`. The navigator subscribes
to its `Closed` token and starts closing when it fires: later requests are
`Rejected(Closed)` at once, and transitions in flight are cancelled as closed.
Without it, the navigator learns about the close only when a turn or hook
fails with `ObjectDisposedException`, so a request can wait behind a
transition whose hook ignores its cancellation. Decorators forward this
interface too.

During `DisposeAsync`, a scheduled disposal that hasn't started by the
clearing turns' deadline runs on the thread pool instead, exactly once, so
disposal completes even when the model's thread is blocked. Don't block the
model's thread on `DisposeAsync` (for example `GetAwaiter().GetResult()` in a
WPF `OnExit`): call `Dispose()` there, which starts disposal without waiting.
A cleanup that starts after disposal finished (for example content that a
factory returns late) waits on the model's thread without a bound. If that
thread never runs it again and the context never closes, that content and
its model-context lease are never released. Close the context when the
application shuts down, for example on `Dispatcher.ShutdownStarted` in WPF.

The navigator itself never continues on the model's thread: after awaiting a
hook, a turn or other user code, it moves to the thread pool when the
completion ran in the model context. A caller that awaits a navigation result
resumes on its own context as usual.

### Pitfalls

- A hook that disposes its own navigator stalls disposal for `CloseTimeout`,
  because disposal waits for the transition that runs the hook.
- A hook that awaits `WhenIdleAsync()` deadlocks: the navigator is not idle
  while that hook runs. The same holds for the `WhenIdleAsync()` of the hook's
  region.
- A child guard that commits a change in its own region supersedes the
  parent's transition, because the parent's commit sees the child's version
  change.
- A request from a hook into the same region, one of its descendants or one of
  its ancestors is `Rejected(Reentrant)`. For example, a parent's
  `InitializeAsync` cannot push into its own child region, and a `CurrentPane`
  guard cannot call `Main.BackAsync()` on the region that holds its document.
  Issue the request after the transition commits. A hook's code is the hook's
  own frame and its continuations, including `await Task.Yield()` and awaits
  that resume through the `SynchronizationContext` the hook started with:
  requests from there stay `Rejected(Reentrant)`. Input handlers that a hook's
  nested message loop dispatches, for example while a guard shows a modal
  window, are not the hook. One gap fails safe: a handler that the nested loop
  dispatched and that awaits past the loop's end, while the transition is
  still in flight, may continue as the hook and get `Rejected(Reentrant)`.
- A service that creates a region with a container-built initial target whose
  constructor needs that service is a dependency cycle. For example,
  `MainNavigation` creates its region with `Create<HomeViewModel>()`, and
  `HomeViewModel(MainNavigation navigation)` resolves `MainNavigation` again,
  which creates another region. `CreateRegion` detects the re-entry for the
  same owner type and initial target type on the same thread, on any
  navigator, and throws `InvalidOperationException` instead of overflowing the
  stack. One holder type reused at two levels with different targets is fine.
  Create the region empty and start it once the service exists, for example
  with `navigation.Region.Start(NavigationTarget.Create<HomeViewModel>())` in
  the window ViewModel's constructor (see [The root page](#the-root-page)).

## Model context

`IRunicModelContext` runs short synchronous turns one at a time, in posting
order. Navigation commits run as turns, and region `PropertyChanged` handlers
run inside them. `InvokeAsync` runs a turn and returns its result; called from
a turn of the same context, it runs inline. `TryPost` queues a turn without
waiting. `RunicModelContext` is the default implementation, and
`AddRunicNavigation()` registers one per scope unless one is registered.

`RunicModelContext` runs each turn under a `SynchronizationContext` that
queues posted callbacks as new turns, so an `await` in async code started in a
turn, such as a command body, resumes in a later turn, as on a UI thread.
Continuations are queued in the order they become ready. Code after
`ConfigureAwait(false)` or in `Task.Run` leaves the context.
`RunicModelContext.Current` is the context whose turn is running on the current
thread, or `null` outside a turn; adapters use it to route work scheduled in a
turn back to that turn's context. Once the context
is closed, continuations run on the thread pool outside any turn, so the code
still finishes. Navigation hooks don't run in turns (see
[Threading](#threading)).

`RunicModelContextRegistry` associates each model object with the context that
owns its changes. `Bind` registers objects with an application-owned context.
`Acquire` creates a context for a model graph, reuses it for later leases of
the same graph, and disposes it after the last lease is released. A second
context for an object that is already bound is rejected.

Disposing a `RunicModelContext` rejects queued turns and waits for the running
turn. It implements `IRunicModelContextLifetime`: its `Closed` token is cancelled
after admission stops and before queued turns are rejected, so navigators close
immediately, like those using the WPF dispatcher context. A posted turn that
disposal drops is logged (event 1031). Exceptions from close callbacks are reported
through `UnhandledTurnException` (event 1030) and do not interrupt shutdown.

`UnhandledTurnException` is an `EventHandler<RunicModelTurnExceptionEventArgs>`
whose sender is the context. `Exception` is what a posted turn or close callback
threw, or an `ObjectDisposedException` for a posted turn that the context dropped
when it closed, which `Dropped` reports:

```csharp
context.UnhandledTurnException += (sender, e) =>
{
    if (!e.Dropped) logger.LogError(e.Exception, "A posted model turn failed.");
};
```

## Testing

Test a ViewModel through a real navigator rather than a test double. The
navigator needs no UI host: construct it with a `RunicModelContext`, push the
ViewModel, and drive it through its commands. The ViewModel then receives a
real `NavigationEntryContext`, and its results, Backs, guards and
`INavigationAware` notifications behave as in the application.

```csharp
[Fact]
public async Task ConfirmCompletesTheRequest()
{
    await using var model = new RunicModelContext();
    await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = model, Services = services });
    var dialog = navigator.CreateRegion<object>(this);

    var request = dialog.PushForResult<ConfirmViewModel, bool>();
    Assert.True((await request.Transition).IsCommitted);
    await ((ConfirmViewModel)dialog.Current!).Confirm();

    Assert.Equal(true, (await request.Completion as NavigationCompletion<bool>.Completed)?.Value);
    Assert.Null(dialog.Current); // the dialog went back and retired
}
```

- `Services` provides the ViewModel's constructor dependencies, fakes
  included. Without it, constructors that need services fail as
  `Failed(Preparing)`.
- Every navigation result is returned, so assert on it: a guard's veto is
  `Rejected(Guard)`, a throwing hook is `Failed`.
- Code that reads ViewModel state the navigation changed should run in a turn
  (`model.InvokeAsync`) when other threads write it.
- `RunicNavigator.UnretiredEntryCount` counts entries that have not finished
  retiring. After `DisposeAsync`, it is 0 when every entry retired.

## Logging

| Event ID | Name | Level | Logged when | Properties |
| --- | --- | --- | --- | --- |
| 1030 | `ModelTurnFailed` | Error | A posted model-context turn throws. | `ErrorType` |
| 1031 | `ModelTurnDropped` | Warning | Disposal drops a posted turn. | `ErrorType` |
| 1032 | `UnhandledTurnHandlerFailed` | Error | An `UnhandledTurnException` handler throws. | `ErrorType` |
| 1033 | `ModelContextReleaseFailed` | Error | Releasing a model context fails in the background. | `ErrorType` |
| 1060 | `NavigationGuardFailed` | Error | A navigation departure guard throws; the transition fails. A close of the navigator or region is rejected as `Closed` and does not log it. | `Region`, `RegionId`, `Operation`, `EntryType`, `ErrorType` |
| 1061 | `NavigationPreparationFailed` | Error | A navigation factory, the ownership check, binding, initialize or resume throws. A close of the navigator or region is rejected as `Closed` and does not log it. | `Region`, `RegionId`, `Operation`, `EntryType`, `ErrorType` |
| 1062 | `NavigationCommitFailed` | Error | A navigation commit turn cannot run. | `Region`, `RegionId`, `Operation`, `ErrorType` |
| 1063 | `NavigationNotificationFailed` | Error | A navigation region `PropertyChanged` handler throws; the commit stands. | `Region`, `RegionId`, `Property`, `ErrorType` |
| 1064 | `NavigationEntryCleanupFailed` | Error | A retirement step (`Retirement`, `Children`, `Forget`, `Dispose`, `DisposeScope`, `Lease`), the clearing of a closed region (`Close`) or the cancellation of transitions (`Cancel`, a throwing cancellation callback) fails; later steps still run. `EntryType` is `None` for `Close` and `Cancel`. | `Region`, `RegionId`, `EntryType`, `Step`, `ErrorType` |
| 1065 | `NavigationTransitionRejected` | Debug | A navigation request is rejected. | `Region`, `RegionId`, `Operation`, `Reason` |
| 1066 | `NavigationTransitionSuperseded` | Debug | A later request supersedes a navigation request. | `Region`, `RegionId`, `Operation` |
| 1067 | `NavigationSupersededTransitionOverrun` | Warning | A superseded navigation request is still running 5 seconds after supersession. | `Region`, `RegionId`, `Operation` |
| 1068 | `NavigationCloseTimedOut` | Warning | Closing a navigation region timed out waiting for a model turn; its state was cleared outside a turn. The clearing turns of one disposal share one close timeout, so a disposal takes about twice `CloseTimeout` at most. | `Region`, `RegionId` |
| 1069 | `NavigationInitializeTimedOut` | Warning | Retiring an entry stopped waiting for its running initialize hook after the close timeout and disposed the content while the hook runs. During disposal the wait is skipped once the wait for cancelled transitions timed out. | `Region`, `RegionId`, `EntryType` |
| 1070 | `NavigationResultDismissed` | Debug | A `PushForResult` request ends `Dismissed`. `Reason` is `NotCommitted` (its push did not commit), `Retired` (its entry retired without `CompleteAsync`), `Cancelled` (the caller's token was cancelled after the commit), `Dismissed` (the entry called `DismissAsync`) or `Closed` (the navigator closed). | `Region`, `RegionId`, `Reason` |
| 1071 | `NavigationResultDropped` | Debug | `CompleteAsync` went back from an entry whose request had already ended, so its value was dropped. | `Region`, `RegionId` |
| 1072 | `NavigationDepartureActionFailed` | Error | A `NavigationDeparture.OnCommitted` action throws; later actions still run, and the commit stands. | `Region`, `RegionId`, `Operation`, `EntryType`, `ErrorType` |
| 1073 | `NavigatorDisposeFailed` | Error | Disposing the navigator fails unexpectedly, for example started by `Dispose`, which does not wait; entries may not have retired. | `ErrorType` |
| 1074 | `NavigationBackJoined` | Debug | A Back joins the pending Back of its region instead of superseding it. | `Region`, `RegionId` |
| 1075 | `NavigationEntryNotificationFailed` | Error | A `NavigationEntryContext.StateChanged` handler or an `INavigationAware` method throws; the other handlers still run, and the change stands. `Notification` is `StateChanged`, `OnNavigatedTo` or `OnNavigatedFrom`. | `Region`, `RegionId`, `Operation`, `EntryType`, `Notification`, `ErrorType` |
| 1076 | `NavigationArrivalFailed` | Error | An `INavigationArrived` or `INavigationArrived<TInput>` hook throws; the commit stands and reports `Arrival = Failed`. | `Region`, `RegionId`, `Operation`, `EntryType`, `ErrorType` |

Errors and warnings carry the exception. Rejections, supersessions, joined Backs and
dismissed results log at Debug. The properties are `Region` (the `TContent`
type name), `RegionId` (a per-navigator number that tells apart regions with
the same `TContent`), `Operation`, `EntryType`, `Step`, `Reason`, `Property`
(event 1063, the region property whose handler threw), `Notification` (event 1075)
and `ErrorType`, never
values.

- Events 1060-1076 use the category `Runic.Navigation`
  (`RunicNavigator.LogCategory`), from `RunicNavigatorOptions.LoggerFactory`
  or the scope's `ILoggerFactory` with `AddRunicNavigation()`.
- Events 1030-1033 use the logger of the `RunicModelContext`:
  `ILogger<RunicModelContext>` (category `Runic.Navigation.RunicModelContext`)
  when DI or a Runic.Application.Views window session with a logger factory created
  it. Event 1033 is also logged by the registry when a context it created
  fails to shut down.
- Without a logger factory, each entry is written to
  `System.Diagnostics.Trace` (`TraceError`, or `TraceWarning` for warnings) as
  its formatted message followed by the exception.

Navigation reserves events 1060-1079: 1060-1069 for transitions and cleanup,
and 1070-1079 for results and later navigation events, of which 1077-1079 are
not used yet. Events 1044-1049 are reserved for the ReactiveUI navigation
adapter.
