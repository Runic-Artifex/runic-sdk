# Runic.Navigation

Typed, host-neutral navigation for .NET ViewModels. `RunicNavigator` owns
navigation regions with stable entry ids, awaited departure guards,
initialize-once and resume-on-return hooks, results from an entry, and owned or
borrowed content. `IRunicModelContext` serializes the ViewModel changes that
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
| ReactiveUI ViewModels | `Runic.Navigation.ReactiveUI` (or its System.Reactive flavor) provides observables and a Back command whose `NavigationOutcome` a View can bind. Model-context scheduling comes from `Runic.Application.Views.ReactiveUI` in a Views app. Compose native ReactiveUI commands for other operations. |
| CommunityToolkit ViewModels | Use the core's async operations directly with `AsyncRelayCommand` or `[RelayCommand]`, forwarding the command's cancellation token. No additional navigation adapter is needed. |

An integration can optimize its framework's API without duplicating navigation
behavior. Replacing WPF Views with web Views can retain the navigation engine
and ViewModels. Keep native UI activation and dispatcher concerns in the
presentation integration; retained entry lifetime belongs to navigation.

The [Toolkit WPF example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/wpf-navigation)
and [ReactiveUI recipes](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md#native-command-composition)
show each framework's native command style.

## Experimental API

`RunicNavigator` owns typed navigation regions. Each region gives its entries
stable ids, and it supports awaited departure guards, initialize-once and
resume-on-return hooks, and owned or borrowed content. The navigation API is
experimental: every type is marked `[Experimental("RUNICNAV001")]`. Suppress
`RUNICNAV001` to use it, and expect changes before it is supported;
[Experimental APIs](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/experimental.md#RUNICNAV001)
shows how and lists the criteria for stabilizing it. The model
context types are not experimental. The navigator replaces ReactiveUI's
`RoutingState`; it does not wrap one. See the
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

### Operations and results

| Operation | Effect |
| --- | --- |
| `PushAsync` | The current entry is retained, and the new entry becomes current. |
| `BackAsync` | The current entry retires, and the top retained entry resumes. With fewer than two entries the request is `Rejected(NoHistory)`. |
| `BackToAsync(id)` | Every entry above the target retires, and the target resumes. |
| `ReplaceAsync` | The current entry retires, and the history is unchanged. |
| `ReplaceBorrowedAsync` on `INavigationRegion` | A host selects an existing model without knowing the generic content type. The model is type-checked, borrowed, and replaced through the same guards and engine. |
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
  - `Committed`, with the new current entry and the retired entries.
  - `Rejected`, with a `Reason`: `Guard`, `Cancelled`, `Closed`, `Reentrant`,
    `NoHistory`, `NotCurrent` or `EntryNotFound`.
  - `Failed`, with the `Phase` (`Guarding`, `Preparing` or `Committing`) and
    the exception (`Error`).
  - `Superseded`.

  Operations throw only for argument and ownership errors.

  `IsCommitted` tells code after the request whether to continue, without a
  pattern match on the content type. `ThrowIfFailed()` rethrows a `Failed`
  outcome's `Error` with its original stack trace and returns the result
  otherwise, so a caller that treats failures as bugs can write
  `if ((await main.PushAsync<EditorViewModel>()).ThrowIfFailed().IsCommitted) { ... }`.
- `result.Outcome` is a `NavigationOutcome`: the `Kind` (`Committed`,
  `Rejected`, `Superseded` or `Failed`) and, for a rejection, its `Rejection`
  reason. It holds no .NET content or exception, so a ViewModel can return it to
  a Runic View, which cannot receive a `NavigationResult<TContent>`.
- `NavigationRequestOptions.ExpectedCurrent` makes a request conditional. It
  is rejected as `NotCurrent` if that entry is not current at admission or at
  commit. Use it, or `NavigationEntryContext.BackAsync`, for work that
  completes late, such as a double click or a dialog that answers after the
  user moved on.
- A region exposes `Current`, `CurrentEntry`, `History`, `CanGoBack` and
  `IsTransitioning`. It raises `PropertyChanged` for them inside a model turn,
  the commit turn for a navigation.

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
| Admission | Synchronously, in the call | The request is rejected at once for these reasons: `Closed`; `Reentrant`, when the caller is inside a hook of a transition in this region, an ancestor or a descendant, for example a `CurrentPane` guard that calls `Main.BackAsync()`; `NotCurrent`; `NoHistory`; or `Cancelled`, for a token that is already cancelled. Otherwise the request supersedes every earlier request of the region that has not started committing, unless it is a Back that joins a pending Back (see below). It also supersedes those of the child regions its plan affects. |
| Guarding | Outside model turns | The request first waits until earlier requests of the region, and those of the child regions in its plan, have ended. Then departure guards run one at a time, deepest first, also when a push only retains the current entry (`NavigationDepartureKind.Retain`). `false` rejects the request as `Guard`. A guard that throws fails it (event 1060). |
| Preparing | Outside model turns | A new entry runs its factory, the ownership check, model-context binding and `InitializeAsync`, exactly once. A resumed entry runs `ResumeAsync`, which runs again on a retry. A step that throws fails the request (event 1061), and its pending entry retires. |
| Committing | One model turn | The turn re-checks supersession, `ExpectedCurrent`, the target and the versions of the affected regions. Then it applies the new stacks and child policies and raises `PropertyChanged`. A handler that throws is logged (event 1063; its `Property` names the region property), and the commit stands. If the turn cannot run, for example because the model context throws, the request fails as `Failed(Committing)` (event 1062), nothing changes, and its pending entry retires. If the model context is already closed, the request is `Rejected(Closed)` instead, without event 1062, and the navigator starts closing. |
| Committed | Outside model turns | Admission is released, and departing entries retire. The returned `ValueTask` completes after that cleanup. |

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

### Ownership

| Target | Who creates the content | On retirement |
| --- | --- | --- |
| `Borrow(content)` | The caller or a container | Nothing: the content is never disposed or forgotten, and the regions it owns are left alone. |
| `Own(content)` | The caller, with `new`, handing it over | Child regions close, presentations are forgotten, the content is disposed and its lease is released. |
| `Create(factory)` / `Create(factory, input)` | The factory, with `new`, when the transition prepares | As `Own`, then the entry's scope is disposed. |
| `Create<T>()` / `Create<T, TInput>(input)` | The navigator, with `ActivatorUtilities`, when the transition prepares | As `Own`, then the entry's scope is disposed. |

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
- Borrowed selection hosts use caller/container-owned models. Do not re-borrow
  content from a navigator-owned entry into another entry; retirement of its
  owning entry still disposes that content.
- Owned content is disposed outside model turns, on the model's thread when the
  context schedules hooks (see Threading). A `Dispose` that touches
  shared model state uses `ModelContext.InvokeAsync`, and it must not await
  navigation on the same navigator.
- A region's owner can be any object. Regions owned by an owned entry's
  content are that entry's children, and they close when it retires.
  `NavigationRegionOptions.WhileParentRetained` decides what a child region
  does while its parent entry is retained: `Keep` (the default), `ResetToRoot`
  or `Clear`. Regions owned by borrowed content or by the root close only
  with the navigator.
- An initial target (`CreateRegion(owner, initial)`) becomes current without a
  transition. Its content must not implement `INavigationInitialize` or
  `INavigationInitialize<TInput>`, and `Create(factory, input)` and
  `Create<T, TInput>(input)` cannot be initial targets.

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
- `INavigationEntry.Services` and `NavigationEntryContext.Services` return the
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
Other hosts bind `INavigationRegion`, the non-generic view of a region, and
`INavigationEntry`. `Runic.Navigation.Wpf` provides WPF hosts and a dispatcher
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
closes. The dialog region must not be the guarded region, one of its
ancestors or one of its descendants: that push would be
`Rejected(Reentrant)`.

`LeaveConfirmation` keeps the caller's context, so its confirm delegate stays
on the UI thread. Without a hook scheduler the guard can run in the frame of
the call that started the navigation. If that call blocks a UI thread on the
navigation's result (for example with `GetAwaiter().GetResult()`), the guard's
continuation can never run there, and the two deadlock. Await navigation
results; don't block on them, as with any asynchronous code.

The [Notes example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-view-first)
asks this way:

```csharp
// The document: the guard forwards to a LeaveConfirmation.
_leave = LeaveConfirmation.InDialog(navigation.Dialog,
    () => NavigationTarget.Own<IDialogViewModel>(new ConfirmNavigationViewModel("Discard the unsaved edits?")),
    () => Editor.IsDirty, Editor.DiscardChanges);

public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken token) =>
    _leave.CanDepartAsync(departure, token);

// The confirm captures its entry in InitializeAsync and answers with it.
public ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken token)
{ _entry = entry; return ValueTask.CompletedTask; }
private Task Confirm() => _entry.CompleteAsync(true).AsTask();
private Task Cancel() => _entry.DismissAsync().AsTask();
```

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

Window close does not run guards. Disposing a navigator cancels window-owned
operations that its owned content started, and then disposes that content.

`WhenIdleAsync()` waits until no transition or retirement is running.

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
  while that hook runs.
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
  Create the region empty and reset it after construction:
  `await navigation.Region.ResetAsync<HomeViewModel>()`, for example from
  application startup.

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
immediately, like those using the WPF dispatcher context. Like the interface,
`Closed` is experimental (`RUNICNAV001`). A posted turn that
disposal drops is logged (event 1031). Exceptions from close callbacks are reported
through `UnhandledTurnException` (event 1030) and do not interrupt shutdown.

## Testing

`RunicNavigator.UnretiredEntryCount` counts entries that have not finished
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

Errors and warnings carry the exception. Rejections, supersessions, joined Backs and
dismissed results log at Debug. The properties are `Region` (the `TContent`
type name), `RegionId` (a per-navigator number that tells apart regions with
the same `TContent`), `Operation`, `EntryType`, `Step`, `Reason`, `Property`
(event 1063, the region property whose handler threw) and `ErrorType`, never
values.

- Events 1060-1074 use the category `Runic.Navigation`
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
and 1070-1079 for results and later navigation events, of which 1075-1079 are
not used yet. Events 1044-1049 are reserved for the ReactiveUI navigation
adapter.
