# Runic.Application.Testing

Test a generated Runic Window contract and its routed Views without a browser or
native window. `RunicWindowTestHost<TViewModel>` owns an in-memory transport, a real
`WindowContentSession`, and the generated root Bridge attachment, so tests run the
real ViewModels and generated .NET handlers. The caller owns the ViewModel and its
dependency injection scope.

## Testing

Build the application's own services, create a scope for the window, and create the
host from it, with a test clock in place of the system clock:

```csharp
var clock = new FakeTimeProvider();   // Microsoft.Extensions.Time.Testing
await using var services = new ServiceCollection()
    .AddSingleton<TimeProvider>(clock)
    .AddNotes()          // the application's registrations, including AddRunicViews()
    .BuildServiceProvider();
await using var scope = services.CreateAsyncScope();
using var host = RunicWindowTestHost.Create<ShellViewModel>(scope.ServiceProvider,
    new RunicWindowTestHostOptions { TimeProvider = clock });
```

`RunicWindowTestHost.Create` resolves what the Desktop and CS-WebUI windows resolve
from the window's scope: the root ViewModel, its generated Bridge attachment
(`AddRunicViews()` registers it), and, when registered, the `IRunicViewLocator`, the
`IRunicModelContext` (for example from `AddRunicReactiveModelContext()` or
`AddRunicNavigation()`) and the `ILoggerFactory`. A locator, context or logger factory
set in the options is used instead. It names the missing registration when the
ViewModel or its Bridge is not registered. The scope and its ViewModels stay yours;
dispose the host before the scope. `host.Services` is the provider it resolved them
from.

The services need two registrations: the ViewModel, as the application registers it
(for example `services.AddScoped<ShellViewModel>()`), and the generated Bridge, which the
application's generated `AddRunicViews()` adds. Register both with the application's
own registration method when it has one, so the test runs the same composition. A
ViewModel the test builds by hand needs no registration: pass it to `Create`, which
still takes the Bridge, locator, model context and logger factory from the services
(since 0.14):

```csharp
await using var services = new ServiceCollection()
    .AddRunicViews()     // the application's generated Bridge registrations
    .BuildServiceProvider();
var shell = new ShellViewModel(new InMemoryNotes());
using var host = RunicWindowTestHost.Create(services, shell);
```

A Window opened on a real host has the same scope: `window.Services` (since 0.14) is the
provider of the Window's dependency injection scope, which resolved its ViewModel, so a
test can read the scoped services the ViewModel received.

The constructors take the ViewModel and the attachment directly, for a window that is
not composed with dependency injection:

```csharp
using Runic.Application.Views.Generated.Bridges;   // the generated ShellBridge

using var host = new RunicWindowTestHost<ShellViewModel>(shell,
    (transport, content, vm) => new ShellBridge(transport, vm, content: content),
    new RunicWindowTestHostOptions { ViewLocator = locator, TimeProvider = clock });
```

The generated bridges are internal, so this needs `InternalsVisibleTo` from the
application to the test project. A test project that sees two applications' internals
can find the same bridge name in both: name one by an `extern alias` of its reference,
or create the host with `RunicWindowTestHost.Create` instead.

Then drive the Window by ViewModel member, as the generated TypeScript client does:

```csharp
var sidebar = await host.Root.ViewAsync<SidebarViewModel>(vm => vm.Sidebar);
(await sidebar.ExecuteAsync(vm => vm.OpenNotesCommand)).ThrowIfFailed();
var document = await host.Root.ViewAsync<DocumentViewModel>(vm => vm.Main);
var editor = await document.ViewAsync<EditorViewModel>(vm => vm.CurrentPane);
(await editor.SetAsync(vm => vm.Title, "Groceries")).ThrowIfFailed();
(await editor.ExecuteAsync(vm => vm.TagCommand, new TagInput("shopping", Pinned: true))).ThrowIfFailed();

var save = await editor.StartAsync(vm => vm.SaveCommand);
Assert.True((await editor.SnapshotAsync()).IsExecuting(vm => vm.SaveCommand));
clock.Advance(TimeSpan.FromMilliseconds(250));   // storage waits on the injected clock
Assert.Equal(RunicOperationStatusKind.Succeeded, (await save.WaitAsync()).Kind);
Assert.Equal("Saved Groceries", (await editor.SnapshotAsync()).Read(vm => vm.SavedMessage));
```

### Asynchronous and synchronous drivers

Every request has an asynchronous method that never blocks a thread (since 0.14):
`SnapshotAsync`, `SetAsync`, `SetJsonAsync`, `WriteAsync`, `ExecuteAsync`,
`CanExecuteAsync`, `StartAsync`, `MountAsync`, `UnmountAsync`, `ViewAsync`,
`ViewsAsync` and `TrackAsync` on a driver, `GetStatusAsync`, `CancelAsync` and
`ReadStreamAsync` on an operation, `VerifyAsync` on a tracker and `RequestAsync` on
the transport. Concurrent requests on one connection share its `hello`.

The synchronous methods (`Snapshot`, `Set`, `Write`, `Start`, `Mount`, `Request`, ...)
remain for short tests. They block the calling thread until the reply, while the
window's own asynchronous work, such as a model turn or an awaited command, continues
on thread-pool threads. A suite that runs many tests in parallel on the thread pool,
as TUnit and xUnit do, can starve the pool this way: each blocked test holds a thread
that the replies it waits for need, and the pool adds threads slowly. Use the
asynchronous methods in such suites.

`dotnet new runic-app --tests` creates an application with a TUnit test project
that does this for its Window; the
[CommunityToolkit Notes example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-view-first/Tests)
is a larger xUnit project.

### Skip the frontend build

A test project that references the application project builds the application, and
by default that build installs and builds the frontend too. The host needs only the
.NET assembly, so pass `RunicApplicationFrontendBuildEnabled=false` to the reference, as the
template's test project does:

```xml
<ProjectReference Include="../MyApp/MyApp.csproj" AdditionalProperties="RunicApplicationFrontendBuildEnabled=false" />
```

The generated TypeScript clients are still written. In a solution that also builds
the application itself, MSBuild then builds the application project twice, once
with each setting; build the test project on its own, or leave the property out and
let the frontend build's up-to-date check skip unchanged frontends.

### Deterministic time and ids

`RunicWindowTestHost.Create` and both constructors give the window a
[`FakeTimeProvider`](https://learn.microsoft.com/dotnet/api/microsoft.extensions.time.testing.faketimeprovider)
from `Microsoft.Extensions.TimeProvider.Testing`, which this package depends on, unless
options supply another `TimeProvider`. Its time passes only through `Advance` or
`SetUtcNow`, which fire due timers, `CancellationTokenSource` timeouts and
`Task.Delay(TimeSpan, TimeProvider)` in order; a timer due now fires at once. The
window uses it for interaction deadlines and `DrainAsync` timeouts; register
the same instance in the application's services for its own time. `host.Time`
returns it.

Work that waits on the clock does not finish by itself:

- `ExecuteAsync` of an awaited command that waits on the clock completes only after
  the test advances it. Start the call, advance the clock, then await the call:

  ```csharp
  var save = editor.ExecuteAsync(vm => vm.SaveCommand);
  clock.Advance(TimeSpan.FromMilliseconds(250));
  (await save).ThrowIfFailed();
  ```

  `Start` returns at admission, so an operation can be inspected while it runs.
- Only code that takes the `TimeProvider` follows it. `Task.Delay(TimeSpan)`,
  `DateTime.UtcNow` and ReactiveUI's schedulers (`Throttle`, `Timer`,
  `RxApp.MainThreadScheduler`) keep using real time. Give such code the clock, for
  example through a scheduler built on it, or test it with ReactiveUI's
  `TestScheduler`.

Content ids are `1`, `2`, ... in presentation order, so routes are `content1`,
`content2`, ... and snapshots are stable. Set `CreateId` for another sequence. The
drivers number presentation ids `test-presentation1`, ..., operation ids
`request1`, ... and checked-write request ids `write1`, ... from one counter.

For real time, set `TimeProvider = TimeProvider.System` in the options.

### File watchers

`RunicPathWatchTester` drives the `RunicPathWatcher`s started with its
`Options` (see [Watching files](../Runic.Application.Views/README.md#watching-files)):
their debounce and polling run on its `FakeTimeProvider`, `Time`. Give the
options to the ViewModel that starts the watcher:

```csharp
using var watching = new RunicPathWatchTester();
using var overview = new OverviewViewModel(context, watching.Options);

File.WriteAllText(Path.Combine(checkout, "flake.lock"), lockText);
var change = await watching.NextChangeAsync();     // after the callback ran in the model context

// A path the test cannot change: report it as if the platform had.
await watching.TriggerAsync("/run/current-system");
```

- `NextChangeAsync` returns the next change not returned yet, once its callback
  has run. While none is reported, it advances `Time` one debounce interval at a
  time, so events that arrive from a real file system are delivered. It throws
  `TimeoutException` after 10 seconds of real time. Each of the three waits also
  takes a `CancellationToken`.
- `TriggerAsync(path)` marks the targets with that path changed in every active
  watcher, whether or not they changed, and returns `NextChangeAsync`. It throws
  `InvalidOperationException` when no active watcher has such a target.
- `DrainAsync` advances `Time` for 300 ms of real time and returns what was
  reported meanwhile, to assert that a burst produced one change or nothing more.
- `ActiveWatchers` counts the watchers that are started and not disposed, to
  check that a ViewModel stops its watcher.
- `Dispose` stops the watchers that are still active and detaches the tester
  from its options, so a watcher a test did not dispose does not outlive it.
  Each change's `Entries` names the paths that changed (see
  [Watching files](../Runic.Application.Views/README.md#watching-files)).

### Drivers

`host.Root` drives the root ViewModel. `View<T>(vm => vm.Content)` and
`Views<T>(vm => vm.Items)` follow the content references in the current snapshot
and check that they present `T`; `host.View<T>(reference)` drives a reference you
already have. A `RunicViewDriver<T>` has:

- `Snapshot()`: a `RunicViewState<T>` with `Read(vm => vm.Title)` for scalars
  (a string, Boolean, Int32, Int64, double, decimal, GUID or enum), `Get` for the
  raw JSON of other values, `Reference`, `References`, `Keys` of a
  `[RunicCollection]`, `CanExecute`, `IsExecuting` and `FieldVersion`. `Read` of a
  record, DTO or collection throws `NotSupportedException` naming the property;
  use `Get(vm => vm.Item).GetProperty("wireField")` instead.
- `Set(vm => vm.Title, value)`, encoded as the generated client encodes it: an `int`,
  `bool` or non-nullable `string` as is, and other scalars as wire JSON (`Int64` and
  `decimal` as strings, enums by wire name, `null` for nullable values). Since 0.14 a
  DTO, list or union is encoded as for a typed command input (below); `SetJson` takes
  wire JSON.
- `ExecuteAsync(vm => vm.SaveCommand, argumentJson)`, which waits for an
  asynchronous command, and `CanExecute(command, argumentJson)` for commands with
  an argument. `ExecuteAsync(command, argument)`, `CanExecuteAsync(command, argument)`
  and `StartAsync(command, argument)` take the input as a .NET value instead (since
  0.14) and encode it as the generated client does: scalars as `Set` encodes them, a
  record or class as an object of its properties by wire name (camelCase, or its
  `[RunicAlias]` or `[JsonPropertyName]`, without `[RunicIgnore]` and `[JsonIgnore]`
  members), a `[RunicUnion]` case with its `$case`, lists as arrays, string-keyed
  dictionaries as objects and a `[RunicCodec]` type by its codec. A `string` input binds
  to the wire JSON overload unless you name the type:
  `ExecuteAsync<string>(vm => vm.SearchCommand, "milk")`; a string that is not JSON
  throws `ArgumentException` that says so. A command whose `[RunicCommandKey]` resolver is asynchronous
  answers only after the resolver, so use `CanExecuteAsync` and `StartAsync` for
  it (since 0.11); `CanExecute` and `Start` throw `InvalidOperationException` for
  such a command instead of blocking the test thread, which may own the fake clock
  or the model turn the resolver waits for. A reply has `Succeeded`, `ErrorKind`, `ErrorReason` (since 0.11:
  `unavailable`, `invalid-input` or `unresolved-key` for a `rejected` call),
  `ErrorMessage`, `Revision`, `Released` and `Superseded`. A Views protocol 4
  reply names the `revision` that includes the call's effects instead of
  carrying state, so the driver reads `State` with a `snapshot` request after
  the reply, when the reply has a revision. `Released` is `true` when the call
  released its own route, as a command that navigates away does, and
  `Superseded` when a `set` was not applied because a later edit of the member
  was admitted first. `ThrowIfFailed()` throws for a failed call, as `NavigationResult` does. A
  command's declared failure (`[RunicFailure]`) replies with `ErrorKind`
  `domain-failed` and the encoded failure as `Failure`, such as
  `{"$case":"titleRequired"}`. A call of a released View or a closed window
  replies `released`.
- `Start(command, argumentJson, requestId)` for a recoverable operation. Its
  `Admission` is a `RunicOperationAdmissionKind` (`Accepted`, `Duplicate`, ...).
  It has `GetStatus()`, `WaitAsync()` and `Cancel()`, which returns a
  `RunicOperationCancelKind` (`CancellationRequested`, `NotRunning`, ...). A status
  has a `RunicOperationStatusKind` `Kind` (`Running`, `Succeeded`, `Failed`,
  `DomainFailed`, `Cancelled`, `Expired`, or `Unknown` for a request the window
  does not know), `Result`, `ErrorMessage` and, for a `DomainFailed` operation,
  `Failure`. `ReadStream(cursor)` reads a streamed operation's retained items
  after `cursor` as the generated client's `stream(cursor)` does: a
  `RunicOperationStreamPage` with the status `Kind`, the encoded `Items`, the
  `Cursor` of the next page, `Completed`, and `DeliveryKind` and
  `DeliveryMessage` when items were dropped (`stream-overflow`, ...). The
  delivery kinds, like `BridgeOperationDeliveryFailureKind` in .NET and
  TypeScript, are open: a minor release may add one.
- `Write(vm => vm.Title, value, expectedVersion)` for a checked write. It uses the
  current value and version as the baseline unless `expectedVersion` is given, so a
  stale version tests the `Conflict` receipt. A receipt has a
  `RunicFieldWriteReceiptKind` `Kind`, `ErrorMessage`, `Value`, `Version` and
  `State`, read like a reply's.
- `Mount(presentation)` sends `open` for a presentation of the route, as a page
  that shows the View does, and returns the presentation id
  (`test-presentation{n}` when omitted); `Unmount(presentation)` sends `close`.
  Both take optional client and connection keys and throw
  `InvalidOperationException` naming the route's reply unless it accepted the
  request, for example when another client's keys close it (`ignored`) or the
  route was released.
- `TakePublications()` and `NextPublicationAsync()` return this route's pushes
  in order, each a `RunicViewPublication` whose `Kind` is `State` (with `State`
  and `Fields`), `Frame` (with `BaseRevision`, the member `Sets`
  (`RunicMemberSet`), the keyed collection `Changes` (`RunicCollectionChange`)
  and the text `Texts` (`RunicTextChange`)), or `Failure`. `Revision` names the
  revision of each.

### Navigation

A `NavigationRegion<TContent>` slot is an ordinary content slot, so the same host
and drivers apply. Create the navigator with the window's model context and pass
that context as `RunicWindowTestHostOptions.ModelContext`; a generated Bridge
rejects a session whose context is not the navigator's. A `NavigationSelection`
slot is a nullable number: read the selected index with
`Snapshot().Get(vm => vm.Tabs)`, which is JSON `null` for an empty selection.

```csharp
using Runic.Navigation; // RunicNavigator and RunicModelContext
await using var context = new RunicModelContext();
await using var navigator = new RunicNavigator(new() { ModelContext = context, TimeProvider = clock });
var shell = new ShellViewModel(navigator);
using var host = new RunicWindowTestHost<ShellViewModel>(shell, attach,
    new RunicWindowTestHostOptions { ModelContext = context, TimeProvider = clock });

// A scripted guard: the test page's CanDepartAsync returns decision.Task, which
// holds the transition in Guarding until the test decides.
var decision = new TaskCompletionSource<bool>();
editor.Departure = decision.Task;
var back = shell.Main.BackAsync();
decision.SetResult(true);
await back;
await navigator.WhenIdleAsync();
var home = host.Root.View<HomeViewModel>(vm => vm.Main); // the resumed entry
```

- `RunicNavigator.WhenIdleAsync()` waits until no transition or retirement is
  running.
- `navigator.UnretiredEntryCount` counts entries that have not finished
  retiring (zero after `DisposeAsync`), and
  `host.Content.RetainedContentModelCount()`, an extension on the window's
  `WindowContentSession`, counts the content models whose model-context leases
  the window holds.
- Pass a `FakeTimeProvider` as `RunicNavigatorOptions.TimeProvider` to drive the
  close timeout and the overrun warning (event 1067) without waiting.

With `AddRunicNavigation()`, create the window scope with `CreateAsyncScope()`,
because the navigator is only `IAsyncDisposable`. `RunicWindowTestHost.Create` gives
the window the scope's `IRunicModelContext`, which the navigator shares:

```csharp
await using var scope = services.CreateAsyncScope();
using var host = RunicWindowTestHost.Create<ShellViewModel>(scope.ServiceProvider,
    new RunicWindowTestHostOptions { TimeProvider = clock });
```

A unit test of one page that takes its `NavigationEntryContext`, such as a
dialog that completes its request, needs no window. `RunicTestNavigationEntry`
(since 0.11) pushes the page on a test navigator with its own
`RunicModelContext`, as a region does in the application, and exposes the
context, the request's completion and the transitions that change the entry's
state:

```csharp
await using var dialog = await RunicTestNavigationEntry.CreateAsync(
    entry => new ConfirmNavigationViewModel(entry, "Discard the unsaved edits?"));

await dialog.Content.ConfirmCommand.ExecuteAsync(null);

await Assert.That(await dialog.Completion).IsEqualTo(new NavigationCompletion<object?>.Completed(true));
await Assert.That(dialog.Context.State).IsEqualTo(NavigationEntryState.Retired);
```

- The navigator constructs the content through `create`, so its navigation
  hooks, guards and `INavigationAware` calls run as in the application, and
  it owns and disposes the content when the entry retires. Pass `services`
  for the entry's `Services`.
- The entry is pushed for an `object` result: `Completion` reports
  `CompleteAsync` with any value, and `Dismissed` when the entry leaves
  otherwise.
- `RetainAsync` pushes another entry over it, `ActivateAsync` goes back to
  it, and `RetireAsync` clears the region. Each returns the
  `NavigationResult`, so a guard's veto is visible. `Navigator` and `Region`
  are there for anything else; disposing the object closes the navigator.

Write navigation tests so they are deterministic:

- Hold a transition in a phase with a `TaskCompletionSource` that a guard,
  `InitializeAsync` or `ResumeAsync` awaits. Signal a second one from the hook
  when the test must know that the hook started. Then the test decides when
  the hook continues, instead of racing it.
- Don't await a request whose guard waits for the test. Start it, wait for the
  observable effect, answer, then await it. For example, start the Back, wait
  until the dialog region's `Current` is set, execute the dialog's command
  through its driver, and then await the Back. A command driver's `Start`
  returns at admission, which suits a command that navigates.
- Wait for region changes with `PropertyChanged`, not with delays. Every
  change is raised inside a model turn.
- `await navigator.WhenIdleAsync()` before you assert on retirement:
  departing entries retire after the commit turn, and owned content is
  disposed then.
- Assert lifetimes through the counters. `navigator.UnretiredEntryCount` falls as
  entries retire, and is zero after `DisposeAsync`. `RetainedContentModelCount()`
  falls when owned content's leases are released. Assert `Retired` states and
  disposal counts of your own test content too.
- Make overruns and timeouts happen by advancing the `FakeTimeProvider`:
  5 seconds for event 1067, and `CloseTimeout` for disposal.
- To provoke a race, drive overlapping requests from a seeded `Random`, and
  assert invariants that don't depend on timing. For example, every request
  ends, an entry retires at most once, and nothing is left unretired after
  `DisposeAsync`.

The [Notes tests](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-view-first/Tests)
cover a guard that asks in a dialog region with `PushForResult`, resuming a
retained entry, retirement of a parent's child regions, racing navigation and
closing the window while the dialog asks; [ConfirmNavigationTests](https://github.com/Runic-Artifex/runic-sdk/blob/main/examples/notes-view-first/Tests/ConfirmNavigationTests.cs)
tests the dialog page on its own with `RunicTestNavigationEntry`.

### Interactions

A test answers a ReactiveUI `Interaction`, such as the question of a
`ReactiveLeaveConfirmation.WithInteraction` guard, with a .NET handler on the
ViewModel. ReactiveUI asks the latest handler first, so a handler the test
registers answers before a fallback registered in the ViewModel's constructor;
dispose it to restore the fallback. From the
[ReactiveUI WPF navigation tests](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/wpf-navigation/Tests.ReactiveUI):

```csharp
var answers = new Queue<bool>([false, true]);
var prompts = new List<string>();
using var handler = detail.ConfirmDiscard.RegisterHandler(context =>
{
    prompts.Add(context.Input);
    context.SetOutput(answers.Dequeue());
});
```

This is the supported test path for a question that a web View answers in the
browser. The host does not run the generated TypeScript client, and a
driver's `Mount()` opens a presentation without registering any
interaction handler, so the question never waits for a browser: it goes to
the .NET handlers. The test still covers everything the ViewModel decides: the
guard, the prompt's input, and what each answer keeps or discards. With no
handler that answers, the departure fails closed with ReactiveUI's
`UnhandledInteractionException`, as in the application.

The browser's side is tested with the frontend: the generated typed mock's
`interactions.<name>.request(input)` resolves with the answer of the handler
the View registered with `interactions.<name>.handle(...)`; see the
[Testing guide](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#testing).
Which View is asked for which departure, and the fallback when none is
mounted, are covered by the SDK's own tests and described in
[Leave confirmation in a web View](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#leave-confirmation-in-a-web-view).

CommunityToolkit.Mvvm has no interaction type: a Toolkit guard asks in a
dialog region, and a test answers it by executing the dialog's command through
its driver, as in [Navigation](#navigation).

### Collections and frames

`Track()` follows a route from a snapshot through its pushes, as the generated
client does: newer full states replace the state, frames must continue the tracked
revision (`BaseRevision`), and their keyed collection changes must name the row
keys they change; failure notices are collected. A frame the client could not
apply throws, naming the problem: a lost or reordered frame, more than 4,096
changes, a `set` beside another change of the same member, a member the View
does not have, a missing or unexpected checked-field version, a text change
whose length does not match, an index outside the rows, a row key that does not
match, an item count that does not match the keys, or an empty or duplicate key.
`FullStates` counts the full states and `Deltas` the frames it applied.

```csharp
var tracker = host.Root.View<HomeViewModel>(vm => vm.Main).Track();
library.Record("Groceries", "Milk, eggs");
var state = await tracker.WaitUntilAsync(state => state.Keys(vm => vm.RecentNotes)[0] == "Groceries");
Assert.Equal(0, tracker.FullStates);                       // only keyed changes were sent
Assert.Contains(tracker.Changes, change => change.Kind == RunicCollectionChangeKind.Move);
tracker.Verify();                                          // the frames match a fresh snapshot
```

Publications are delivered off the calling thread, so waits use real time,
`RunicWindowTestHostOptions.PublicationTimeout` (five seconds by default). A route's
frames go to its tracker or to `TakePublications`, not both.

### Lower level

`host.Transport` is the `InMemoryBridgeTransport`, the window's
`IBridgeConnectionTransport`.
`Request(json)` and `RequestAsync(json)` send a Views protocol 4 request on a connection
(`hello` first), `CloseConnection` closes it, and `Messages`/`TakeMessages()` list the
pushes the window sent, such as
`host.Transport.Request($$"""{"kind":"snapshot","route":"{{host.RootRoute}}"}""")` for
the raw reply that `host.Root.Snapshot()` parses. Each `RunicBridgeMessage` has the
push's `Json`, the `ConnectionKey` of a push to one connection, or `null` for a
broadcast, and, read from the JSON, its `Kind` (`state`, `frame`, `failure`,
`operation`, ...) and `Route` (since 0.14), so
`host.Transport.TakeMessages().Where(message => message.Route == editor.Route)` selects one
route's pushes. `DefaultConnectionKey` (`connection-1`) and `DefaultClientKey`
(`client-1`) are the keys of requests that name none. The verb is Take, as on the drivers,
because the pushes are removed; Drain names `DrainAsync`, which waits for accepted
work. A driver's `Mount` and `Unmount` (`open` and `close`) with client and
connection keys and `Transport.CloseConnection` test View lifetimes and client
disconnects. Await `host.DrainAsync(timeout)` before disposing
a host that has accepted background operations. It stops admission, as a closing
window does. With a fake clock its timeout passes only when the clock advances, and
its token stops only the wait.

The host does not instantiate the application's native Window wrapper or run the
generated TypeScript client or a rendering framework. Test the frontend with the
generated typed mocks of `@runic-artifex/views/mock`; see its
[Testing guide](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#testing).
To drive the whole application in a browser, use `RunicServeOnlyApplication`
(below); to run script in the embedded Runic Desktop View from an in-process
test, use `DesktopBridgeWindow<TViewModel>.Surface.ExecuteJavaScriptAsync`. See the
[Runic.Application.Views.Desktop automation](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.Desktop/README.md#automation)
section.

### Custom window hosts

Runic Desktop, CS-WebUI, WPF and this package carry the Views protocol over an
`IBridgeTransport` from `Runic.Application.Views`. A host of your own, such as
one that serves pages over its own WebSocket, implements the same interface;
the protocol it carries is in the
[Views protocol](../../../specs/application/README.md#transport). The window
calls `Serve` once with its `IBridgeRequestHandler` and sends every push
through `BroadcastAsync`. A transport that can push to one connection outside a
request also implements `IBridgeConnectionTransport.SendAsync`; without it, the
window keeps each connection's targeted pushes in a mailbox (1,024 pushes and
4 MiB at most), and the page's host script drains it with `receive` requests,
as CS-WebUI's does. The transport:

- passes each request to `HandleAsync` as a `BridgeRequest` with the message
  and the client and connection keys it authenticated, never keys read from the
  message, in the order the connection received them, without waiting for
  earlier replies: a `receive` or an awaited command stays open for long;
- sends the replies and pushes of one connection in the order the window sent
  them, and completes `BroadcastAsync` and `SendAsync` once the push is queued
  on every connection ahead of later sends;
- copies a push that it keeps after `BroadcastAsync` or `SendAsync` completed:
  the message is valid only until then, because the window returns its pooled
  memory and reuses it for later pushes;
- answers a request while no handler is served with the `released` reply, and
  reports each closed connection once with `ConnectionClosed`. A reconnect is a
  new connection with a new connection key.

```csharp
using System.Collections.Concurrent;
using Runic.Application.Views;

// PageSocket is the host's own connection: it writes replies and pushes in call order, and its
// Closing token is cancelled when the connection closes.
public sealed class SocketBridgeTransport : IBridgeConnectionTransport
{
    private static readonly byte[] Released =
        """{"ok":false,"error":{"kind":"released","message":"This View was released."}}"""u8.ToArray();
    private readonly ConcurrentDictionary<string, PageSocket> _pages = new();
    private IBridgeRequestHandler? _handler;

    public IDisposable Serve(IBridgeRequestHandler handler)
    {
        if (Interlocked.CompareExchange(ref _handler, handler, null) is not null)
            throw new InvalidOperationException("The transport already serves a handler.");
        return new Lease(() => Interlocked.CompareExchange(ref _handler, null, handler));
    }

    public async ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        foreach (var page in _pages.Values) await page.PushAsync(message, cancellationToken);
    }

    public async ValueTask<bool> SendAsync(string connectionKey, ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default)
    {
        if (!_pages.TryGetValue(connectionKey, out var page)) return false;
        await page.PushAsync(message, cancellationToken);
        return true;
    }

    public void Opened(PageSocket page) => _pages[page.ConnectionKey] = page;

    // The socket's read loop calls this for each request, in arrival order, with a buffer of its own.
    public void Received(PageSocket page, long requestId, ReadOnlyMemory<byte> message)
    {
        if (Volatile.Read(ref _handler) is not { } handler) { _ = page.ReplyAsync(requestId, Released); return; }
        _ = ReplyAsync(page, requestId, handler.HandleAsync(
            new BridgeRequest(message, page.ClientKey, page.ConnectionKey), page.Closing));
    }

    public void Closed(PageSocket page)
    {
        if (_pages.TryRemove(page.ConnectionKey, out _)) Volatile.Read(ref _handler)?.ConnectionClosed(page.ConnectionKey);
    }

    private static async Task ReplyAsync(PageSocket page, long requestId, ValueTask<ReadOnlyMemory<byte>> reply) =>
        await page.ReplyAsync(requestId, await reply);

    private sealed class Lease(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
```

The page's script installs `__runicBridge` with `request`, `onMessage` and
`onConnectionChange`, as the
[host bridge](../../../specs/application/README.md#host-bridge-page) describes, and
the window attaches its generated Bridge to the transport with
`new WindowContentSession(transport, options)` and the factory that
`AddRunicViews()` registers, as the constructors above take it.

Test the window side over `InMemoryBridgeTransport`, which implements the same
contract: `RunicWindowTestHost` runs the window on it, and a host's own
composition can take `new InMemoryBridgeTransport()` in place of its transport
and drive it with `Request`, `CloseConnection` and `TakeMessages`. Test the
transport itself with a stub `IBridgeRequestHandler` that records the
requests, keys and `ConnectionClosed` calls it receives and replies in an order
the test chooses, so a test can check that replies and pushes keep their order
and that a slow reply does not hold up the next request.

### Compatibility

- The enums say in their documentation whether they are closed or open.
  `RunicCollectionChangeKind` and `RunicPublicationKind` are closed. The operation,
  admission, cancellation and receipt kinds are open: a minor release may add a
  value, so a `switch` over one needs a default arm. `ErrorKind` and `ErrorReason` are
  strings because BridgeError kinds and reasons are open sets that Runic.Application.Views owns.
- The package has no test-framework types. Failures throw `InvalidOperationException`
  or `TimeoutException`, so it works with TUnit, xUnit, NUnit and MSTest alike, as
  `Runic.CommandLine.Testing` and ASP.NET Core's `WebApplicationFactory` do.
- It uses internals of `Runic.Application.Views`, so it references that package at
  exactly its own version. Keep both at the same version.
- The constructors' `attach` parameter,
  `Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable>`, is the
  factory the generated `AddRunicViews()` registers. It changes only together with
  the generated code.
- `RetainedContentModelCount()` counts every content model whose model-context lease
  the window holds, not only navigation entries. It is a test-only view of window
  internals, which `WindowContentSession` does not expose.

## The whole application in a browser

`RunicWindowTestHost` runs the ViewModels without a page. To test the real page
against the real ViewModels, start the built application in serve-only mode with
`RunicServeOnlyApplication` and point a browser at its URL. It works with an
application on Runic Desktop or CS-WebUI and with any browser driver. With
[Playwright for .NET](https://playwright.dev/dotnet/) and TUnit:

```csharp
[Test]
public async Task IncrementsTheCount()
{
    // The application, built next to the tests, for example through a ProjectReference.
    var start = RunicServeOnlyApplication.CreateStartInfo(
        Path.Combine(AppContext.BaseDirectory, "MyApp.dll"));
    await using var app = await RunicServeOnlyApplication.StartAsync(start);

    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync();
    var page = await browser.NewPageAsync();
    await page.GotoAsync(app.Url.ToString());
    await page.GetByRole(AriaRole.Button, new() { Name = "Increment" }).ClickAsync();
    await Assertions.Expect(page.GetByTestId("count")).ToHaveTextAsync("1");

    var children = app.GetDescendantProcesses();   // while the application runs
    await browser.CloseAsync();
    await Assert.That(await app.StopAsync()).IsEqualTo(0);
    await Assert.That(children.All(child => child.HasExited())).IsTrue();
}
```

`StartAsync` sets `RUNIC_APPLICATION_SERVE_ONLY=1`, removes `DISPLAY` and
`WAYLAND_DISPLAY`, so no window can open, and waits up to 30 seconds (its
`timeout`) for the first `RUNIC_APPLICATION_URL=` line. It reports an
application that exits or stays silent with its standard output and standard
error, which `StandardOutput` and `StandardError` also return. Add arguments
or environment variables to the `ProcessStartInfo` before starting it;
`CreateStartInfo` runs a `.dll` with `dotnet`. `StartAsync` uses the file name,
arguments, working directory and environment and leaves the `ProcessStartInfo`
unchanged, so it can start the application again.

`StopAsync` writes a line to standard input, which ends serving on every
platform, and returns the exit code. When the application has not exited within
its `timeout` (30 seconds), its process tree is killed and `TimeoutException`
is thrown. The line is written once: another `StopAsync`, for example after a
cancelled one, waits for the same exit with its own timeout, and after a kill
it throws `InvalidOperationException`. Disposing stops the application the same
way but kills a hanging one without an exception, so call `StopAsync` when a
test should fail on a shutdown that hangs. To check that the application ends
the processes it started, such as a long-running tool it cancels on shutdown,
list them with `GetDescendantProcesses` while it runs and call `HasExited()`
on each after `StopAsync`. A process whose parent has exited is no longer listed
as a descendant. Each `RunicDescendantProcess` is a snapshot of the process id
and start time that holds no process handle, so there is nothing to dispose.
`HasExited()` looks the process up again and also reports a process whose id
the system has since given to another process as exited.

A test process that exits without disposing the application still ends it on a
best-effort basis: on Windows the application runs in a job object that ends
with the test process, even when the test process is killed, and elsewhere the
application is killed when the test process exits normally. A test process that
is killed itself on Linux or macOS, for example with SIGKILL or by a crash, can
leave the application running.

The page connects to the real Bridge, and the serve-only surface admits one
browser. A test in TypeScript can follow the same protocol, described in the
[Runic.Application.Views.Desktop automation](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.Desktop/README.md#automation)
section: start the application with `RUNIC_APPLICATION_SERVE_ONLY=1`, read the
`RUNIC_APPLICATION_URL=` line, and write a line to its standard input to stop it.
