# ReactiveUI adapter for Runic.Navigation

`Runic.Navigation.ReactiveUI` connects [Runic.Navigation](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md)
regions to ReactiveUI 26 (the `ReactiveUI.Primitives` flavor). It depends on
`Runic.Navigation` and `ReactiveUI` only: no `Runic.Application.Views`, generator or
host, so it works in a WPF or console app. It provides, in the
`Runic.Navigation.ReactiveUI` namespace, `WhenCurrentChanged()`,
`WhenCurrentEntryChanged()`, `WhenCanGoBackChanged()`, `WhenIsTransitioningChanged()`,
`WhenIsBusyChanged()` and `CreateBackCommand()` for `NavigationRegion<TContent>`
(in `ReactiveNavigationRegionExtensions`); `WhenNavigatedTo()`,
`WhenNavigatedFrom()`, `CreateCompleteCommand()`, `CreateDismissCommand()` and
`CreateCloseCommand()` for an entry's `NavigationEntryContext` (in
`ReactiveNavigationEntryExtensions`); and
`ReactiveLeaveConfirmation.WithInteraction()`, a leave confirmation asked through a
ReactiveUI `Interaction`.

Apps that use the System.Reactive distribution of ReactiveUI reference
[`Runic.Navigation.ReactiveUI.Reactive`](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI.Reactive/README.md)
instead. Runic Views apps reference this package next to
`Runic.Application.Views.ReactiveUI`, which no longer brings it, so an app without
navigation does not reference it.

```sh
dotnet add package Runic.Navigation.ReactiveUI --prerelease
```

## Walkthrough

This section builds master/detail navigation from ReactiveUI ViewModels: pages,
a leave guard, a result, and a guarded window close. The later sections are the
reference. The code comes from two examples:

- [Reactive Notes](https://github.com/Runic-Artifex/runic-sdk/blob/main/examples/notes-reactive-views/README.md)
  is a Runic Views app (web Views).
- [NotesNavigation.ReactiveUI](https://github.com/Runic-Artifex/runic-sdk/blob/main/examples/wpf-navigation/README.md#the-reactiveui-variant)
  is a WPF app.

Neither passes a scheduler. A Runic Views app calls
`AddRunicReactiveModelContext()` and `AddRunicNavigation()`. A WPF app calls
`RxAppBuilder.CreateReactiveUIBuilder().WithWpf().BuildApp()` and
`AddRunicWpfNavigation()`. Either way, ReactiveUI's main-thread scheduler is
the navigator's model context (see [Scheduling](#scheduling)).

**1. Pages.** The shell owns a region and exposes it as a get-only property.
A web View presents it as a content slot, and a WPF `NavigationHost` binds to
it. Back is a command, and a page opens from an ordinary `ReactiveCommand`:

```csharp
Main = navigator.CreateRegion<IMainPage>(this, NavigationTarget.Borrow<IMainPage>(home));
OpenHomeCommand = Main.CreateBackCommand(); // enabled while Main has history
OpenDocumentCommand = ReactiveCommand.CreateFromTask(OpenDocumentAsync,
    Main.WhenCurrentChanged().Select(page => page is HomeViewModel));
```

Neither command needs disposing: they observe the shell's own region (see
[Command lifetime](#command-lifetime)). In a Runic Views app, neither needs a
`ThrownExceptions` subscription either: the generated Bridge logs an unexpected
exception.

The navigator constructs a pushed page from the container. The page takes its
`NavigationEntryContext` as a constructor parameter, or receives a typed input
in `INavigationInitialize<TInput>` (`PushAsync<NoteDetailViewModel, int>(id)`).

**2. Arrivals.** Per-entry work goes in `entry.WhenNavigatedTo`, which runs
each time the entry becomes current, including after a Back. Its scope ends
when the entry is left. `WhenActivated` follows a mounted View instead:

```csharp
entry.WhenNavigatedTo((_, visit) => editor.WhenAnyValue(model => model.IsDirty)
    .Subscribe(dirty => Status = dirty ? "Unsaved changes" : "No unsaved changes")
    .DisposeWith(visit));
```

The subscription to the window-scoped editor ends with the visit scope.
`WhenNavigatedTo` itself ends when the entry retires.

**3. The leave guard.** The page implements `INavigationDepartureGuard` with
`ReactiveLeaveConfirmation.WithInteraction`. Every exit asks the guard,
including Back, a push from code, a parent region leaving and a guarded window
close. The discard runs only when the departure commits:

```csharp
_leave = ReactiveLeaveConfirmation.WithInteraction(ConfirmLeave,
    () => new DiscardNoteRequest(editor.Title, editor.Body.Length), () => editor.IsDirty, editor.RevertChanges);

public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
    _leave.CanDepartAsync(departure, cancellationToken);
```

The presentation answers the interaction. A WPF View answers it in
`WhenActivated`, and a web View answers it with
`view.interactions.confirmLeave.handle(...)` for a departure its own window
starts: the page's Close, the shell's Back or a guarded window close. A .NET
fallback handler that the ViewModel registers answers when no View does, such as
for navigation that .NET code starts (see
[Leave confirmation through an interaction](#leave-confirmation-through-an-interaction)).
A web View registers its handler when it mounts, so a departure during the
page's first render, such as a close just as the page opened, gets the fallback
too.

**4. Results.** The caller pushes for a result and awaits its completion. It
doesn't need `ToTask()` or a scheduler:

```csharp
var request = Main.PushForResult<DocumentViewModel, string>();
if ((await request.Transition).IsCommitted)
    _home.LastResult = await request.Completion is NavigationCompletion<string>.Completed(var title)
        ? $"Saved and closed {title}" : "Closed the document.";
```

Reactive Notes awaits `Completion` outside the command, so its bridged command
ends when the push settles rather than when the document closes.

The page ends its entry with result commands that the View binds like any other
command:

```csharp
SaveAndCloseCommand = entry.CreateCompleteCommand(async token =>
{
    await editor.SaveAsync(token);
    return editor.Title;
});
CloseCommand = entry.CreateCloseCommand(); // asks the guard when dirty, disposed with the entry
```

A confirm dialog uses `entry.CreateCompleteCommand(_ => Task.FromResult(true))`
for Yes and `entry.CreateDismissCommand()` for No. If the caller passes a
command's token to `PushForResult`, cancelling that token after the commit
closes the entry. So pass the token only from a command that waits for the
completion, such as Delete waiting for its confirm. Don't pass it from a command
that ends when the push commits.

**5. The window close.** Disposing a navigator retires its entries without
asking their guards. To ask the guards before the user closes the window,
opt in on the window:

- In a Runic Views app, set `GuardClose = true` on the `RunicWindow<T>`. Its
  default `CanCloseAsync` clears the regions that the ViewModel exposes, and
  `RequestCloseAsync()` closes from code the way the close button does. See
  [Guarding a user close](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#guarding-a-user-close).
- In WPF, set `GuardWindowClose="True"` on the `NavigationHost`.

In a Runic Views app, the Window opts in. A ViewModel that keeps the Window
`OpenWindowAsync` returned closes it from a `ReactiveCommand` the way the close
button does:

```csharp
public sealed partial class EditorWindow : RunicWindow<EditorViewModel>
{
    public EditorWindow() => GuardClose = true;
}

CloseEditorCommand = ReactiveCommand.CreateFromTask(async token =>
{
    if (!await _editor.RequestCloseAsync(token)) StatusText = "The editor has unsaved changes.";
});
```

The leave guard from step 3 then decides. In a Runic Views app the close is the
window's own departure, so the page's mounted web View asks the user, as it does
for its Close and the shell's Back. A WPF View answers in `WhenActivated`.

## Scheduling

The adapter neither sets nor needs a scheduler. Like any `ReactiveCommand`, the
back command delivers its output and `IsExecuting` on ReactiveUI's
`RxSchedulers.MainThreadScheduler` unless you pass an `outputScheduler`:

- In a Runic Views app, `AddRunicReactiveModelContext()` from
  `Runic.Application.Views.ReactiveUI` makes that scheduler the model context of the
  turn that executes the command. See its
  [model-context scheduling](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#model-context-scheduling).
- In a WPF app, it is the dispatcher.
- Elsewhere, such as a console app or a test, pass a scheduler that runs work on
  the model context when the notifications must be ordered with model state.

`AddRunicReactiveModelContext()`, `ReactiveSchedulers` and
`IReactiveSchedulerProvider` are in `Runic.Application.Views.ReactiveUI`.

## Navigation

`RunicNavigator` regions replace `RoutingState` rather than wrap it: a region
has awaited departure guards, stable entry ids, owned content and supersession,
which `RoutingState`'s mutable stack and synchronous `Navigate` cannot enforce.

```csharp
BackCommand = Main.CreateBackCommand(); // holds a handler on this ViewModel's own region

Main.WhenCurrentChanged()          // TContent?, distinct by instance, on the model turn
    .Select(current => current is DocumentViewModel)
    .Subscribe(isDocument => IsDocumentOpen = isDocument);
```

- `WhenCurrentChanged()` emits the current content (`null` when the region is
  empty) on subscription and then each different instance.
  `WhenCurrentEntryChanged()` emits each `NavigationEntry<TContent>`, also when two
  entries present the same borrowed or shared instance, as after
  `ReplaceAsync(NavigationTarget.Share(current))`.
- `WhenCanGoBackChanged()` emits whether the region has Back history.
  `WhenIsTransitioningChanged()` emits whether it has an unsettled transition,
  including one started by another caller. They emit initial values and then
  distinct values observed at region change notifications. Neither includes
  an ancestor region's state. Back history availability stays true while a
  guard waits; combine it with transition state when composing a command.
- `WhenIsBusyChanged()` emits whether a request of the region has not
  completed (`IsBusy`). It stays true after the commit until the request's
  task completes, while departed entries retire. Use it for a region created
  with `Admission = NavigationAdmission.RejectWhileBusy`, which refuses
  requests as `Rejected(Busy)` meanwhile.
- All five emit the initial value on the subscribing thread, inside `Subscribe`,
  and later values on the model turn that raises the change. Content changes
  arrive on the navigation's commit turn. Use `ObserveOn` to deliver elsewhere.
  They never complete, and stop when the subscription is disposed.
- `CreateBackCommand()` returns a `ReactiveCommand<RxVoid, NavigationOutcome>`;
  pass an `outputScheduler` to choose where it delivers, and a `canExecute`
  observable, such as "not saving", to add a condition: the command can execute
  while both the region's condition and the latest `canExecute` value are true,
  and not before `canExecute` emits. `NavigationOutcome` is
  the result's `Kind` (`Committed`, `Rejected`, `Superseded` or `Failed`) and,
  for a rejection, its `Rejection` reason, without .NET content, so a Runic View
  can bind the command. For the full `NavigationResult<TContent>`, such as a
  failure's exception, await `BackAsync` in your own command. The command can
  execute while `CanGoBack` is true and `IsTransitioning` is false, including
  transitions it did not start, and, in a region that rejects requests while
  busy, while `IsBusy` is false. `CanExecute` turns false in the same
  notification that starts a transition, without a scheduler hop.
  It reflects this region only: a transition of
  an ancestor region does not disable it. The command observes the region
  until it is disposed: dispose it only when the region outlives the command's
  owner (see [Command lifetime](#command-lifetime)). A rejected, superseded
  or failed Back is its output, not an exception, so `ThrownExceptions`
  carries only defects and cancellation. In a Runic Views app the generated
  Bridge logs them; elsewhere, subscribe to `ThrownExceptions`.
  The Back's task, and so the command's `IsExecuting`, includes the resumed
  entry's arrival hook (`INavigationArrived`), such as a data load.
  Overlapping executions to the same destination share one Back and confirmation.
  Each caller cancels independently; the shared Back cancels only after every
  caller cancels before commit.
- **ReactiveUI activation is not entry lifetime.** It follows a mounted
  View. A navigation entry lives from its push until it retires: a retained
  entry stays alive and keeps its state while nothing presents it, and its View
  deactivates and activates again when the entry returns. Put per-presentation
  subscriptions in `WhenActivated`. Use `WhenNavigatedTo` (below), the
  navigator's hooks (`INavigationInitialize`, `INavigationAware`,
  `INavigationDepartureGuard`) and the entry's `Retirement` token for per-entry
  work, and `Dispose` for owned content.

## Entry lifecycle and result commands

Content that the navigator creates takes its `NavigationEntryContext` as a
constructor parameter (`NavigationTarget.Create<T>()` and the region methods that
wrap it). The extension methods on the entry context follow the entry's core
`State`/`StateChanged`, so they keep no state or history of their own:

```csharp
public sealed record DiscardPrompt(string ItemName);

public sealed class EditViewModel : ReactiveObject, IPage, INavigationDepartureGuard
{
    private readonly LeaveConfirmation _leave;

    public EditViewModel(ItemStore store, NavigationEntryContext entry)
    {
        entry.WhenNavigatedTo((arrival, scope) =>
        {
            // Each arrival: the push that created the entry, then each Back or BackTo that resumes it.
            if (arrival.Operation is NavigationOperation.Back) Refresh.Execute().Subscribe().DisposeWith(scope);
        });

        _leave = ReactiveLeaveConfirmation.WithInteraction(
            ConfirmDiscard, () => new DiscardPrompt(Name), () => IsDirty, discard: () => Name = _original.Name);

        SaveCommand = entry.CreateCompleteCommand(ct => store.SaveAsync(_original with { Name = Name }, ct),
            this.WhenAnyValue(vm => vm.IsDirty));
        CancelCommand = entry.CreateDismissCommand();
        CloseCommand = entry.CreateCloseCommand(); // asks when dirty
    }

    public Interaction<DiscardPrompt, bool> ConfirmDiscard { get; } = new();
    public ReactiveCommand<RxVoid, NavigationOutcome> SaveCommand { get; }
    public ReactiveCommand<RxVoid, NavigationOutcome> CancelCommand { get; }
    public ReactiveCommand<RxVoid, NavigationOutcome> CloseCommand { get; }

    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken ct) =>
        _leave.CanDepartAsync(departure, ct);
    // ...
}
```

- `WhenNavigatedTo(onNavigatedTo)` calls back each time the entry becomes its
  region's current entry, with a `NavigationArrival` (the entry id and the
  operation: `Push`, `Replace`, `Reset`, `Back` or `BackTo`) and a new disposable
  scope (`MultipleDisposable`; `ContainerDisposable` in the System.Reactive
  flavor, as in `WhenActivated`). The scope is disposed when the entry stops being
  current, whether a push retains it or it retires. Both run in the commit turn,
  at the same moment as `INavigationAware`. Subscribe while the entry is pending,
  in the constructor or `InitializeAsync`: a subscription made later starts with
  the next arrival and does not replay the current one. The subscription ends
  when the entry retires; dispose it only to stop the calls earlier, which also
  disposes the current scope.
- `WhenNavigatedFrom()` emits a `NavigationExit` (`Kind` `Retain` or `Retire`)
  in the commit turn of each departure, and completes when the entry retires.
- `CreateCompleteCommand(produce)` produces a result, then calls
  `entry.CompleteAsync(result)`; `CreateCompleteCommand<TResult>()` completes with
  the command's parameter, for a Pick command. The Back runs the entry's guards: a
  veto outputs `Rejected`, and the entry and its `PushForResult` request stay
  open, so the command can run again.
- `CreateDismissCommand()` calls `entry.DismissAsync()`: a dialog's Cancel. The
  request ends `Dismissed` at once, before the guards run.
- `CreateCloseCommand()` calls `entry.BackAsync()`: an editor's Close. The guards
  may veto it; the request stays open and ends `Dismissed` only when the Back
  commits.

The commands output the Back's `NavigationOutcome`, which a Runic View can
receive (no `RUNICBRIDGE003`). Like `CreateBackCommand`, they take an optional
`canExecute` and `outputScheduler` (pass it by name) and need no scheduler argument. A failure of
`produce`, or completing an entry that was not pushed with `PushForResult`,
reaches `ThrownExceptions`. The entry disposes them when it retires, so the
ViewModel has no dispose line for them.

### Command lifetime

ReactiveUI does not require disposing a command or a subscription that
observes only the ViewModel itself; dispose one only when it observes an object
that outlives its owner. For navigation:

- The entry commands (`CreateCompleteCommand`, `CreateDismissCommand`,
  `CreateCloseCommand`) and `WhenNavigatedTo` end when their entry retires.
- `CreateBackCommand()` and the region observables on the ViewModel's own
  region, such as a shell's `Main` or a page's child region, need no disposal.
  Dispose them with the owner when the region outlives it, such as a page's
  command on its parent's region.
- Subscriptions to window-scoped or singleton objects, such as a borrowed
  editor or a service, go in the `WhenNavigatedTo` scope, `WhenActivated` or the
  ViewModel's `Dispose`. The navigator disposes the content it owns when its
  entry retires.

### State in InitializeAsync

`InitializeAsync` runs outside model turns, before the entry commits. Content
that the navigator created for the entry (`NavigationTarget.Create`, or a new
instance handed over with `Own`) is not presented until that commit, and the
commit turn runs after the awaited hook. So the hook sets that content's own
state directly, also after `await`:

```csharp
public async ValueTask InitializeAsync(NavigationEntryContext entry, int noteId, CancellationToken ct)
{
    var note = await _store.LoadAsync(noteId, ct);
    Title = note.Title; // not presented yet: no InvokeAsync
}
```

Use `context.InvokeAsync` for state that something already presents or that
other work also changes: a `Borrow` target, a service, another page, or state
that the page's own background work writes. Guards and `ResumeAsync` run while
the content may be presented, so they also use `InvokeAsync`. Don't schedule
work on ReactiveUI's main-thread scheduler from a hook, for example through a
command's `Execute().Subscribe()`: outside a turn it falls back (Views event
1043).

## Leave confirmation through an interaction

`ReactiveLeaveConfirmation.WithInteraction(interaction, prompt, hasUnsavedChanges,
discard, askOnRetain)` returns a `LeaveConfirmation` whose question is
`interaction.Handle(prompt())`. A handler's `true` lets the entry depart; `false`
keeps it. Every `LeaveConfirmation` rule holds: a retaining push is allowed
without asking unless `askOnRetain`; nothing is asked without unsaved changes;
`prompt` and `hasUnsavedChanges` run inside a model turn; a yes stands while its
departure is unsettled; and `discard` runs at most once per yes, in the commit
turn. `LeaveConfirmation.InDialog` stays for apps that prefer a dialog region.

- **Superseded or cancelled.** The guard's token disposes the `Handle`
  subscription and the departure ends at once, without waiting for the handler.
  ReactiveUI lets a running .NET handler finish and drops its answer. A Runic web
  View's handler receives an aborted `signal`.
- **Fails closed.** With no handler that answers, ReactiveUI's
  `UnhandledInteractionException<TInput, TOutput>` fails the departure:
  `NavigationResult.Failed` with `NavigationPhase.Guarding` (outcome `Failed`),
  logged by the navigator as `NavigationGuardFailed` (event 1060, category
  `Runic.Navigation`) with that exception type as `ErrorType`. A handler that
  throws fails it the same way. The entry stays current.
- **Threads.** The question is asked outside model turns, on the thread that
  runs the guard: the dispatcher for a navigator on WPF's
  `DispatcherModelContext`, the thread pool otherwise.

Each presentation answers the same interaction natively. In WPF, register the
handler in the View's `WhenActivated`. The View needs ReactiveUI.WPF and a
`ViewModel`; with Runic.Navigation.Wpf's view locator, bind it from the
`DataContext` with `ViewModel="{Binding}"`, as
[NotesNavigation.ReactiveUI](https://github.com/Runic-Artifex/runic-sdk/blob/main/examples/wpf-navigation/NotesNavigation.ReactiveUI/Notes/NoteDetailView.xaml)
does:

```csharp
public partial class EditView : ReactiveUserControl<EditViewModel>
{
    public EditView()
    {
        InitializeComponent();
        // DisposeWith is in ReactiveUI.Primitives; MultipleDisposable in ReactiveUI.Primitives.Disposables.
        this.WhenActivated((MultipleDisposable d) => ViewModel!.ConfirmDiscard
            .RegisterHandler(context => context.SetOutput(MessageBox.Show(
                $"Discard the changes to {context.Input.ItemName}?", "Close",
                MessageBoxButton.YesNo) == MessageBoxResult.Yes))
            .DisposeWith(d));
    }
}
```

While no View of the entry is active, such as for a retained entry, nothing
answers and a departure that asks fails closed. When that can happen, register a
fallback handler in the ViewModel's constructor: ReactiveUI runs the latest
handler first, so an active View's handler still answers before it. The same
fallback answers navigation that .NET code starts outside any View, such as a
timer or a service, because no web View is asked for it.

A Runic web View answers for the departures its own window starts:

| Departure | Who answers |
| --- | --- |
| The page's own command, such as its Close | The page's web View on the invoking browser connection |
| Another View's command in the same window, such as the shell's Back | The page's web View on the invoking browser connection |
| A guarded window close (`GuardClose`, `RequestCloseAsync`) | The page's web View, when it is the window's only presentation with a mounted handler |
| Navigation that .NET code starts outside a command | .NET handlers only |
| A command of another window | .NET handlers only |

A .NET handler registered later than the web View's, such as in a WPF View's
`WhenActivated`, answers first; the web View registers when its bridge
attaches, after the ViewModel's constructor.

In a test, answer in place (awaiting a command needs
`using ReactiveUI.Primitives.Signals;` in this flavor):

```csharp
using var handler = editor.ConfirmDiscard.RegisterHandler(context => context.SetOutput(true));
var outcome = await editor.CloseCommand.Execute(); // NavigationOutcomeKind.Committed
```

A Runic web View answers it with the generated client; see
[Leave confirmation in a web View](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#leave-confirmation-in-a-web-view).

A question for a whole region rather than one page, such as unsaved work
anywhere in a window, is an `INavigationRegionGuard` registered with
`region.AddDepartureGuard(guard)` or `navigator.AddDepartureGuard(guard)` (see
[Guards for a whole region](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md#guards-for-a-whole-region)).
It can ask through an `Interaction` the same way: its
`NavigationRegionDeparture` has the `ModelContext` and `OnCommitted` of a
`NavigationDeparture`. It is asked after the pages' guards, or before them
with `Order = NavigationRegionGuardOrder.BeforeEntries` for a precondition
that should refuse before any page asks.

## Native command composition

The adapter supplies observable state for ReactiveUI pipelines; it does not
replace ReactiveUI's command factories. A ViewModel can compose its own
application conditions, `ReactiveCommand.CreateFromTask`, observable properties
and navigation outcomes:

```csharp
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives;
using Runic.Navigation;
using Runic.Navigation.ReactiveUI;

var canOpen = Main.WhenIsTransitioningChanged()
    .CombineLatest(this.WhenAnyValue(vm => vm.HasSelection),
        (busy, selected) => !busy && selected);

OpenCommand = ReactiveCommand.CreateFromTask(async token =>
{
    var result = await Main.PushAsync<DocumentViewModel>(cancellationToken: token);
    HandleNavigationOutcome(result); // resumes on the model context
}, canOpen); // observes this ViewModel and its own region: no disposal

_isNavigatingHelper = Main.WhenIsTransitioningChanged()
    .ToProperty(this, vm => vm.IsNavigating);
```

The `IsNavigating` getter reads `_isNavigatingHelper.Value` (or use ReactiveUI's
`[ObservableAsProperty]` generator convention).

The command resumes on the model context after `await` because it started
there: the Runic bridge starts commands in a model-context turn, and a WPF
binding starts them on the dispatcher of a `DispatcherModelContext`. A command
started elsewhere, such as directly from a test, commits its state with
`context.InvokeAsync`.

`HandleNavigationOutcome` is application policy: a guard veto, cancellation,
supersession or failure is a typed core result, and must not be treated as a
successful navigation merely because the command's task completed. Command
availability is advisory; the engine still validates every request. Forward
the token for cancellation during guards or preparation. For result requests,
forward it to `PushForResult` to also dismiss a committed prompt on cancellation.

Deliver model state on the model context (the default in a Runic Views app) and
native UI state on the presentation's dispatcher. Dispose a command or
subscription with the ViewModel only when it observes an object that outlives
the ViewModel (see [Command lifetime](#command-lifetime)); subscriptions
specific to a mounted View belong in `WhenActivated`.
This command/property shape works in WPF and through Runic's web bridge. The
example exposes a completion-only command and handles navigation results inside
the ViewModel, so the web boundary does not need to encode navigation's content
and ownership objects. To return an outcome to the View, return
`result.Outcome`, a `NavigationOutcome`.

Runic has no adapter over ReactiveUI's `RoutingState`: the navigator replaces it.

## Logging

The adapter's category is `Runic.Navigation.ReactiveUI`. Events 1044-1049 are
reserved for it; it logs none today. Event 1043 is the Views
`ReactiveSchedulerOutsideModelTurn`.

The navigator logs what the adapter's hooks cause, in the `Runic.Navigation`
category: an interaction leave confirmation that no handler answers is
`NavigationGuardFailed` (event 1060) with `ErrorType`
`ReactiveUI.Binding.UnhandledInteractionException`2` (in the System.Reactive
flavor, `ReactiveUI.Binding.Reactive.UnhandledInteractionException`2`); a
`WhenNavigatedTo` or `WhenNavigatedFrom` callback that throws is event 1075, like
any `StateChanged` handler.
