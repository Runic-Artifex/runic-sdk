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
[Runic.Application](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md)
depends on it and presents regions in web Views.

## Install

```sh
dotnet add package Runic.Navigation --prerelease
```

Runic.Application apps already have it through that package.

## Experimental API

`RunicNavigator` owns typed navigation regions. Each region gives its entries
stable ids, and it supports awaited departure guards, initialize-once and
resume-on-return hooks, and owned or borrowed content. The navigation API is
experimental: every type is marked `[Experimental("RUNICNAV001")]`. Suppress
`RUNICNAV001` to use it, and expect changes before it is supported. The model
context types are not experimental. The navigator replaces ReactiveUI's
`RoutingState`; it does not wrap one. See the
[ReactiveUI adapter](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md)
for observables and a back command.

```csharp
using Runic.Navigation;

services.AddRunicNavigation(); // one model context and navigator per window scope

var main = navigator.CreateRegion<IMainViewModel>(this, NavigationTarget.Borrow<IMainViewModel>(home));
var result = await main.PushAsync(NavigationTarget.Create<IMainViewModel>(services => new DocumentViewModel(
    services.GetRequiredService<EditorViewModel>())));
if (result is NavigationResult<IMainViewModel>.Committed) { /* main.Current is the document */ }
await main.BackAsync(); // the document retires and is disposed; home resumes
```

`AddRunicNavigation()` registers a scoped `IRunicModelContext` and a scoped
`RunicNavigator` that uses it. Dispose window scopes with `DisposeAsync`
(`CreateAsyncScope`). The navigator is also `IDisposable` for containers that
only dispose synchronously: `Dispose` starts the same shutdown and returns
without waiting for it. Without DI, construct the navigator with
`RunicNavigatorOptions` (`ModelContext`, `Services`, `LoggerFactory`,
`TimeProvider`, `CloseTimeout`), and use the same model context for the
window's session.

### Operations and results

| Operation | Effect |
| --- | --- |
| `PushAsync` | The current entry is retained, and the new entry becomes current. |
| `BackAsync` | The current entry retires, and the top retained entry resumes. With fewer than two entries the request is `Rejected(NoHistory)`. |
| `BackToAsync(id)` | Every entry above the target retires, and the target resumes. |
| `ReplaceAsync` | The current entry retires, and the history is unchanged. |
| `ResetAsync` | Every entry retires, and the new entry becomes the root. |
| `ClearHistoryAsync` | The retained entries retire, and the current entry stays. |
| `ClearAsync` | Every entry retires, and the region becomes empty. |

- Every operation returns a `NavigationResult<TContent>`. The outcomes are
  returned, never thrown:
  - `Committed`, with the new current entry and the retired entries.
  - `Rejected`, with a `Reason`: `Guard`, `Cancelled`, `Closed`, `Reentrant`,
    `NoHistory`, `NotCurrent` or `EntryNotFound`.
  - `Failed`, with the `Phase` (`Guarding`, `Preparing` or `Committing`) and
    the exception (`Error`).
  - `Superseded`.

  Operations throw only for argument and ownership errors.
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
4. Owned content only: the content is disposed, outside model turns.
5. Owned content only: its model-context lease is released.

A failed step is logged as event 1064.

### Transitions

| Phase | Where | What happens |
| --- | --- | --- |
| Admission | Synchronously, in the call | The request is rejected at once for these reasons: `Closed`; `Reentrant`, when the caller is inside a hook of a transition in this region, an ancestor or a descendant, for example a `CurrentPane` guard that calls `Main.BackAsync()`; `NotCurrent`; `NoHistory`; or `Cancelled`, for a token that is already cancelled. Otherwise the request supersedes every earlier request of the region that has not started committing. It also supersedes those of the child regions its plan affects. |
| Guarding | Outside model turns | The request first waits until earlier requests of the region, and those of the child regions in its plan, have ended. Then departure guards run one at a time, deepest first, also when a push only retains the current entry (`NavigationDepartureKind.Retain`). `false` rejects the request as `Guard`. A guard that throws fails it (event 1060). |
| Preparing | Outside model turns | A new entry runs its factory, the ownership check, model-context binding and `InitializeAsync`, exactly once. A resumed entry runs `ResumeAsync`, which runs again on a retry. A step that throws fails the request (event 1061), and its pending entry retires. |
| Committing | One model turn | The turn re-checks supersession, `ExpectedCurrent`, the target and the versions of the affected regions. Then it applies the new stacks and child policies and raises `PropertyChanged`. A handler that throws is logged (event 1063; its `Property` names the region property), and the commit stands. If the turn cannot run, for example because the model context throws, the request fails as `Failed(Committing)` (event 1062), nothing changes, and its pending entry retires. If the model context is already closed, the request is `Rejected(Closed)` instead, without event 1062, and the navigator starts closing. |
| Committed | Outside model turns | Admission is released, and departing entries retire. The returned `ValueTask` completes after that cleanup. |

- A request is superseded only before it commits, never after. A cancelled
  token gives `Rejected(Cancelled)` and navigator disposal gives
  `Rejected(Closed)`, in any phase before the commit.
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
  guards to be repeatable; the user may see the same prompt twice.

### Ownership

| Target | Who creates the content | On retirement |
| --- | --- | --- |
| `Borrow(content)` | The caller or a container | Nothing: the content is never disposed or forgotten, and the regions it owns are left alone. |
| `Own(content)` | The caller, with `new`, handing it over | Child regions close, presentations are forgotten, the content is disposed and its lease is released. |
| `Create(factory)` / `Create(factory, input)` | The factory, with `new`, when the transition prepares | As `Own`. |

- Content resolved from a container (a window scope, the root provider or
  Splat) belongs to that container, so `Borrow` it. Use `Own` and `Create` only
  for content constructed with `new`. The factory receives the window's
  `IServiceProvider` to resolve constructor dependencies. Those dependencies
  stay container-owned, so don't resolve disposable transients for it.
- An instance can be owned once. `Own` of an instance the navigator has
  already owned, whether live or retired, throws `InvalidOperationException`.
  A `Create` factory that returns one gives `Failed(Preparing)`.
- Present owned content only through its region's slot. Retirement forgets
  every presentation of the instance.
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
  `INavigationInitialize<TInput>`, and `Create(factory, input)` cannot be an
  initial target.

### Presenting regions

A host presents a region's `Current`. In Runic.Application, a get-only
`NavigationRegion<TContent>` property of a ViewModel is a content slot that
the generated Bridge presents in the window's web View; see
[Content slots](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#content-slots).
Other hosts bind `INavigationRegion`, the non-generic view of a region, and
`INavigationEntry`. `RunicNavigator.AttachPresentation` is for presentation
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

The confirm example below follows the
[Notes example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-view-first).
Its guard only decides. Guards can run again for the same departure, and a
guard's `true` is not a commit, because a later request can still supersede
the Back. So the guard reads the draft on a model turn and asks again on each
run, and the draft is discarded only when `Main` leaves the document, in the
commit turn. The code that starts the Back forgets the guard's yes when the
Back ends without committing, so a later departure that runs no guards, such
as the window closing, keeps the draft. Cancel must always end the question.
If its `CompleteAsync` is rejected, for example because something was pushed
over the confirm, the confirm cancels a token that the guard linked into the
request. That dismisses the request instead.

```csharp
// The document's departure guard asks in a sibling Dialog region.
public async ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken token)
{
    _discardOnDeparture = false;
    if (departure.Kind != NavigationDepartureKind.Retire) return true;
    if (!await _context.InvokeAsync(() => Editor.IsDirty)) return true; // guards run outside model turns
    var confirm = new ConfirmNavigationViewModel("Discard the unsaved edits?");
    using var answer = CancellationTokenSource.CreateLinkedTokenSource(token, confirm.Dismissal);
    var request = dialog.PushForResult<bool>(NavigationTarget.Own<IDialogViewModel>(confirm),
        cancellationToken: answer.Token);
    if (await request.Completion is not NavigationCompletion<bool>.Completed { Value: true }) return false;
    _discardOnDeparture = true; // discard when the Back commits, not here
    return true;
}

// In the workspace: the caller of the Back forgets the yes when the Back is superseded,
// rejected or fails.
public async Task OpenHomeAsync()
{
    var leaving = main.Current as DocumentViewModel;
    var committed = false;
    try { committed = await main.BackAsync() is NavigationResult<IMainViewModel>.Committed; }
    finally { if (!committed) leaving?.ForgetConfirmedDeparture(); } // also when Back throws
}

// In the document:
internal void ForgetConfirmedDeparture() => _discardOnDeparture = false;

// Main's PropertyChanged handler runs in the commit turn.
private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
{
    if (e.PropertyName == nameof(main.Current) && _discardOnDeparture && !ReferenceEquals(main.Current, this))
    { _discardOnDeparture = false; Editor.DiscardChanges(); }
}

// The confirm captures its entry in InitializeAsync and answers with it.
public ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken token)
{ _entry = entry; return ValueTask.CompletedTask; }
private Task Confirm() => _entry.CompleteAsync(true).AsTask();
private async Task Cancel()
{
    if (await _entry.CompleteAsync(false) is not NavigationResult<object>.Committed)
        await _dismissal.CancelAsync(); // _dismissal.Token is Dismissal
}
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
  Issue the request after the transition commits.

## Model context

`IRunicModelContext` runs short synchronous turns one at a time, in posting
order. Navigation commits run as turns, and region `PropertyChanged` handlers
run inside them. `InvokeAsync` runs a turn and returns its result; called from
a turn of the same context, it runs inline. `TryPost` queues a turn without
waiting. `RunicModelContext` is the default implementation, and
`AddRunicNavigation()` registers one per scope unless one is registered.

`RunicModelContextRegistry` associates each model object with the context that
owns its changes. `Bind` registers objects with an application-owned context.
`Acquire` creates a context for a model graph, reuses it for later leases of
the same graph, and disposes it after the last lease is released. A second
context for an object that is already bound is rejected.

Disposing a `RunicModelContext` rejects queued turns and waits for the running
turn. A posted turn that disposal drops is logged (event 1031).

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
| 1064 | `NavigationEntryCleanupFailed` | Error | A retirement step (`Retirement`, `Children`, `Forget`, `Dispose`, `Lease`), the clearing of a closed region (`Close`) or the cancellation of transitions (`Cancel`, a throwing cancellation callback) fails; later steps still run. `EntryType` is `None` for `Close` and `Cancel`. | `Region`, `RegionId`, `EntryType`, `Step`, `ErrorType` |
| 1065 | `NavigationTransitionRejected` | Debug | A navigation request is rejected. | `Region`, `RegionId`, `Operation`, `Reason` |
| 1066 | `NavigationTransitionSuperseded` | Debug | A later request supersedes a navigation request. | `Region`, `RegionId`, `Operation` |
| 1067 | `NavigationSupersededTransitionOverrun` | Warning | A superseded navigation request is still running 5 seconds after supersession. | `Region`, `RegionId`, `Operation` |
| 1068 | `NavigationCloseTimedOut` | Warning | Closing a navigation region timed out waiting for a model turn; its state was cleared outside a turn. The clearing turns of one disposal share one close timeout, so a disposal takes about twice `CloseTimeout` at most. | `Region`, `RegionId` |
| 1069 | `NavigationInitializeTimedOut` | Warning | Retiring an entry stopped waiting for its running initialize hook after the close timeout and disposed the content while the hook runs. During disposal the wait is skipped once the wait for cancelled transitions timed out. | `Region`, `RegionId`, `EntryType` |
| 1070 | `NavigationResultDismissed` | Debug | A `PushForResult` request ends `Dismissed`. `Reason` is `NotCommitted` (its push did not commit), `Retired` (its entry retired without `CompleteAsync`), `Cancelled` (the caller's token was cancelled after the commit) or `Closed` (the navigator closed). | `Region`, `RegionId`, `Reason` |
| 1071 | `NavigationResultDropped` | Debug | `CompleteAsync` went back from an entry whose request had already ended, so its value was dropped. | `Region`, `RegionId` |
| 1073 | `NavigatorDisposeFailed` | Error | Disposing the navigator fails unexpectedly, for example started by `Dispose`, which does not wait; entries may not have retired. | `ErrorType` |

Errors and warnings carry the exception. Rejections, supersessions and
dismissed results log at Debug. The properties are `Region` (the `TContent`
type name), `RegionId` (a per-navigator number that tells apart regions with
the same `TContent`), `Operation`, `EntryType`, `Step`, `Reason`, `Property`
(event 1063, the region property whose handler threw) and `ErrorType`, never
values.

- Events 1060-1073 use the category `Runic.Navigation`
  (`RunicNavigator.LogCategory`), from `RunicNavigatorOptions.LoggerFactory`
  or the scope's `ILoggerFactory` with `AddRunicNavigation()`.
- Events 1030-1033 use the logger of the `RunicModelContext`:
  `ILogger<RunicModelContext>` (category `Runic.Navigation.RunicModelContext`)
  when DI or a Runic.Application window session with a logger factory created
  it. Event 1033 is also logged by the registry when a context it created
  fails to shut down.
- Without a logger factory, each entry is written to
  `System.Diagnostics.Trace` (`TraceError`, or `TraceWarning` for warnings) as
  its formatted message followed by the exception.

Navigation reserves events 1060-1079: 1060-1069 for transitions and cleanup,
and 1070-1079 for results and later navigation events, of which 1072 and
1074-1079 are not used yet. Events 1043-1049 are reserved for the ReactiveUI navigation
adapter.
