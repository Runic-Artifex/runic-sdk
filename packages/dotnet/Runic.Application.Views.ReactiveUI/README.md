# ReactiveUI integration for Runic Views

`Runic.Application.Views.ReactiveUI` is the default ReactiveUI 26 adapter for Runic
Views. It references ReactiveUI's `ReactiveUI.Primitives` flavor and has no
CommunityToolkit dependency. It provides:

- model-context scheduling: `AddRunicReactiveModelContext()` makes commands
  created without a scheduler deliver on the Window's model context (see
  [Model-context scheduling](#model-context-scheduling));
- `ReactiveRunicView<T>`, which implements `IViewFor<T>` over a typed Runic
  `DataContext` and activates an `IActivatableViewModel` while its web component
  is mounted. Override the `protected virtual` hooks `OnAttached`, `OnDetached`,
  `OnWebMounted` and `OnWebUnmounted` to add work; the activation runs either way,
  so an override does not call `base` (see
  [View lifetime hooks](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#view-lifetime-hooks)). Windows derive from `RunicWindow<T>` in
  `Runic.Application.Views` with ReactiveUI ViewModels too: the
  `ReactiveRunicWindow<T>` base was removed in 0.7.0-preview.7, so the same Window
  class opens on every host with either MVVM library;
- `ReactiveRunicViewLocator`, adapting explicit ReactiveUI view mappings and
  contracts;
- mount-owned `IActivatableViewModel` leases; and
- typed command and interaction adapters used by
  the compiled-model generator.

One browser presentation gets one logical Runic View and activation lease. A
shared ViewModel stays activated while any presentation remains mounted.
`WindowContentSession.AttachPresentation` creates those logical views for
generated content routes. The [Reactive Notes](https://github.com/Runic-Artifex/runic-sdk/blob/main/examples/notes-reactive-views/README.md)
example covers multiple views over one ViewModel, routed content, explicit view
contracts, and activation lifetimes.
The `runic-app` project template uses ReactiveUI ViewModels with this adapter by
default (`dotnet new runic-app`; `--view-models toolkit` selects CommunityToolkit.Mvvm).

## Primitives, not System.Reactive

ReactiveUI 26 has two flavours. This package targets the default one, built on
`ReactiveUI.Primitives`. If you know ReactiveUI from System.Reactive:

- `RxVoid` replaces `System.Reactive.Unit`: a command without input or output is
  a `ReactiveCommand<RxVoid, RxVoid>`.
- `ISequencer` replaces `IScheduler`. `RxSchedulers.MainThreadScheduler`,
  formerly `RxApp.MainThreadScheduler`, is an `ISequencer`.
- Awaiting a command needs `using ReactiveUI.Primitives.Signals;`, which
  provides the awaiter. Without it, `await command.Execute()` fails with CS1061.
- `command.Execute().ToTask()` from the `ReactiveUI.Primitives` namespace also
  works and returns the last value. Both throw `InvalidOperationException` when
  the command completes without a value, which a `CreateFromObservable` command
  can do; `Create` and `CreateFromTask` commands always produce one.

Both await styles resume on the model context when they start in a turn. For
System.Reactive, use [`Runic.Application.Views.ReactiveUI.Reactive`](#systemreactive-flavor).

## ReactiveUI 26

This package targets ReactiveUI **26.0.1**, Binding **9.1.0**, Primitives
**9.0.0**, and SourceGenerators **4.2.0**. ReactiveUI 26 and Binding 9 keep the
25/8.x public API; they are majors because their .NET 11 assemblies no longer
use runtime-async, so they also work on Mono. Runic's adapter API and generated
contracts are unchanged; rebuild the app against the 26 packages.

Primitives 9 changes `SubscribeSafe`: a single lambda is now the `onNext`
handler. Rename error-only calls, such as `source.SubscribeSafe(ex => Log(ex))`,
to `SubscribeSafeErrors`. Binding 9 also makes `BindCommandUnsafe` and
`BindInteractionUnsafe` dispose the binding they replace when the control chain
changes.

When moving from ReactiveUI 24, note that ReactiveUI 25 moved `IViewFor<T>` and
`IViewLocator` to `ReactiveUI.Binding`, so upgrade the Runic adapter, app, and
generated web client together. An adapter binary compiled against ReactiveUI
24 can fail at runtime against ReactiveUI 25 or later even if a dependency
override compiles.

ReactiveUI now includes shared source generators. Remove old explicit generator
pins or update them to 4.2.0 or later. The SDK centrally pins 4.2.0; the same
generator package supports the System.Reactive flavor, so there is no separate
`ReactiveUI.Reactive.SourceGenerators` package.

Create explicit mappings with the Binding locator:

```csharp
var locator = new DefaultViewLocator();
locator.CreateMappingBuilder()
    .Map<EditorViewModel>(() => new EditorView())
    .Map<EditorViewModel>(() => new CompactEditorView(), "compact");
```

For global lookup, replace `ReactiveUI.ViewLocator.Current` with
`ReactiveUI.Binding.ViewLocator.GetCurrent()`.

## Read-only and settable properties

As with any ReactiveUI View, the property's setter decides what the web
frontend may set. A public setter gets a `set<Property>` client method; give
status a private setter so it stays read-only to every View:

```csharp
[Reactive]
public partial string Title { get; set; } = "";       // client: setTitle

[Reactive]
public partial bool IsDirty { get; private set; }     // read-only: no setIsDirty
```

A hand-written property with `private set => this.RaiseAndSetIfChanged(ref _isDirty, value)`
works the same way. See
[read-only and settable state](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/dotnet/Runic.Application.Views#read-only-and-settable-state).

## Generated ReactiveUI contracts

Public `IReactiveCommand<TInput, TResult>` properties are inspected through
their interface contract, so `ReactiveCommand`, a base type, and a combined
command use the same generated input/result bridge. `RxVoid` is a no-result
operation and can complete with zero observable values; a non-void result must
produce exactly one observable value unless the property opts into `Last` or
`Stream` with `[RunicCommandResult(...)]`.
Generated TypeScript exposes operation start, request-ID recovery, status,
cancellation, retained results, and cursor reads for streams.

Use `[RunicCommandInput(typeof(TInput))]` on a plain `ICommand` property to
generate a typed argument. That command remains synchronous fire-and-snapshot
work and has no generated operation result or cancellation handle.

Public getter-only `Interaction<TInput, TOutput>` properties become typed
browser surfaces. The generated client uses this shape:

```ts
const unregister = view.interactions.confirmDiscard.handle(
  async (request, { signal }) => confirmInFrontend(request, signal),
);
```

The disposer, replacement handler, view disposal, unmount, disconnect, the
cancellation of the command that asked, and the end of a navigation guard that
asked (see [Leave confirmation in a web View](#leave-confirmation-in-a-web-view))
abort the handler signal as appropriate. Requests go only to a selected mounted
endpoint through a pull route, and never to another window or browser
connection. With no eligible browser handler, the adapter does not consume the
interaction, preserving normal ReactiveUI .NET handler precedence and unhandled
behavior.

The browser handler is a ReactiveUI handler registered when the View's bridge
attaches. ReactiveUI asks the latest handler first, so a .NET handler registered
after the View attached, such as a WPF View's in `WhenActivated`, answers before
it, and a fallback registered in the ViewModel's constructor answers only when
no browser handler is eligible.

## Model-context scheduling

Call `AddRunicReactiveModelContext()` (namespace
`Runic.Application.Views.ReactiveUI`) when you register the ViewModels, and
create commands without a scheduler:

```csharp
using Runic.Application.Views.ReactiveUI;

services.AddRunicReactiveModelContext();

public CounterViewModel() =>
    IncrementCommand = ReactiveCommand.Create(() => { Count += Step; });
```

The extension installs a model-context main-thread scheduler as
`RxSchedulers.MainThreadScheduler`, the scheduler a `ReactiveCommand` uses when
none is passed. Work scheduled inside a turn of a `RunicModelContext` runs on
that context, so a bridged command delivers its results and `IsExecuting` on
the model context of the Window that runs it, ordered with Bridge replies and
state publication. `CanExecute` follows its source synchronously, in the turn
that changes the state it observes. Routing follows the running turn, so two
Windows never share notifications. Work that a turn's async flow schedules after
it left the turn, for example after `ConfigureAwait(false)`, in `Task.Run`, or
from a timer or `Interval` created in the turn, also runs on that turn's context
(since 0.10). Work whose flow never ran in a turn, for example from a timer or
service started outside any turn, and work of a context that has closed, runs on
the scheduler the extension replaced, ReactiveUI's task pool. Runic logs that as
`ReactiveSchedulerOutsideModelTurn` (Views event 1043, Warning), once per
ViewModel type found on the call stack, named in its `Source` property, and once
for work with no ViewModel there. Runic Desktop, CS-WebUI and
`RunicWindowTestHost.Create` pass the application's `ILoggerFactory` to the
scheduler when a window opens, so the warning reaches the application's logs;
before that, or without a factory, it goes to `Trace`. To fix it, observe the
background source on the context's scheduler, as shown below for `ObserveOn`.

An application that builds ReactiveUI with `RxAppBuilder` adds
`WithRunicModelContext()` (namespace `ReactiveUI.Builder`) to that builder, as
Avalonia, MAUI and WPF applications add `WithAvalonia()`, `WithMaui()` or
`WithWpf()`, and still calls `AddRunicReactiveModelContext()`:

```csharp
using ReactiveUI.Builder;
using Runic.Application.Views.ReactiveUI;

services.AddRunicReactiveModelContext();

RxAppBuilder.CreateReactiveUIBuilder()
    .WithPlatformServices()
    .WithRunicModelContext()
    .BuildApp();
```

Without it, `BuildApp()` with `WithPlatformServices()` replaces the scheduler
with ReactiveUI's default outside a test runner. An application that does not
use `RxAppBuilder` needs only `AddRunicReactiveModelContext()`.

The extension replaces only ReactiveUI's default schedulers. A scheduler that an
application or UI platform set, such as WPF's dispatcher or a test scheduler, is
kept, and `InstallMainThreadScheduler()` returns `false`. To opt out, set
`RxSchedulers.MainThreadScheduler`, or call `WithMainThreadScheduler` on
ReactiveUI's builder, before `AddRunicReactiveModelContext()`, or replace it
afterwards. When a Runic Desktop, CS-WebUI or test-host window opens while
another scheduler is in effect, Runic logs it once per scheduler type: as
`ReactiveSchedulerKept` (Views event 1090, Information) when it was set before
`AddRunicReactiveModelContext()`, and as `ReactiveSchedulerReplaced` (1091,
Warning) when it replaced Runic's afterwards. A UI dispatcher's scheduler, such
as WPF's, is not logged. Commands read the scheduler when they are created, so
register the model context before the ViewModels create their commands.

The scheduler only delivers the command's notifications. The task body runs in
the context because the Bridge starts it in a turn. Set ViewModel state directly
in a `CreateFromTask` body, also after `await`, as with any MVVM library. Use
`InvokeAsync` only for code that left the context, for example after
`ConfigureAwait(false)` or in `Task.Run`: the scheduler routes such code's
ReactiveUI work back to the context, but a plain property write there still runs
outside the turn. An execution started in a turn carries the turn's context to
the command's notifications, so a `CreateFromTask(async cancellationToken =>
...)` command, which ReactiveUI completes outside the turn, and a body whose last
`await` uses `ConfigureAwait(false)` still deliver on the Window's context. That
holds for a bridged execution and, since 0.10, for a plain
`command.Execute().Subscribe(...)` or `await command.Execute()` in hand-written
code started in a turn, such as a wrapper command or a property-change handler.
A turn that blocks on these notifications deadlocks, as it does for a bridged
execution. The
[threading rule](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#threading-state-after-await)
has the details.

For an explicit scheduler, for example an `ObserveOn` in a DynamicData
pipeline, inject `ISequencer` or call `ReactiveSchedulers.For(context)`.
Both return the context's own sequencer, one per context, which the main-thread
scheduler also uses. A ViewModel that observes a source outside model turns, such as a
service's events or a timer, and does not take the context in its constructor,
can call `ReactiveSchedulers.For(this)` (since 0.11): each scheduled item goes to
the context the ViewModel is bound to at that time, through that context's
sequencer. While the ViewModel is not bound, for example in its constructor
before the window binds it or after the window released it, and when its
context has closed, the work goes to `RxSchedulers.MainThreadScheduler`, so
work from outside any turn is logged as event 1043 there.

```csharp
public ClockViewModel(IClock clock) =>
    clock.Ticks.ObserveOn(ReactiveSchedulers.For(this)).Subscribe(time => Now = time);
```

Use the injected sequencer instead for a source that replays when the
constructor subscribes. The injected sequencer is resolved from the window's scope
before the ViewModel is constructed, so a source that replays when the
constructor subscribes is already observed on the right context; a scheduler
that looked up the ViewModel's context could not, because the host binds the
ViewModel only after constructing it. The extension registers, with `TryAdd`, the scoped
`IRunicModelContext`, the singleton `IReactiveSchedulerProvider` and a
transient `ISequencer` for the resolved context, preserving custom application
registrations. Dispose a scope asynchronously to drain its default owned
context. Each queued item captures its own `ExecutionContext`, so a trusted
interaction scope follows its own deferred work without leaking to another
queued operation.

The ViewModels need no `RunicModelContextRegistry.Bind`. The CS-WebUI, Desktop
and WPF hosts give the window the scope's context and bind the root ViewModel
to it. The window binds each child ViewModel it presents, including one a
ViewModel creates later, for as long as it presents it, and a navigator binds
the region entries it owns. A ViewModel already bound to a different context
fails when presented, with an `InvalidOperationException` naming its type. When
you build a `WindowContentSession` or `RunicWindowTestHost` yourself, pass the
context the ViewModels run on as its `ModelContext`; otherwise the window
creates a context of its own and commands and replies are not ordered with each
other.

`ReactiveSchedulers` is the static entry point: `For(context)`, `For(viewModel)`
and `InstallMainThreadScheduler()`. `IReactiveSchedulerProvider` is the same lookup
as a service, for code that takes it by injection; the extension registers
`ReactiveSchedulerProvider`, which has no public constructor.

The [ReactiveUI reference guide](https://docs.runic-artifex.eu/guides/application/reference/reactiveui/)
defines the supported data shapes, operation semantics, interaction targeting,
and model-context ownership.

## Declared failures and ThrownExceptions

A `ReactiveCommand` reports every exception on `ThrownExceptions`, including a
declared `RunicFailureException` (`[RunicFailure]`) that the Bridge already sent
to the client as `domain-failed`. Without a subscriber, ReactiveUI routes it to
`RxState.DefaultExceptionHandler`, which breaks into the debugger and throws
`UnhandledErrorException`.

While a generated Bridge presents a ViewModel, it subscribes to the
`ThrownExceptions` of each bridged `ReactiveCommand`, so the ViewModel needs no
subscription of its own. Each exception is reported exactly once:

| Exception | Reported by |
| --- | --- |
| Declared failure (`RunicFailureException`) or cancellation | The reply to the client (`domain-failed`, `cancelled`); not logged as an error |
| Unexpected exception of a Bridge call | The Views runtime: the reply and event 1000 (awaited call) or 1004 (operation) |
| Unexpected exception of an execution that hand-written code ran with a plain `Execute` inside a Bridge call, when the code let it propagate or wrapped it (as `InnerException` or in an `AggregateException`) | The Views runtime, as the Bridge call's exception (since 0.10) |
| Unexpected exception of an execution no Bridge call received, such as `command.Execute().Subscribe()` in .NET, a fire-and-forget `ICommand.Execute` or `InvokeCommand`, or a failing `canExecute` | The Bridge: `ReactiveCommandFailed` (event 1042, Error), in category `Runic.Application.Views` through the Window's `ILoggerFactory`, with `Model.Command` as `Source` |

A failure raised in a Bridge call's async flow, also by a command that the
call's own command runs, is held until that call ends, and until the outermost
Bridge call in the flow ends: it is logged then unless one of the calls received
it or an exception that wraps it. A fire-and-forget failure during a Bridge call
is therefore logged when the call ends, not at once. A streamed operation's flow
holds nothing, so a failure during a long stream is logged when it happens.

Runic cannot tell an execution whose hand-written caller received and handled
the exception from a fire-and-forget one: ReactiveUI does not report which
observer received an error. So event 1042 is still logged when hand-written
code awaits a bridged command with a plain `Execute` and handles the exception
itself, and when it runs a bridged command that way outside any Bridge call, for
example in a model turn, and handles the exception. Run the command with
`RunAsync` there (see [Which call to use](#which-call-to-use)), or observe it
with `ObserveBridgeExceptions` to take over its logging.

Two Bridges that present one ViewModel log an exception once. The Bridge
unsubscribes when it detaches and every execution that a Bridge call or
`RunAsync` started has ended (since 0.11). An execution that was cancelled ends
when its work stops, which can be after the Bridge detached, for example when a
retiring navigation entry cancels its operation: ReactiveUI then reports the
cancellation on `ThrownExceptions`, which the Bridge still observes, so it never
reaches ReactiveUI's default handler. Another failure that such an execution
reports after the call stopped awaiting it reached no caller, so it is logged
as event 1042 with the logger of the Bridge that detached last (through `Trace`
when no Bridge presented the command). A cancelled `CreateFromObservable`
execution ends without telling Runic, so its command stays observed while it
exists. Otherwise a command that no Bridge presents, such as one on a retained
page or in a WPF View, keeps ReactiveUI's behaviour. Subscribe to its
`ThrownExceptions`, or set ReactiveUI's handler with the builder's
`WithExceptionHandler`, for those.

To handle a command's exceptions yourself, use `ObserveBridgeExceptions` or
your own `ThrownExceptions` subscription:

```csharp
DiscardCommand.ObserveBridgeExceptions(error => status.Report(error));
```

While `ObserveBridgeExceptions` observes a command, the Bridge does not log
that command's exceptions: the handler replaces the logging. It ignores
`RunicFailureException` and `OperationCanceledException`, also one that is not
the Bridge's, such as an `HttpClient` timeout when the command runs outside the
Bridge, and reports every other exception: the `ILogger` overload logs it as
event 1042 with the command's expression, such as `SaveCommand`, as `Source`
(pass `sourceName` to choose another), and the callback overload passes it on.
An unexpected exception of a Bridge call is still logged by the Views runtime
(event 1000 or 1004) as well. An exception thrown by the callback surfaces from
`ThrownExceptions.OnNext`, inside ReactiveUI. A raw `ThrownExceptions`
subscription does not stop the Bridge's logging. Dispose the returned
subscription only when the command outlives the handler's owner. The helper
works on any `IHandleObservableErrors`, such as a `ReactiveObject`.

## Which call to use

Hand-written code sometimes runs a command on behalf of a Bridge call or inside
a model turn, for example a command that a Bridge presents and that runs another
command as one of its steps:

```csharp
using Runic.Application.Views.ReactiveUI;

public EditorViewModel()
{
    SaveCommand = ReactiveCommand.CreateFromTask<Document>(SaveAsync);
    // Presented by the Bridge; it saves, then closes the editor.
    SaveAndCloseCommand = ReactiveCommand.CreateFromTask(async cancellationToken =>
    {
        await SaveCommand.RunAsync(Current, cancellationToken);
        Close();
    });
}
```

A wrapper that only looks up an object for a command whose input has no wire
form is not needed since 0.11: present that command itself with
`[RunicCommandKey]`, which resolves the page's key to the object inside the
generated call (see
[Commands whose parameter has no wire form](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#commands-whose-parameter-has-no-wire-form)).

`RunAsync(input, cancellationToken)` (namespace `Runic.Application.Views.ReactiveUI`,
since 0.11) runs a command the way a generated Bridge call runs a command
without a result:

- It completes when the execution completes, after zero or more values, so a
  command that produces no value does not fail as `ToTask()` does. The values
  are not returned.
- Cancelling the token disposes the execution, which cancels a token-taking
  `CreateFromTask` body, and the task is cancelled.
- A failure is rethrown unchanged and counted as received by the caller, so it
  is never logged as `ReactiveCommandFailed` (event 1042), inside or outside a
  Bridge call, whether the code lets it propagate, wraps it or handles it. A
  failure that arrives after the token cancelled the task reaches no caller and
  is logged as a fire-and-forget failure is. The command's `ThrownExceptions`
  stay observed until the execution ended, so its late cancellation does not
  reach ReactiveUI's default handler.
- The command's notifications follow the model turn that started it, as for any
  execution started in a turn.

| Situation | Call |
| --- | --- |
| Inside a Bridge call, the exception propagates or is wrapped, and the result is needed | `await command.Execute(input)`: the Bridge call receives the exception |
| The code handles the failure itself, or runs outside any Bridge call, for example in a model turn or a service | `await command.RunAsync(input, cancellationToken)` |
| Completion is needed and the command may produce no value | `await command.RunAsync(input, cancellationToken)` |
| Fire and forget, with failures logged | `command.Execute(input).Subscribe()` or `InvokeCommand`; event 1042 reports failures |
| A command that runs only from .NET and handles its own failures | `ObserveBridgeExceptions` on that command |
| A wrapper that only looks up the input of a command whose input has no wire form | None: `[RunicCommandKey]` on that command |

`RunAsync` is not the generated-code helper `ReactiveCommandExecution`
(namespace `….Generated`), which hand-written code should not use. Both have the
same semantics for a command without a result.

## Command disposal

ReactiveUI does not require disposing a command: `Dispose` only ends its
`canExecute` subscription. Follow ReactiveUI's rule for subscriptions and
dispose a command only when its `canExecute` observes an object that outlives
the command's owner, such as a service, another page or a parent's region.

- A command without `canExecute`, or whose `canExecute` observes the
  ViewModel's own properties (`this.WhenAnyValue(...)`), needs no disposal.
- The entry commands (`CreateCompleteCommand`, `CreateDismissCommand`,
  `CreateCloseCommand`) are disposed by their entry when it retires.
- `CreateBackCommand()` on a region of the same ViewModel, such as a shell's
  `Main`, needs no disposal. Dispose it when a page creates it on its parent's
  region.
- A handler registered on the ViewModel's own `Interaction` needs no disposal.
  Put View-scoped work in `WhenActivated`.

## Navigation

Start with the [ReactiveUI navigation walkthrough](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md#walkthrough):
pages, a leave guard, results and a guarded window close, with code from the
[Reactive Notes](https://github.com/Runic-Artifex/runic-sdk/blob/main/examples/notes-reactive-views/README.md)
example. This section covers what is specific to Runic Views.

The observables and back command for `RunicNavigator` regions
(`WhenCurrentChanged`, `WhenCurrentEntryChanged` and `CreateBackCommand`) are in the
`Runic.Navigation.ReactiveUI` namespace of the
[Runic.Navigation.ReactiveUI](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md)
package. It depends on `Runic.Navigation` and `ReactiveUI` only. This package
no longer references it, so an app without navigation does not carry it; add a
`PackageReference` to `Runic.Navigation.ReactiveUI` to navigate.
Use a `NavigationRegion<TContent>` for navigation. Expose the region as a
get-only property and the generator presents its `Current` like any content
slot. This package has no `RoutingState` adapter: code that projects a
ReactiveUI router exposes its own content property and raises
`PropertyChanged` when `CurrentViewModel` emits.

`CreateBackCommand()` needs no scheduler: with `AddRunicReactiveModelContext()`
its notifications arrive on the Window's model context. Its output is a
`NavigationOutcome`, so a View can bind it. The Bridge logs its unexpected
exceptions, and on the shell's own region it needs no disposal (see
[command disposal](#command-disposal)):

```csharp
using Runic.Navigation.ReactiveUI;

BackCommand = Main.CreateBackCommand();
```

ReactiveUI activation follows a mounted View, not a navigation entry's lifetime:
put per-presentation subscriptions in `WhenActivated`, and per-entry ones in the
adapter's `entry.WhenNavigatedTo(...)`.

The adapter's entry commands (`CreateCompleteCommand`, `CreateDismissCommand`,
`CreateCloseCommand`) also output a `NavigationOutcome`, so a View binds them
without `RUNICBRIDGE003`, and their entry disposes them when it retires; see
[entry lifecycle and result commands](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md#entry-lifecycle-and-result-commands).

### Leave confirmation in a web View

`ReactiveLeaveConfirmation.WithInteraction` from the navigation adapter asks
through a ViewModel `Interaction`, which the generator bridges like any other
interaction. The ViewModel takes its entry context in the constructor:

```csharp
public sealed record DiscardPrompt(string ItemName);

public sealed class EditViewModel : ReactiveObject, INavigationDepartureGuard, IDisposable
{
    private readonly LeaveConfirmation _leave;

    public EditViewModel(NavigationEntryContext entry)
    {
        _leave = ReactiveLeaveConfirmation.WithInteraction(
            ConfirmDiscard, () => new DiscardPrompt(Name), () => IsDirty, discard: () => Name = _savedName);
        SaveCommand = entry.CreateCompleteCommand(_ => Task.FromResult(_savedName = Name));
        CloseCommand = entry.CreateCloseCommand(); // asks when dirty
    }

    public Interaction<DiscardPrompt, bool> ConfirmDiscard { get; } = new();
    public ReactiveCommand<RxVoid, NavigationOutcome> SaveCommand { get; }
    public ReactiveCommand<RxVoid, NavigationOutcome> CloseCommand { get; }

    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken ct) =>
        _leave.CanDepartAsync(departure, ct);
    // Name, IsDirty, Dispose ...
}

public sealed partial class EditView : RunicView<EditViewModel>;
```

The web View registers the handler while it is mounted and runs the commands:

```ts
const unregister = view.interactions.confirmDiscard.handle(
  async (prompt, { signal }) => confirmDialog(`Discard the changes to ${prompt.itemName}?`, signal),
);

const close = await view.startClose();
const settled = await close.outcome();
if (settled.ok && settled.value.kind === "Rejected") showStillEditing(); // the user said no
```

The browser is asked when the page's own window starts the departure:

| Departure | Who answers |
| --- | --- |
| A bridged command of this View, such as `close()` or `startClose()` | This View, mounted by the browser connection that ran the command |
| A bridged command of another View in the same window, such as the shell's Back | This View, mounted by the browser connection that ran the command |
| A guarded window close: the close button with `GuardClose`, or `RequestCloseAsync()` | This View, when it is the only presentation in the window with a mounted handler |
| Navigation that .NET code starts outside a command, such as a timer or startup | .NET handlers only |
| A command of another window that navigates this window's region | .NET handlers only |

- An interaction never crosses windows or browser connections. A command's
  question goes only to the connection and client that ran the command. A
  close has no browser caller, so it asks only when exactly one presentation in
  the window, from any connection, has mounted a handler; with several, as in a
  multi-client CS-WebUI window, none is asked.
- A .NET handler keeps ReactiveUI's order: the latest registered is asked first.
  A fallback registered in the ViewModel's constructor answers only when no
  browser handler is eligible, for example when the View isn't mounted or the
  departure came from .NET code. The web View registers its handler when it
  mounts, so a departure during the page's first render, such as a close just
  as the page opened, is answered by that fallback too.
- When the departure is superseded (another navigation of the region wins),
  cancelled or its navigator closes, the request ends and `signal` aborts. So
  does the close decision when the window closes without asking. Close the
  dialog when it does; a late answer is ignored.
- A View that unmounts or disconnects while it asks fails the departure closed:
  the page stays, and a close keeps the window open.
- With no handler that answers, the departure fails closed: the command's
  outcome is `Failed`, a close keeps the window open, and the navigator logs
  event 1060 with the `UnhandledInteractionException` type.

## DynamicData

For DynamicData changesets, in either flavor, place `BatchBridgeSnapshots(model)`
(namespace `Runic.Application.Views`, for any `IObservable<T>`) after
`ObserveOn(ReactiveSchedulers.For(context))` and before `Bind` or
`SortAndBind`. The downstream delivery owns the batch even when scheduling is
deferred, so each changeset is one frame. Annotate a read-only DTO collection
with `[RunicCollection(nameof(Row.Id))]` to publish indexed updates; a
ViewModel with validation publishes them too. A `Bind` that runs outside the
window's model turn is logged once per field as event 1025
`BridgeCollectionChangedOutsideModelTurn`. Changes after the window's model
context closed are ignored and do not end the binding. See the
[DynamicData guide](https://docs.runic-artifex.eu/guides/application/guides/dynamicdata/).

## System.Reactive flavor

Applications using `ReactiveUI.Reactive`, `ReactiveUI.Binding.Reactive`,
`System.Reactive.Unit`, or `IScheduler` should instead reference
[`Runic.Application.Views.ReactiveUI.Reactive`](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI.Reactive/README.md).
The two packages expose distinct ReactiveUI namespaces and must not be mixed in
one application. The generated bridge calls the command helpers of the adapter
the project references, so a command typed as ReactiveUI's shared
`IReactiveCommand<TInput, TOutput>` interface works with either flavor; there is
no flavor property to set. The build fails with `RUNICBRIDGE023` when adapters of
both flavors arrive, directly or through another package or project, including
`Runic.Navigation.ReactiveUI.Reactive` with this package.

See the upstream [Binding migration guide](https://www.reactiveui.net/documentation/reactiveui/upgrading/reactiveui-binding-migration/)
for ReactiveUI's application-level migration details.
