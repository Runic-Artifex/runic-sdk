using System.Text.Json;
using ReactiveUI;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class FormsTests
{
    [Test]
    public void EnumsHaveValueListsAndFieldsCarryTheirDataAnnotations()
    {
        var directory = GeneratedTypeScriptDirectory();
        var types = File.ReadAllText(Path.Combine(directory, "types.ts")).ReplaceLineEndings("\n");
        var forms = File.ReadAllText(Path.Combine(directory, "forms.ts")).ReplaceLineEndings("\n");
        var shape = File.ReadAllText(Path.Combine(directory, "codegenShape.ts")).ReplaceLineEndings("\n");
        foreach (var (file, text, expected) in new[]
        {
            // C8: the names .NET writes, so an alias (Urgent = High) is not listed twice.
            ("types.ts", types, "export type FormsPriority = \"Low\" | \"Normal\" | \"High\" | \"Urgent\";\n"
                + "/** The values of {@link FormsPriority} that .NET writes, in declaration order. */\n"
                + "export const FormsPriorityValues = [\"Low\", \"Normal\", \"High\"] as const satisfies readonly FormsPriority[];"),
            ("types.ts", types, "export const CodegenShapeModeValues = [\"None\", \"Active\"] as const satisfies readonly CodegenShapeMode[];"),
            // Each module re-exports the value lists of the enums it uses, beside their types.
            ("forms.ts", forms, "export { FormsPriorityValues } from \"./types.js\";\n"),
            ("codegenShape.ts", shape, "export { CodegenShapeModeValues } from \"./types.js\";\n"),
            ("forms.ts", forms, "    name: \"name\", kind: \"text\", constraints: { required: true, minLength: 2, maxLength: 40 },\n"),
            ("forms.ts", forms, "    name: \"tag\", kind: \"text\", constraints: { maxLength: 8 },\n"),
            ("forms.ts", forms, "    name: \"quantity\", kind: \"number\", integer: true, constraints: { min: 1, max: 10 },\n"),
            ("forms.ts", forms, "    name: \"price\", kind: \"decimal\", constraints: { min: \"0.01\", max: \"999.99\", minExclusive: true },\n"),
            ("forms.ts", forms, "    name: \"code\", kind: \"text\", constraints: { pattern: \"^[A-Z]{3}$\" },\n"),
            ("forms.ts", forms, "    name: \"loose\", kind: \"text\",\n"),
            ("forms.ts", forms, "    name: \"slug\", kind: \"text\",\n"),
            ("forms.ts", forms, "    name: \"zip\", kind: \"text\",\n"),
            ("forms.ts", forms, "    name: \"kebab\", kind: \"text\", constraints: { pattern: \"^[a-z0-9]+(-[a-z0-9]+)*$\" },\n"),
            ("forms.ts", forms, "    name: \"when\", kind: \"datetime\", constraints: { min: \"2020-01-01\", max: \"2030-12-31T12:30:00\" },\n"),
            // A bound that does not read as its type (1e3 is not a decimal in Number style) is left out.
            ("forms.ts", forms, "    name: \"rate\", kind: \"decimal\", constraints: { min: \"0.10\" },\n"),
            ("forms.ts", forms, "    name: \"priority\", kind: \"choice\", choices: [\"Low\", \"Normal\", \"High\"],\n"),
            // C7: an Int64 key is the decimal form of the bigint.
            ("forms.ts", forms, "item => String(item.id))"),
        })
            Require(text.Contains(expected, StringComparison.Ordinal), $"Generated {file} is missing:\n{expected}\n--- {file} ---\n{text}");
    }

    [Test]
    public async Task Int64CollectionKeysTravelAsTheirDecimalFormAsync()
    {
        var model = new FormsViewModel();
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new FormsBridge(transport, model, "forms");
        using (transport.SnapshotState("forms")) { }
        model.Items.Add(new(9_007_199_254_740_993, "beyond 2^53"));
        model.Items.Add(new(-5, "negative"));
        var keys = new List<string?>();
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (keys.Count < 2 && DateTime.UtcNow < timeout)
        {
            foreach (var push in transport.TakePushes("forms"))
                using (push)
                    foreach (var change in push.RootElement.GetProperty("changes").EnumerateArray())
                        if (change.GetProperty("field").GetString() == "rows" && change.GetProperty("kind").GetString() == "add")
                        {
                            keys.AddRange(change.GetProperty("keys").EnumerateArray().Select(key => key.GetString()));
                            Require(change.GetProperty("items")[0].GetProperty("id").ValueKind == JsonValueKind.String,
                                $"An Int64 row field is not a JSON string: {change.GetRawText()}");
                        }
            await Task.Delay(5);
        }
        Require(keys.SequenceEqual(["9007199254740993", "-5"]), $"Int64 keys were not their decimal form: {string.Join(", ", keys)}");
    }

    [Test]
    public async Task PendingValidationIsReportedAndPublishedAsync()
    {
        var model = new PendingFormViewModel();
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new PendingFormBridge(transport, model, "pending");
        using (var initial = transport.SnapshotState("pending"))
        {
            var validation = initial.RootElement.GetProperty("state").GetProperty("validation");
            Require(!validation.TryGetProperty("pending", out _), $"pending is present without a check: {validation.GetRawText()}");
        }

        var check = model.Pending.Begin(nameof(PendingFormViewModel.UserName));
        Require(Pending(await NextValidationAsync(transport)) == "[[\"userName\"]]", "A check that started was not published.");

        // A nested object's own path ("") and an unresolvable one report at the object's path, once.
        using (model.Address.Pending.Begin(nameof(PendingAddress.Postcode)))
        {
            Require(Pending(await NextValidationAsync(transport)) == "[[\"address\",\"postcode\"],[\"userName\"]]",
                "A nested object's check was not reported at its path.");
            using var own = model.Address.Pending.Begin();
            using var unknown = model.Address.Pending.Begin("NoSuchMember");
            var both = await NextValidationAsync(transport, until: value => Pending(value).StartsWith("[[\"address\"],", StringComparison.Ordinal));
            Require(Pending(both) == "[[\"address\"],[\"address\",\"postcode\"],[\"userName\"]]",
                $"The object's own and an unresolvable path were not one path at the object: {Pending(both)}");
        }
        check.Dispose();
        check.Dispose();
        var ended = await NextValidationAsync(transport, until: value => !value.TryGetProperty("pending", out _));
        Require(!ended.GetProperty("hasErrors").GetBoolean(), "Pending validation set hasErrors.");

        var directory = GeneratedTypeScriptDirectory();
        var client = File.ReadAllText(Path.Combine(directory, "pendingForm.ts")).ReplaceLineEndings("\n");
        Require(client.Contains("    validating: state => fieldValidating(state.validation, \"userName\"),\n", StringComparison.Ordinal),
            $"The field descriptor has no validating:\n{client}");
    }

    [Test]
    public void TheTestHostReadsPendingValidation()
    {
        var model = new PendingFormViewModel();
        using var host = new RunicWindowTestHost<PendingFormViewModel>(model,
            (transport, content, vm) => new PendingFormBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "pendingForm", ViewLocator = new TestViewLocator() });
        Require(!host.Root.Snapshot().IsValidating(vm => vm.UserName), "A field without a check is validating.");
        using (model.Pending.Begin(nameof(PendingFormViewModel.UserName)))
        {
            var view = host.Root.Snapshot();
            Require(view.IsValidating(vm => vm.UserName) && !view.IsValidating(vm => vm.Address)
                && view.PendingValidation.Single().SequenceEqual(["userName"]), $"RunicViewState does not read pending: {view}");
        }
        Require(host.Root.Snapshot().PendingValidation.Count == 0, "The check did not end.");
    }

    [Test]
    public async Task PendingPathsAreSortedAndTheirOverflowIsFlaggedApartAsync()
    {
        var model = new PendingFormViewModel();
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new PendingFormBridge(transport, model, "pending");
        using (transport.SnapshotState("pending")) { }
        var leases = new List<IDisposable>();
        // Reported in reverse order: written sorted.
        leases.Add(model.Pending.Begin(nameof(PendingFormViewModel.UserName)));
        leases.Add(model.Pending.Begin());
        var sorted = await NextValidationAsync(transport, until: value => Pending(value).Contains("[]", StringComparison.Ordinal));
        Require(Pending(sorted) == "[[],[\"userName\"]]", $"Pending paths are not sorted: {Pending(sorted)}");
        foreach (var lease in leases) lease.Dispose();
        leases.Clear();
        {
            var writer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var json = new Utf8JsonWriter(writer))
            {
                var nested = new PendingNestedRoot();
                for (var index = 0; index < 300; index++) _ = nested.Pending.Begin($"Rows[{index}].Label");
                Runic.Application.Views.Generated.BridgeValidation.Write(json, nested, PendingNestedRoot.Metadata);
            }
            using var written = JsonDocument.Parse(writer.WrittenMemory);
            var root = written.RootElement;
            Require(root.GetProperty("pending").GetArrayLength() == 256 && root.GetProperty("pendingTruncated").GetBoolean()
                && !root.GetProperty("truncated").GetBoolean(), $"Pending overflow: {root.GetRawText()[..300]}");
            Require(root.GetProperty("pending")[0].GetRawText() == "[\"rows\",0,\"label\"]" && root.GetProperty("pending")[2].GetRawText() == "[\"rows\",2,\"label\"]",
                "Indexes are not sorted by value.");
        }
    }

    [Test]
    public async Task ACancellableTrackCancelsTheOlderCheckOfThePathAsync()
    {
        var pending = new RunicPendingValidation();
        var older = new TaskCompletionSource<string>();
        var results = new List<string>();
        var first = pending.Track("Name", async cancellationToken =>
        {
            // A check that ignores its token: Track still does not report it after a newer one started.
            var value = await older.Task;
            results.Add(value);
            return value;
        });
        CancellationToken newerToken = default;
        var observed = pending.Track("Name", async cancellationToken =>
        {
            newerToken = cancellationToken;
            await Task.Yield();
            results.Add("newer");
        });
        await observed;
        Require(pending.IsPending("Name"), "The older check still runs, so the path stays pending.");
        older.SetResult("older");
        var cancelled = false;
        try { await first; }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled && !newerToken.IsCancellationRequested && !pending.HasPending,
            $"The older check was not cancelled: {string.Join(", ", results)}");
        // A cancellable check sees its token cancelled by the next one.
        var token = new TaskCompletionSource<CancellationToken>();
        var gate = new TaskCompletionSource();
        var waiting = pending.Track("Email", async cancellationToken => { token.SetResult(cancellationToken); await gate.Task.WaitAsync(cancellationToken); });
        var seen = await token.Task;
        var next = pending.Track("Email", _ => Task.CompletedTask);
        await next;
        Require(seen.IsCancellationRequested, "The newer check did not cancel the older one's token.");
        try { await waiting; } catch (OperationCanceledException) { }
        Require(!pending.HasPending, "A cancelled check stayed pending.");
    }

    [Test]
    public async Task TheTrackerCountsOverlappingChecksAndFollowsStreamsAsync()
    {
        var pending = new RunicPendingValidation();
        var changes = new List<string>();
        pending.PropertyChanged += (_, args) => changes.Add(args.PropertyName!);
        var first = pending.Begin("Name");
        var second = pending.Begin("Name");
        first.Dispose();
        Require(pending.IsPending("Name") && pending["Name"] && pending.HasPending, "A path ended while another check of it runs.");
        second.Dispose();
        Require(!pending.HasPending && pending.PendingMemberPaths.Count == 0, "The path stayed pending.");
        Require(changes.Count(change => change == nameof(RunicPendingValidation.HasPending)) == 2, $"Changes: {string.Join(", ", changes)}");

        var gate = new TaskCompletionSource<int>();
        var tracked = pending.Track("Code", () => gate.Task);
        Require(pending.IsPending("Code"), "Track did not mark the path while the check runs.");
        gate.SetResult(7);
        Require(await tracked == 7 && !pending.IsPending("Code"), "Track did not end with the check.");

        var source = new Subject<string>();
        var seen = new List<string>();
        using (source.TrackPending(pending, "Email", state => state == "checking").Subscribe(new Observer(seen.Add)))
        {
            source.OnNext("checking");
            Require(pending.IsPending("Email"), "A pending state did not mark the path.");
            source.OnNext("checking");
            source.OnNext("valid");
            Require(!pending.IsPending("Email"), "A result did not end the mark.");
            source.OnNext("checking");
        }
        Require(!pending.IsPending("Email"), "Disposing the subscription did not end the mark.");
        Require(seen.SequenceEqual(["checking", "checking", "valid", "checking"]), "The operator changed the values.");
    }

    private sealed class Subject<T> : IObservable<T>
    {
        private readonly List<IObserver<T>> _observers = [];
        public IDisposable Subscribe(IObserver<T> observer) { _observers.Add(observer); return new Unsubscribe(() => _observers.Remove(observer)); }
        public void OnNext(T value) { foreach (var observer in _observers.ToArray()) observer.OnNext(value); }
        private sealed class Unsubscribe(Action action) : IDisposable { public void Dispose() => action(); }
    }

    private sealed class Observer(Action<string> next) : IObserver<string>
    {
        public void OnNext(string value) => next(value);
        public void OnError(Exception error) => throw error;
        public void OnCompleted() { }
    }

    private static string Pending(JsonElement validation) =>
        validation.TryGetProperty("pending", out var pending) ? pending.GetRawText() : "";

    // The validation of the next frame that sets it (and satisfies until), or of a state push.
    private static async Task<JsonElement> NextValidationAsync(InMemoryBridgeTransport transport, Func<JsonElement, bool>? until = null, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        var seen = new List<string>();
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeout)
        {
            foreach (var push in transport.TakePushes("pending"))
                using (push)
                {
                    var root = push.RootElement;
                    seen.Add(root.GetRawText());
                    JsonElement? validation = root.GetProperty("kind").GetString() == "state"
                        ? root.GetProperty("state").GetProperty("validation")
                        : root.GetProperty("changes").EnumerateArray()
                            .Where(change => change.GetProperty("field").GetString() == "validation")
                            .Select(change => (JsonElement?)change.GetProperty("value")).FirstOrDefault();
                    if (validation is { } value && (until is null || until(value))) return value.Clone();
                }
            await Task.Delay(5);
        }
        throw new InvalidOperationException($"The bridge did not publish validation (line {line}): {string.Join("\n", seen)}");
    }

    [Test]
    public async Task AFieldFlushWritesThePagesDraftsBeforeADepartureIsDecidedAsync()
    {
        var model = new PendingFormViewModel();
        using var host = new RunicWindowTestHost<PendingFormViewModel>(model,
            (transport, content, vm) => new PendingFormBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "pendingForm", ViewLocator = new TestViewLocator() });
        var flushed = 0;
        host.Transport.FieldFlush = async _ =>
        {
            flushed++;
            await host.Root.SetAsync(vm => vm.UserName, "typed before leaving");
        };
        // The page connects; its hello lists field-flush/1, which the window lists too.
        using (var hello = JsonDocument.Parse(host.Transport.Request("""{"kind":"hello","protocol":4,"session":"probe","client":{"name":"probe","version":"1"},"features":["field-flush/1"]}""", "probe-connection")))
            Require(hello.RootElement.GetProperty("features").EnumerateArray().Any(feature => feature.GetString() == "field-flush/1"),
                "The window does not list field-flush/1.");
        host.Transport.CloseConnection("probe-connection");
        _ = host.Root.Snapshot();

        // A departure that .NET starts flushes first: the guard reads the draft.
        var context = host.Content.ModelContext!;
        await Runic.Navigation.RunicDraftFlush.FlushAsync(context);
        Require(flushed >= 1 && model.UserName == "typed before leaving", "The flush did not write the page's draft.");
        var flush = host.Transport.Messages.Last(message => message.Kind == "flush");
        Require(flush.ConnectionKey == InMemoryBridgeTransport.DefaultConnectionKey && flush.Json.Contains("\"id\":\"flush-", StringComparison.Ordinal),
            $"The flush push: {flush.Json}");

        // A page that never answers delays the decision by at most a second.
        host.Transport.FieldFlush = _ => new ValueTask(new TaskCompletionSource().Task);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Runic.Navigation.RunicDraftFlush.FlushAsync(context);
        Require(watch.Elapsed < TimeSpan.FromSeconds(10), $"An unanswered flush waited {watch.Elapsed}.");
    }

    // A page that never answers: the flush ends after 1 s, logs event 1140, and the departure goes on.
    [Test]
    public async Task AnUnansweredFlushEndsAfterOneSecondAndIsLoggedAsync()
    {
        var logs = new FlushLogCapture();
        using var host = new RunicWindowTestHost<PendingFormViewModel>(new PendingFormViewModel(),
            (transport, content, vm) => new PendingFormBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "pendingForm", ViewLocator = new TestViewLocator(), LoggerFactory = logs });
        host.Transport.FieldFlush = _ => new ValueTask(new TaskCompletionSource().Task);
        _ = host.Root.Snapshot();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Runic.Navigation.RunicDraftFlush.FlushAsync(host.Content.ModelContext!);
        Require(watch.Elapsed >= TimeSpan.FromMilliseconds(900) && watch.Elapsed < TimeSpan.FromSeconds(10),
            $"An unanswered flush took {watch.Elapsed}.");
        Require(logs.Events.Contains(1140), $"Event 1140 was not logged: {string.Join(", ", logs.Events)}");
    }

    // A disposed window is no longer asked, a flush in progress ends with it, and a later departure does not wait.
    [Test]
    public async Task ADisposedWindowIsNotFlushedAsync()
    {
        await using var context = new Runic.Navigation.RunicModelContext();
        var host = new RunicWindowTestHost<PendingFormViewModel>(new PendingFormViewModel(),
            (transport, content, vm) => new PendingFormBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "pendingForm", ViewLocator = new TestViewLocator(), ModelContext = context });
        var asked = 0;
        host.Transport.FieldFlush = _ => { asked++; return new ValueTask(new TaskCompletionSource().Task); };
        _ = host.Root.Snapshot();
        var inProgress = Runic.Navigation.RunicDraftFlush.FlushAsync(context).AsTask();
        await Until(() => asked == 1);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        host.Dispose();
        await inProgress;
        Require(watch.Elapsed < TimeSpan.FromMilliseconds(800), $"A flush in progress waited {watch.Elapsed} for a disposed window.");
        watch.Restart();
        await Runic.Navigation.RunicDraftFlush.FlushAsync(context);
        Require(watch.Elapsed < TimeSpan.FromMilliseconds(200) && asked == 1, $"A disposed window was flushed ({asked}, {watch.Elapsed}).");
    }

    // Two windows with their own model contexts: a departure of one neither asks nor waits for the other's pages.
    [Test]
    public async Task AFlushAsksOnlyTheWindowsOfItsModelContextAsync()
    {
        await using var contextA = new Runic.Navigation.RunicModelContext();
        await using var contextB = new Runic.Navigation.RunicModelContext();
        var modelA = new PendingFormViewModel();
        var modelB = new PendingFormViewModel();
        using var hostA = new RunicWindowTestHost<PendingFormViewModel>(modelA,
            (transport, content, vm) => new PendingFormBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "pendingForm", ViewLocator = new TestViewLocator(), ModelContext = contextA });
        using var hostB = new RunicWindowTestHost<PendingFormViewModel>(modelB,
            (transport, content, vm) => new PendingFormBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "pendingForm", ViewLocator = new TestViewLocator(), ModelContext = contextB });
        var askedB = 0;
        hostA.Transport.FieldFlush = async _ => await hostA.Root.SetAsync(vm => vm.UserName, "typed in A");
        hostB.Transport.FieldFlush = _ => { askedB++; modelB.UserName = "written in B"; return new ValueTask(new TaskCompletionSource().Task); };
        _ = hostA.Root.Snapshot();
        _ = hostB.Root.Snapshot();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Runic.Navigation.RunicDraftFlush.FlushAsync(contextA);
        Require(modelA.UserName == "typed in A" && askedB == 0 && modelB.UserName == "" && watch.Elapsed < TimeSpan.FromMilliseconds(800),
            $"Window A's departure reached window B ({askedB}, {modelB.UserName}, {watch.Elapsed}).");
    }

    // The close guard flushes its window first, also when the close is requested in a model turn (a native menu's
    // command): the guard sees what the page had not written yet.
    [Test]
    public async Task TheCloseGuardSeesTheDraftAlsoWhenRequestedInATurnAsync()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Runic.Navigation.RunicNavigationServiceCollectionExtensions.AddRunicNavigation(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<GuardedFormViewModel>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<Func<IBridgeTransport, WindowContentSession, GuardedFormViewModel, IDisposable>>(
            services, _ => (transport, content, model) => new GuardedFormBridge(transport, model, content: content));
        await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var window = await new TestWindowHost(provider).OpenWindowAsync<GuardedFormWindow>();
        var native = (TestWindow<GuardedFormViewModel>)window.Host;
        var typed = "typed before the close button";
        native.Host.Transport.FieldFlush = async _ => await native.Host.Root.SetAsync(vm => vm.Draft, typed);
        _ = native.Host.Root.Snapshot();

        Require(!await window.RequestCloseAsync() && window.SeenByGuard == "typed before the close button" && native.IsOpen,
            $"The close guard saw '{window.SeenByGuard}'.");

        typed = "typed before the menu command";
        var context = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Runic.Navigation.IRunicModelContext>(window.Services);
        var request = await context.InvokeAsync(() => window.RequestCloseAsync().AsTask());
        Require(!await request.WaitAsync(TimeSpan.FromSeconds(10)) && window.SeenByGuard == "typed before the menu command",
            $"A close requested in a turn: the guard saw '{window.SeenByGuard}'.");

        window.Allow = true;
        Require(await window.RequestCloseAsync() && !native.IsOpen, "The allowed close did not close the window.");
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(10);
        Require(condition(), "The condition did not hold within 5 s.");
    }

    private sealed class FlushLogCapture : Microsoft.Extensions.Logging.ILoggerFactory
    {
        public System.Collections.Concurrent.ConcurrentBag<int> Events { get; } = [];
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) => throw new NotSupportedException();
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(FlushLogCapture owner) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) => owner.Events.Add(eventId.Id);
        }
    }

    [Test]
    public async Task APageWithoutTheFeatureIsNotAskedAsync()
    {
        var model = new PendingFormViewModel();
        using var host = new RunicWindowTestHost<PendingFormViewModel>(model,
            (transport, content, vm) => new PendingFormBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "pendingForm", ViewLocator = new TestViewLocator() });
        _ = host.Root.Snapshot();
        await Runic.Navigation.RunicDraftFlush.FlushAsync(host.Content.ModelContext!);
        Require(!host.Transport.Messages.Any(message => message.Kind == "flush"), "A page without field-flush/1 got a flush.");
        // flush-reply without the feature is invalid.
        using var reply = JsonDocument.Parse(host.Transport.Request("""{"kind":"flush-reply","id":"flush-1"}"""));
        Require(reply.RootElement.GetProperty("error").GetProperty("reason").GetString() == "invalid-request", reply.RootElement.GetRawText());
    }

    [Test]
    public void AnEditSessionWorksWithReactiveUI()
    {
        var model = new ReactiveEditor();
        using var session = new Runic.Navigation.RunicEditSession<string>(model, () => model.Title, title => model.Title = title);
        var dirty = new List<bool>();
        using var subscription = session.WhenAnyValue(s => s.IsDirty).Subscribe(new BoolObserver(dirty.Add));
        model.Title = "Changed";
        session.Revert();
        Require(dirty.SequenceEqual([false, true, false]) && model.Title == "", $"WhenAnyValue saw {string.Join(", ", dirty)}.");
    }

    private sealed class BoolObserver(Action<bool> next) : IObserver<bool>
    {
        public void OnNext(bool value) => next(value);
        public void OnError(Exception error) => throw error;
        public void OnCompleted() { }
    }

    private sealed class ReactiveEditor : ReactiveObject
    {
        private string _title = "";
        public string Title { get => _title; set => this.RaiseAndSetIfChanged(ref _title, value); }
    }

    private static string GeneratedTypeScriptDirectory()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx")))
                return Path.Combine(directory.FullName, "tests", "dotnet", "Runic.Application.Testing.Tests", "obj", "bridge-frontend", "generated");
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
