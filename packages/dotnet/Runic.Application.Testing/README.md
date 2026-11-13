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
`SnapshotAsync`, `SetAsync`, `SetJsonAsync`, `WriteAsync`, `WritePathAsync`, `ExecuteAsync`,
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
already have. `T` may be an interface or abstract class that the presented
ViewModel implements, such as a
[`[RunicSharedClient]`](../Runic.Application.Views/README.md#shared-clients)
interface, so one test drives every ViewModel that publishes it (since 0.17). A `RunicViewDriver<T>` has:

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
- `WritePath(vm => vm.Lines, ["7", "quantity"], value, expectedVersion)` writes a
  field path below a member, as the page's `InvoiceFields.lines.at(7).quantity`
  does (`field-paths/1`, since 0.16): the row's key and the cell for a
  `[RunicCollection]` with a `[RunicRowEditor]`, or member names such as
  `["street"]` for an object member. The baseline is the path's value and, unless
  `expectedVersion` is given, the state's revision; an older revision after another
  write of the cell tests the `Conflict` receipt. Test connections list
  `field-paths/1` in their `hello`.
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

### Several pages of one window

Each driver sends its requests on the transport's test connection,
`connection-1` of client `client-1`. `host.Root.ForConnection(connectionKey,
clientKey)` (since 0.16) returns a driver of the same route on another
connection, as a second browser tab of a multi-client CS-WebUI window. The
drivers it returns from `View<T>`, and the operations it starts, use that
connection too. A connection sends its `hello` with its first request, and
`host.Transport.CloseConnection(key)` closes it as a page that leaves does.

The window's `IRunicConnections` (see
[Who is connected, and who called](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#who-is-connected-and-who-called))
then lists the connections, and a command runs with its driver's connection as
`Caller`. `host.Connections` reads the same list. A window opened with
`RunicWindowTestHost.Create(services)` binds the `IRunicConnections` of
`services`, such as the scope the ViewModel took it from. The window gives each
connection a key of its own that no later connection reuses (`connection-1#1`
on this transport), and a driver's `Connection` returns it:

```csharp
var bob = host.Root.ForConnection("bob-tab", clientKey: "bob");
(await host.Root.ExecuteAsync(vm => vm.ClaimCommand)).ThrowIfFailed();
(await bob.ExecuteAsync(vm => vm.ClaimCommand)).ThrowIfFailed();
Assert.Equal([host.Root.Connection, bob.Connection], host.Connections.Current);

host.Transport.CloseConnection("bob-tab");
await host.Content.ModelContext!.InvokeAsync(() => { }); // Disconnected runs in a model turn
Assert.Single(host.Connections.Current);
```

`Connected` and `Disconnected` are raised in a turn of the window's model
context: a command's reply comes after the `Connected` of its connection, and
after `CloseConnection` a test awaits a model turn before it reads the list.

A test connection reports no languages by default.
`host.Transport.SetLanguages(connectionKey, ["de-CH", "de"])` (since 0.17),
before the connection's first request, makes its `hello` report them, as a
browser reports `navigator.languages`; the window then lists them as the
connection's `RunicConnection.Languages`, in BCP 47 case and each once, as the
window keeps a browser's, and the driver's `Connection` carries them too.

### The window's language

A window follows the application's locale while it sets none of its own, as in
the application ([translated text](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#translated-text)).
`host.ApplicationLocale` (since 0.17) is the `IRunicApplicationLocale` registered
in the services of `RunicWindowTestHost.Create(services)` and
`CreateWindowAsync`, or, for a host built with a constructor, a locale of that
host alone. Switch it as the application does, then read the window's locale:

```csharp
await host.ApplicationLocale.SetLocaleAsync("de");
Assert.Equal("de", host.Content.Locale);              // pushed to pages with translation-text/1
```

The window follows in a turn of its model context, as in the application, and
reading `host.ApplicationLocale` after the host was disposed throws
`ObjectDisposedException`.

Two options (since 0.17) start a test in a language:

- `RunicWindowTestHostOptions.Locale` is the window's own locale from the
  start, the way a ViewModel that sets `IRunicWindowLocale.Locale` before its
  Window opens does; that ViewModel's choice still wins. The window keeps it
  when the application switches. It means the same on every host: the
  constructors, `Create`, and each Window that `CreateWindowAsync` opens.
- `RunicWindowTestHostOptions.ApplicationLocale` starts the application in a
  locale, as the application's startup code does before its first Window opens.
  Windows without a locale of their own follow it, and follow a later switch.
  `Create` and `CreateWindowAsync` set the `IRunicApplicationLocale` the
  services register (with its followers, such as a Translations context), and
  disposing the host, or the Window that `CreateWindowAsync` returned, sets it
  back to what it was, so tests that share one provider do not inherit the
  language. A host built with a constructor, or whose services register none,
  starts its own `host.ApplicationLocale` in it. `CreateWindowAsync` requires
  `AddRunicWindowService()` for it.

With `FlowCultureFromLocale = true`, the window's requests also run in the
locale's culture.

```csharp
// The application starts in German and can switch, as a user would.
using var host = RunicWindowTestHost.Create<EditorViewModel>(services,
    new RunicWindowTestHostOptions { ApplicationLocale = "de", FlowCultureFromLocale = true });
await host.ApplicationLocale.SetLocaleAsync("en");   // the window follows

// This window stays in Arabic whatever the application shows.
using var arabic = RunicWindowTestHost.Create<EditorViewModel>(services,
    new RunicWindowTestHostOptions { Locale = "ar" });
```

**Translated text.** The Testing package does not reference Runic
Translations. To check the text a user sees, read a `TranslationReference`
member and resolve it with the application's `TranslationContext` in the same
locale. A catalog with a right-to-left locale isolates placeholders with
invisible characters; compare after `BidiText.RemoveIsolates`:

```csharp
TranslationReference status = host.Root.Snapshot().Read(vm => vm.Status);
await translations.SetLocaleAsync("de");   // de.rmf2: saved = {$title} gespeichert.
Assert.Equal("Atlas gespeichert.", translations.Resolve(status).Text);
await translations.SetLocaleAsync("ar");
Assert.Equal("تم حفظ Atlas.", BidiText.RemoveIsolates(translations.Resolve(status).Text!));
```

**End-to-end tests in a language.** Applications created with
`dotnet new runic-app --translations` start in the catalog's language for the
`RUNIC_LOCALE` environment variable (`de-CH` starts in `de`), else in the
operating system's language; the template saves no choice of the user's. With
serve-only, a browser test runs the unchanged application in that language:

```sh
RUNIC_APPLICATION_SERVE_ONLY=1 RUNIC_LOCALE=de dotnet run --project src/App
```

`RUNIC_LOCALE` is a convention of the application's startup code, not read by
the Runic packages; see
[the application's language](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#translated-text).

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

**Saving and restoring navigation (since 0.16).** A restart is a new window
scope. `navigator.SavePath()` returns the window's
[navigation path](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md#routes-saved-paths-and-deep-links)
as the JSON an application stores, and `RestorePathAsync(saved)` restores it in
the new window's navigator, waits until it is idle and reads the path back.
`IsExact` is `true` when nothing was left out and the restored path is the
saved one:

```csharp
var saved = scope.ServiceProvider.GetRequiredService<RunicNavigator>().SavePath();
host.Dispose();
await scope.DisposeAsync();

await using var restarted = services.CreateAsyncScope();
using var again = RunicWindowTestHost.Create<ShellViewModel>(restarted.ServiceProvider);
var restore = await restarted.ServiceProvider.GetRequiredService<RunicNavigator>().RestorePathAsync(saved);
await Assert.That(restore.IsExact).IsTrue(); // otherwise restore.Result.Issues says what was left out
```

**Back and the URL (since 0.16).** `host.NavigateBackAsync()` sends the
window's Back, as the mouse's Back button does in a page that called
`installBackNavigation()`. `host.OpenNavigationPathAsync(path)` opens a route
path as a browser's Back, Forward or address bar does in a page that called
`installNavigationPath()`. Both return a `RunicNavigationReply` with the
`Outcome` (`null` when no route matched) and the window's `Path` afterwards,
which the page's URL shows. `host.NavigationPathPushes` lists the path changes
the page was told about, with their history operation:

```csharp
using var path = RunicPresentation.SyncNavigationPath(shell.Main);
var refused = await host.OpenNavigationPathAsync("home");   // a browser Back that a dirty editor refuses
await Assert.That(refused.Outcome?.Rejection).IsEqualTo(NavigationRejection.Guard);
await Assert.That(refused.Path).IsEqualTo("editors/a");     // the URL the page puts back
await Assert.That(host.NavigationPathPushes[^1]).IsEqualTo(new RunicNavigationPathPush("editors/a", "push"));
```

**Appearance (since 0.17).** The test connection lists `appearance/1`. Set
`host.Content.Appearance` as `RunicDesktopHost` does from the desktop's
settings; `host.AppearancePushes` lists the appearances the page was told about,
read back as `DesktopAppearance` (`null` for an unknown one):

```csharp
host.Content.Appearance = new DesktopAppearance(DesktopColorScheme.Dark) { Contrast = DesktopContrast.ForcedColors };
await Assert.That(host.AppearancePushes[^1]?.Contrast).IsEqualTo(DesktopContrast.ForcedColors);
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

A [region collection](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#region-collections)
(since 0.16) reads as a `RunicRegionCollectionState`, and the drivers send the
page's select and close requests by key:

```csharp
var tabs = (await host.Root.SnapshotAsync()).Regions(vm => vm.Documents);
// tabs.Keys, tabs.Regions[i].Content (a PageReference or null), tabs.Selected
var closed = await host.Root.CloseRegionAsync(vm => vm.Documents, tabs.Keys[0]);
// closed.RegionOutcome: Committed, or Rejected with Guard when a page refused
```

`SelectRegionAsync` and `CloseRegionAsync` return a `RunicCallReply` with the
state after the request; a key that no region has fails with `ErrorReason`
`unresolved-key`. The test connection's `hello` lists `region-collections/1`
for these requests; it adds a request kind only, so the other messages are
those of the base protocol.

### Interactions

A test can act as the page: `RunicViewDriver.MountPresentation()` (since 0.16) opens a
presentation that answers the ViewModel's questions and interactions with the
handlers the test registers, and receives its view requests:

```csharp
using var page = await host.Root.MountPresentationAsync();
page.Handle(vm => vm.ConfirmDiscard, request => request.Title == "Draft");           // a ViewQuestion<,>
page.Handle<DiscardNoteRequest, bool>(vm => vm.ConfirmLeave, request => true);        // a ReactiveUI Interaction<,>
(await host.Root.ExecuteAsync(vm => vm.DiscardCommand)).ThrowIfFailed();

var focus = await page.NextViewRequestAsync();
Assert.Equal(new FocusTarget("title"), focus.Input<FocusTarget>());
Assert.Empty(page.TakeViewRequests(vm => vm.Focus));
```

The presentation handles every `ViewRequest<T>` of the ViewModel from the
moment it opens, so the requests reach it by their delivery; one that
`Mount()` opened handles none. `Handle` registers one more handler, as the
generated client's `interactions.<name>.handle` does, and replaces an earlier
one of the same member; `HandleAsync` takes an asynchronous answer, which is
cancelled when .NET cancels the request. An answer that throws answers
`failed`. `RunicViewRequest.Input<T>()` reads the input with System.Text.Json's
web defaults; `InputJson` is the wire JSON. Pass `clientKey` and
`connectionKey` to `MountPresentation` to act as one of several pages: a command sent
on that connection reaches only its presentation. Dispose the presentation to
stop answering, or call `Unmount()`.

A test can also answer a ReactiveUI `Interaction`, such as the question of a
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

A driver's `Mount()` opens a presentation without registering any
interaction handler, so with only mounted presentations a question never waits
for a browser: it goes to the .NET handlers. With no handler that answers, the
departure fails closed with ReactiveUI's `UnhandledInteractionException`, as
in the application.

The browser's side is tested with the frontend: the generated typed mock's
`interactions.<name>.request(input)` resolves with the answer of the handler
the View registered with `interactions.<name>.handle(...)`, and
`requests.<name>.raise(input)` calls the View's `requests.<name>.handle(...)`
handler; see the
[Testing guide](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#testing).
Which View is asked for which departure, and the fallback when none is
mounted, are covered by the SDK's own tests and described in
[Leave confirmation in a web View](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#leave-confirmation-in-a-web-view).

A CommunityToolkit ViewModel asks the page with a `ViewQuestion<TInput, TOutput>`,
which `Handle` answers as above; a Toolkit guard that asks in a dialog region
is answered by executing the dialog's command through its driver, as in
[Navigation](#navigation).

### The Window's presentation

A ViewModel that takes `IRunicWindowPresentation` presents, hides or minimizes
its Window and reacts to its activity. `AddRunicTestWindowPresentation()`
registers a recording `RunicTestWindowPresentation` per scope as the host of the
scope's presentation, in place of the real one, so the application's own
registration method runs unchanged. The ViewModel gets the same per-Window
presentation an application does, with the fake as its host: a change reaches
the ViewModel in a turn of the window's model context after the request that
caused it completed, a handler that throws is logged (event 1130) without
stopping the others, and once the scope is disposed requests report
`OwnerClosed`. `host.Presentation` returns the fake;
`host.SetWindowActivityAsync` changes its activity and waits until the
ViewModel received it, and `host.FlushWindowPresentationAsync` waits for the
changes a command's requests caused:

```csharp
await using var services = new ServiceCollection().AddInbox().AddRunicTestWindowPresentation().BuildServiceProvider();
await using var scope = services.CreateAsyncScope();
using var host = RunicWindowTestHost.Create<InboxViewModel>(scope.ServiceProvider);

(await host.Root.ExecuteAsync(vm => vm.HideToTrayCommand)).ThrowIfFailed();
Assert.Equal([new RunicWindowPresentationRequest(RunicWindowPresentationRequestKind.SetVisible) { Value = false }],
    host.Presentation!.Requests);

await host.SetWindowActivityAsync(new RunicWindowActivity(IsActive: false, IsMinimized: false, IsVisible: true));
Assert.Equal(TimeSpan.FromMinutes(1), host.ViewModel.PollInterval);
```

The fake behaves like a desktop window: `PresentAsync` shows, restores and
activates it, `MinimizeAsync` minimizes it, `SetVisibleAsync(false)` hides it,
and `SetVisibleAsync(true)` shows it again without restoring it, so a minimized
window stays minimized, as Win32's `SW_SHOW` leaves it. `IsFullScreen`,
`IsDocumentEdited` and `ColorScheme` follow their requests, and `PresentCount`
counts the presentations. Set `Features` to test a host that cannot do
something (the request then reports `BackendUnavailable` and changes nothing),
and `Respond` to answer with another result, such as a desktop that refuses the
focus.

`AddRunicTestWindowPresentation(immediate: true)` makes the fake itself the
scope's presentation, and a ViewModel built by hand can take a
`new RunicTestWindowPresentation()`. Used directly, the fake raises its changes
at once on the calling thread, inside the request, and keeps answering after
the scope ends; a change its own handlers cause is still raised after them.

### Windows and their close decision

`RunicWindowTestHost.Create` drives one ViewModel. To test a Window, its close
guard and the Windows its ViewModels open, open it with
`RunicWindowTestHost.CreateWindowAsync<TWindow>(services)` (since 0.16). It
opens a real `RunicWindow` headless, as a host does: in a new scope of the
application's root services, with its parameter, result and close guard, and with
the application's own `IRunicWindowService` (`AddRunicWindowService()`), so
`FindOrOpenAsync`, `ActivateAsync` and `OpenForResultAsync` open test Windows
too.

```csharp
await using var services = new ServiceCollection().AddMyApplication().BuildServiceProvider();
await using var shell = await RunicWindowTestHost.CreateWindowAsync<ShellWindow>(services);

// A guarded editor keeps unsaved changes.
(await shell.Root<ShellViewModel>().ExecuteAsync(vm => vm.OpenDocumentCommand, "\"notes.md\"")).ThrowIfFailed();
var editor = shell.OpenedWindows.Single();
Assert.Equal("notes.md", editor.Key);
Assert.False(await editor.RequestUserCloseAsync());

// A modal dialog that returns a value.
var choosing = shell.Root<ShellViewModel>().ExecuteAsync(vm => vm.ChooseAccountCommand);
var dialog = await shell.WaitForOpenedWindowAsync<AccountWindow>();
Assert.True(shell.IsBlocked);
(await dialog.Root<AccountViewModel>().ExecuteAsync(vm => vm.ChooseCommand, "\"Savings\"")).ThrowIfFailed();
(await choosing).ThrowIfFailed();
```

- `RequestUserCloseAsync()` is the user's close button: a Window with
  `GuardClose` asks `CanCloseAsync`, and the result tells whether it closed. It
  shares one decision with `IRunicWindow.RequestCloseAsync`, as on Runic Desktop.
- `OpenedWindows` lists the Windows opened after this one, including closed
  ones, each with `Window`, `ViewModel`, `Parameter`, `Key`, `Owner`,
  `Modality`, `IsOpen` and its `Root<TViewModel>()` driver;
  `WaitForOpenedWindowAsync<TWindow>()` waits for one, for example a dialog
  whose command is still waiting for its result. `Host<TViewModel>()` is the
  Window's `RunicWindowTestHost<TViewModel>`.
- Each Window has a `RunicTestWindowPresentation` (`Presentation`), the one its
  scope registers or a new one, so activation shows as `PresentCount`.
- As on Runic Desktop: an owned Window closes when its owner closes, hides and
  shows with its owner's `SetVisibleAsync`, and an owner's close asks its owned
  Windows' guards first, innermost first. `IsBlocked` is `true` while a
  window-modal Window it owns is open. The test host does not pass where the
  desktop cannot: on a blocked Window `RequestUserCloseAsync()` and its page's
  commands, operation starts, sets and writes throw `InvalidOperationException`
  (snapshots still read it); the application's `RequestCloseAsync` still works.
- `DisposeAsync` of the Window `CreateWindowAsync` returned closes the whole test
  application; that of an entry of `OpenedWindows` closes only that Window and
  the Windows it owns.
- All Windows share one clock (a `FakeTimeProvider` unless the options name
  another) and one id source. Disposing the result closes and releases every
  Window. A command that closes its own Window is cancelled by the close, as on
  every host, so don't assert its reply.

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

### Platform services

A ViewModel that shows a file dialog, a message box or a context menu, or uses
the clipboard, takes `IFileDialogs`, `IDesktopMessageBoxes`, `IDesktopClipboard`
or `IWindowPlatformServices` in its constructor. `services.AddRunicTestPlatform()`
registers scripted, recording fakes for them: one `RunicTestPlatform` per window
scope and one `RunicTestNotifications` for the application. It replaces the
registrations of `AddRunicPlatformServices()` whether it runs before or after
it. `RunicWindowTestHost.Create` returns the scope's platform as `Platform`:

```csharp
services.AddRunicTestPlatform();
await using var scope = provider.CreateAsyncScope();
using var host = RunicWindowTestHost.Create<EditorViewModel>(scope.ServiceProvider);

host.Platform!.FileDialogs.EnqueueOpen("notes.txt", "Hello");
var saved = host.Platform.FileDialogs.EnqueueSave("notes-copy.txt");
host.Platform.MessageBoxes.EnqueueAnswer("save");

(await host.Root.ExecuteAsync(vm => vm.OpenCommand)).ThrowIfFailed();
(await host.Root.ExecuteAsync(vm => vm.SaveAsCommand)).ThrowIfFailed();

Assert.Equal("Hello", saved.ReadAllText());
Assert.Equal("Import notes", host.Platform.FileDialogs.Calls[0].OpenOptions?.Title);
```

Each fake takes the next queued response and records the call:

- **File dialogs.** `EnqueueOpen(file)` answers an open-file or open-document
  dialog, `EnqueueOpenFiles(...)` an open-files dialog, `EnqueueSave(name)` a
  save dialog, `EnqueueOpenDirectory(path)` a directory dialog, and
  `EnqueueDismissed()`, `EnqueueUnavailable(reason)` and `EnqueueFailed(code)` any
  dialog. A dialog with an empty queue is dismissed; a response for another kind
  of dialog throws `InvalidOperationException` from the dialog call. `Calls`
  lists each dialog with its options and outcome.
- **Files and leases.** A `RunicTestFile` is in memory. Its leases follow the
  real rules: a read lease is consumed once, a save lease has one open write
  transaction at a time (`ResourceBusy` otherwise), a commit happens once, and
  it reports `NotCommitted(Conflict)` when the file no longer holds what the
  application last saw. `file.Write(...)` or `file.Delete()` acts as another
  writer; `file.EnqueueCommitResult(...)` scripts a failed or uncertain commit,
  and `EnqueueCommitResult(new FileCommitResult.CommitUnknown(code), applied: true)`
  an uncertain one whose write landed, as a real lease can report;
  `file.WriteUnavailable` acts as a read-only grant. `ReadAllText()`,
  `ReadAllBytes()` and `CommittedWrites` show what the application saved.
- **Message boxes.** `EnqueueAnswer(buttonId)` chooses a button. An unscripted
  box is answered as Esc would be: with its cancel button, or `null`.
- **Context menus.** `ContextMenus.EnqueueChoice(commandId)` chooses an item of
  `ShowContextMenuAsync`; a disabled item throws, since the user could not
  choose it. An unscripted menu is dismissed.
- **Clipboard.** `Clipboard.SetText(...)` or `SetContent(...)` is what another
  application copied; `Content` and `Writes` are what the application copied.
  Writes, limits and watches follow the real clipboard.
- **Notifications.** `Shown`, `Current` and `Removed` record the application's
  notifications, and `Activate(id, actionId)` raises `Activated`.
- **Launcher and announcements.** `Launches` and `Announcements` record files and
  URIs handed to native applications and screen-reader announcements.

The fakes check their arguments with the real services' rules. Disposing the host
closes the platform, as closing a window does: every service then reports
`OwnerClosed`, file leases are released and clipboard watches end. Monitors, the
menu bar, taskbar badges and progress, and drags report `BackendUnavailable`. A
ViewModel built by the test can take a `new RunicTestPlatform()` directly.

### Query collections

A driver reads a `RunicQueryCollection` as a page does: `QueryRowsAsync(vm => vm.Rows, start, count)` returns the
range's keys and rows (`connectionKey:` reads it as another page), `SetQueryAsync(vm => vm.Rows, query)` changes the
query and returns the state after it, and `IndexOfAsync(vm => vm.Rows, key)` finds a row. `RunicTestRowSource<TRow, TKey>`
serves a list through `RunicQueryFields`, waits `Latency` on the host's clock, and records `Loads`, `RowsLoaded`,
`Counts`, `Searches` and `MaximumConcurrency`, so a test can assert what scrolling and sorting loaded:

```csharp
var source = new RunicTestRowSource<Order, int>(orders, fields, order => order.Id);
using var host = RunicWindowTestHost.Create(services, new OrdersViewModel(source, context));
var rows = await host.Root.QueryRowsAsync(vm => vm.Orders, 500, 40);
Assert.Equal(1, source.Loads.Count);
(await host.Root.SetQueryAsync(vm => vm.Orders, new RunicQuery([new RunicSortKey("amount", Descending: true)]))).ThrowIfFailed();
```

The test connection's `hello` lists `query-collections/1`.

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

## Blobs and uploads

A window of `RunicWindowTestHost` serves its blobs below `http://runic.test/`
(Views feature `blobs/1`). `ReadBlobAsync(url)` returns what the host serves
for a blob URL of the state or a result, as `RunicServedBlob`: status, headers
and content, with `range` for a byte range and `head` for `HEAD`. A replaced or
released URL answers 404, and `ServedBlobCount` counts the URLs the window
still serves. `UploadAsync(bytes, fileName, contentType)` simulates a page's
upload with the window's limits (`RunicWindowTestHostOptions.MaximumUploadBytes`)
and returns the `RunicUpload` that a command input takes;
`PendingUploadCount` counts the uploads no command claimed.

```csharp
var upload = await host.UploadAsync("hello"u8.ToArray(), "notes.txt", "text/plain");
await host.Root.ExecuteAsync(vm => vm.AttachCommand, upload);
var avatar = host.Root.Snapshot().Get(vm => vm.Avatar).GetProperty("url").GetString()!;
var served = await host.ReadBlobAsync(avatar);   // served.StatusCode, served.ContentType, served.Content
```

A ViewModel test without a window uses `RunicUpload.FromBytes` and
`RunicBlob.FromBytes`, and reads a blob with `RunicBlob.OpenReadAsync`.

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

An application that opens more than one Window writes a URL for each.
`Urls` lists those written so far, in the order the Windows opened, and
`WaitForWindowUrlAsync` waits for one, by its position or by its Window type:

```csharp
var settings = await app.WaitForWindowUrlAsync("SettingsWindow", TimeSpan.FromSeconds(10));
var second = await app.WaitForWindowUrlAsync(1, TimeSpan.FromSeconds(10));
```

The type is the Window class's full name or its name without the namespace,
as the host names it on its `RUNIC_APPLICATION_WINDOW=` line from 0.16 on.
A closed generic Window type has no spaces there: its generic definition's
name with its arguments' names without namespaces, such as
``DocumentWindow`1[Report]``.
A wait that runs out throws `TimeoutException` and leaves the application
running. A wait on an application that has exited throws
`InvalidOperationException`.

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

### Platform services in a browser test

The application in a serve-only browser test runs in its own process, so a test
scripts its platform fakes through files. The application opts in from a test
build: call `services.AddRunicServeOnlyTestPlatform()` after its own
`AddRunicPlatformServices()`. It does nothing unless the process runs with both
`RUNIC_APPLICATION_SERVE_ONLY=1` and `RUNIC_TEST_PLATFORM_DIRECTORY` set. Then
it registers the fakes only when the build opted in as well: the application's
entry assembly must carry `[assembly: RunicServeOnlyTestPlatform]`, or the call
throws `InvalidOperationException`. It also throws when the directory is
missing or, outside Windows, when users other than its owner can write to it
(`RunicServeOnlyTestPlatform.Create()` makes it `0700`). When the fakes are
active, the first window logs warning 1158 `ServeOnlyTestPlatformActive` in the
category `Runic.Application.Testing`. Production builds must not reference this
package, so keep the call, the attribute and the reference behind a test-only
build, for example:

```xml
<ItemGroup Condition="'$(Configuration)' == 'E2E'">
  <PackageReference Include="Runic.Application.Testing" />
</ItemGroup>
<PropertyGroup Condition="'$(Configuration)' == 'E2E'">
  <DefineConstants>$(DefineConstants);RUNIC_E2E</DefineConstants>
</PropertyGroup>
<ItemGroup Condition="'$(Configuration)' == 'E2E'">
  <!-- The build-time opt-in that AddRunicServeOnlyTestPlatform checks at run time. -->
  <AssemblyAttribute Include="Runic.Application.Testing.RunicServeOnlyTestPlatformAttribute" />
</ItemGroup>
```

```csharp
services.AddRunicPlatformServices();
#if RUNIC_E2E
services.AddRunicServeOnlyTestPlatform();
#endif
```

The test creates a `RunicServeOnlyTestPlatform`, which owns a temporary directory,
points the application at it and queues responses before the page action:

```csharp
using var platform = RunicServeOnlyTestPlatform.Create();
var start = platform.Configure(RunicServeOnlyApplication.CreateStartInfo(appPath));
await using var app = await RunicServeOnlyApplication.StartAsync(start);
await page.GotoAsync(app.Url.ToString());

platform.EnqueueSaveFile("report.csv");
await page.GetByRole(AriaRole.Button, new() { Name = "Export" }).ClickAsync();
var commit = await platform.WaitForEventAsync(e => e.Operation == "commit");
Assert.Equal("a,b\n", commit.GetText("content"));
```

`EnqueueOpenFile`, `EnqueueSaveFile`, `EnqueueOpenDirectory`,
`EnqueueFileDialogDismissed`, `EnqueueMessageBoxAnswer`,
`EnqueueContextMenuChoice` and `SetClipboard` script the fakes. `ReadEvents()`
and `WaitForEventAsync(match)` return each call the application made, such as
`fileDialogs.saveFile` with its `title`, `fileDialogs.commit` with the file and
its base64 `content`, `clipboard.write`, `messageBoxes.show` with the `button`
chosen and `notifications.show`. Every window of the application shares one
platform, since the script is one queue; closing a window does not close it.
A scripted line that the application cannot apply, such as malformed JSON or an
unknown service, does not fail the application's call: it is recorded as a
`script.error` event with the `message`, and `WaitForEventAsync` then throws
`InvalidOperationException` with that message.

### A hosted application

An application that can be hosted in another Runic application's windows
([hosted mode](../Runic.Application.Views.Desktop/README.md#hosted-mode)) is
tested without a native host: `RunicTestHost` plays the host of the
[Runic host protocol](../../../specs/desktop/host-protocol.md). It starts the
application with the parameter that selects its hosted mode, completes the
handshake, answers each Window's `window-open` and exposes the Window's one-time
launch address, which the browser under test opens once, as the host's window
would. The API is experimental: suppress
[`RUNICDESK103`](../../../docs/experimental.md#RUNICDESK103).

```csharp
#pragma warning disable RUNICDESK103 // Hosted application windows are experimental.
[Test]
public async Task ClosesWhenTheHostAsks()
{
    var start = RunicServeOnlyApplication.CreateStartInfo("../../../../Tool/bin/Debug/net10.0/Tool.dll");
    start.ArgumentList.Add("hosted");            // The parameter the application defines for hosted mode.
    await using var host = await RunicTestHost.StartGuestAsync(start);
    var window = await host.WaitForWindowAsync("MainWindow", TimeSpan.FromSeconds(30));
    await page.GotoAsync(window.LaunchUrl.ToString());

    Assert.True(await window.RequestCloseAsync());   // close-request: the Window's guard decides.
    await window.Closed;
    Assert.Equal(0, await host.StopAsync());          // shutdown, then the exit code.
}
```

- `RunicTestHostOptions` sets the offer (`HostName`, `HostVersion`,
  `Protocols`), the `StartTimeout`, the presentation features each window
  reports (`WindowFeatures`), a policy that refuses windows (`OpenWindow`) and
  the outcome of each presentation request (`Presentation`). Each window's
  `PresentationRequests` lists what the application asked, such as `present`
  or `set-full-screen`. A request whose feature the window did not report is
  answered unavailable, and a kind outside the base protocol is rejected with
  `invalid-request`, as a real host does.
- A window is listed in `Windows`, and found by `WaitForWindowAsync`, once the
  test host answered its `window-open`; its `Opened` task has then completed
  (or is cancelled for a refused window), so a test may stop the host at once.
  `WaitForNthWindowAsync("DocumentWindow", 1, …)` waits for the second window of
  a type, and `WaitForMatchingWindowAsync` for the first window a predicate
  accepts.
- Menus and dialogs (`menus/1`, `dialogs/1`, listed by default in
  `RunicTestHostOptions.Features`): a window's `MenuBar` is the application's
  bar as it sent it, with its items' ids, titles, shortcuts and current
  `enabled` and `checked` states; `WaitForMenuBarAsync` waits for a change,
  `FindMenuItem("File", "Save")` finds an item's id and `InvokeMenuItemAsync`
  chooses it as the host's user would, so the application runs its command.
  `FileDialog`, `MessageBox` and `ContextMenu` answer the application's file
  dialogs, message boxes and context menus with a `RunicTestDialogAnswer`:
  `Select(paths, filter)` with absolute paths and the index of the chosen
  filter, `Choose(id)` with a button's or an item's id
  (`RunicTestHostedWindow.FindMenuItem(menu, "Copy")`), `Dismiss()`, or what a
  host can answer instead: `Refuse()` (the application sees
  `PermissionDenied`), `Fail(code)` and `Unavailable(reason)`, such as
  `OwnerClosed`. By default every dialog and menu is dismissed. Each window's
  `DialogRequests` lists what the application asked for, with typed members
  (`DialogKind`, `Title`, `SuggestedName`, `FilterNames`, `Text`, `ButtonIds`),
  and an answer's token is cancelled when the application gives the dialog up
  or closes the window. An answer that throws or does not fit, such as a
  relative path, fails the application's dialog, and the test host's next
  `WaitFor…` or `StopAsync` throws with the reason.

  ```csharp
  await using var host = await RunicTestHost.StartGuestAsync(start, new RunicTestHostOptions
  {
      FileDialog = (request, _) => Task.FromResult(RunicTestDialogAnswer.Select([Path.GetFullPath("data/notes.txt")])),
  });
  var window = await host.WaitForWindowAsync("MainWindow", TimeSpan.FromSeconds(30));
  await window.WaitForMenuBarAsync(_ => window.FindMenuItem("File", "Open…") is not null, TimeSpan.FromSeconds(10));
  await window.InvokeMenuItemAsync(window.FindMenuItem("File", "Open…")!);
  ```
- `SetActivityAsync` and `SetColorSchemeAsync` report a window's activity and
  colour scheme to the application. The host's locale, its user's languages
  and its appearance start as `RunicTestHostOptions.Locale`, `Languages` and
  `Appearance` (none by default) and change with `RunicTestHost.SetLocaleAsync`
  and `SetAppearanceAsync`: the application's locale follows them unless it set
  its own, each window's `Locale` is the one its `window-open` named, a
  direction change shows in `PresentationRequests` (`set-layout-direction`),
  and the Windows' pages receive the appearance.
- Window services (`window-services/1`, listed by default): `RunicTestHost.Clipboard`
  is the host's clipboard (a `RunicTestClipboard`: `SetText` what the user
  copied, read `Writes` after the application wrote), read only for an active
  window unless `ClipboardReadsOnlyActiveWindow` is `false`, as Runic's host does;
  `Monitors` (`RunicTestHostOptions.Monitors`, one 1920 × 1080 monitor by
  default) are the host's, and `SetMonitorsAsync` changes them, which the
  application's monitor watch follows; each file or link the application opens is
  recorded in the window's `LaunchRequests` and answered by `Launch` (success by
  default); and `DropFilesAsync(paths)` drops files onto a window whose Window
  takes them (`AcceptsFileDrops`).

  ```csharp
  await using var host = await RunicTestHost.StartGuestAsync(start, new RunicTestHostOptions { Locale = "de", Appearance = new(DesktopColorScheme.Dark) });
  var window = await host.WaitForWindowAsync("MainWindow", TimeSpan.FromSeconds(30));
  host.Clipboard.SetText("pasted");
  await window.DropFilesAsync([Path.GetFullPath("data/notes.txt")]);
  await host.SetLocaleAsync("fr");
  ```
- Regions (`region/1`, from 0.18.0; add it to `Features`): the windows that
  `RunicTestHostOptions.EmbedWindow` chooses show in a region of a host page
  instead of a window of their own (`IsRegion`). `AttachRegionAsync(embedder)`
  attaches one and returns the guest's one-time frame launch address: load it
  once, in an `<iframe>` of a page whose origin is `embedder`, such as
  `http://127.0.0.1:<port>` of a test page your Playwright fixture serves; the
  guest lets no other origin frame its page. The page's title arrives in
  `Titles`, its content height as a `resize` in `PresentationRequests` when the
  region was attached with `sizing: "content-height"`, and what happened to the
  page in `RegionPages`. `FocusRegionAsync`, `SetRegionBoundsAsync` and
  `DetachRegionAsync` play the host page's focus, size and removal.

  ```csharp
  await using var host = await RunicTestHost.StartGuestAsync(start, new RunicTestHostOptions
  {
      Features = ["menus/1", "dialogs/1", "window-services/1", "region/1"],
      EmbedWindow = _ => true,
  });
  var window = await host.WaitForWindowAsync("MainWindow", TimeSpan.FromSeconds(30));
  var frame = await window.AttachRegionAsync($"http://127.0.0.1:{hostPagePort}");
  await page.GotoAsync($"http://127.0.0.1:{hostPagePort}/region.html?src={Uri.EscapeDataString(frame.AbsoluteUri)}");
  ```
- Components (`region-component/1`; add it to `Features`, and allow it in the
  guest with `RunicGuestOptions.AllowComponentEmbedding`):
  `AttachComponentAsync(embedder)` attaches a region window as a component and
  returns its `Module`, `Url` and `Token`. Import `Module` in a test page whose
  origin is `embedder`, create the element its `tag` export names and call
  `element.connect({ url, token })`; the guest admits the connection only from
  that origin, and serves its component build only to it. The page's
  Content-Security-Policy must allow the guest's origin.
- The application's `OpenWindowAsync` waits for the page to connect when its
  `DesktopHostOptions.WaitForConnection` is set, the default, as it does with a
  real host. Load `LaunchUrl` in the browser under test, or start the
  application without that wait for a test without a browser. The
  [hosted windows example](../../../examples/hosted-windows/Counter/Tests/HostedCounterTests.cs)
  does the latter: it checks a guest's menu bar in the host's language, a
  language switch, a message box and a link without a browser, choosing menu
  items with `InvokeMenuItemAsync`. That call returns once the push is sent; the
  application runs chosen items one at a time in the order sent, each once the
  one before has returned, and ignores an item whose command cannot run when its
  turn comes, such as an asynchronous command still running. A test that needs
  an item's effect waits for it, such as its dialog request.
- A start that fails throws `RunicHostedApplicationException` with its `Code`
  (`guest-start-timeout`, `guest-exited`, `guest-not-hostable`, `guest-refused`
  with the application's `GuestCode`, `protocol-unsupported`) and kills the
  application.
- Disposing goes away as a host that exits does: it closes the application's
  standard input and the channel, so the application closes its windows and
  exits, and kills its process tree after 15 seconds.
