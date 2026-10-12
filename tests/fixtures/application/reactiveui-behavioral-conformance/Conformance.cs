using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

#if SYSTEM_REACTIVE
using System.Reactive;
using ReactiveUI.Reactive;
using ReactiveUI.Reactive.Builder;
using System.Reactive.Linq;
using Runic.Platform;
using ReactiveUI.Binding.Reactive;
using Runic.Application.Views.ReactiveUI.Reactive;
using Runic.Navigation.ReactiveUI.Reactive;
using FlavorUnit = System.Reactive.Unit;
using FlavorScheduler = System.Reactive.Concurrency.IScheduler;
using FlavorSchedulerProvider = Runic.Application.Views.ReactiveUI.Reactive.IRunicReactiveSchedulerProvider;
using DispatcherSchedulerProbe = ConformanceProbes.DispatcherScheduler;
#elif DEFAULT_FLAVOR
using ReactiveUI;
using ReactiveUI.Builder;
using ReactiveUI.Binding;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation.ReactiveUI;
using FlavorUnit = ReactiveUI.Primitives.RxVoid;
using FlavorScheduler = ReactiveUI.Primitives.Concurrency.ISequencer;
using FlavorSchedulerProvider = Runic.Application.Views.ReactiveUI.IRunicReactiveSchedulerProvider;
using DispatcherSchedulerProbe = ConformanceProbes.DispatcherSequencer;
#else
#error A ReactiveUI flavor must be selected.
#endif

internal sealed class ConformanceTests
{
    [Test, NotInParallel(Order = 1)]
    public async Task CommandSemanticsAsync()
    {
        await using var context = new RunicModelContext();
        var scheduler = new RunicReactiveSchedulerProvider().For(context);

        var exact = ReactiveCommand.Create(() => 7, scheduler);
        Require(await ReactiveCommandExecution.Execute(exact, UnitValue(), CancellationToken.None) == 7,
            "A typed command did not preserve its single result.");

        var multi = ReactiveCommand.CreateFromObservable<FlavorUnit, int>(
            _ => new SequenceObservable<int>(1, 2), scheduler);
        await RequireThrowsAsync<InvalidOperationException>(
            () => ReactiveCommandExecution.Execute(multi, UnitValue(), CancellationToken.None),
            "An exactly-one command contract accepted multiple values.");
        Require(await ReactiveCommandExecution.ExecuteLast(multi, UnitValue(), CancellationToken.None) == 2,
            "The explicit last-result command contract did not retain the final value.");

        var noResult = ReactiveCommand.CreateFromObservable<FlavorUnit, FlavorUnit>(
            _ => new EmptyObservable<FlavorUnit>(), scheduler);
        await ReactiveCommandExecution.ExecuteCompletion(noResult, UnitValue(), CancellationToken.None);

        var executions = 0;
        var preCancelled = ReactiveCommand.Create(() => ++executions, scheduler);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(
                () => ReactiveCommandExecution.Execute(preCancelled, UnitValue(), cancellation.Token),
                "A pre-cancelled command execution subscribed to application work.");
        }
        Require(executions == 0, "Pre-cancelled command work was invoked.");

        var never = new NeverObservable<int>();
        var pending = ReactiveCommand.CreateFromObservable<FlavorUnit, int>(_ => never, scheduler);
        using (var cancellation = new CancellationTokenSource())
        {
            var execution = ReactiveCommandExecution.Execute(pending, UnitValue(), cancellation.Token);
            cancellation.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(
                async () => await execution,
                "An in-flight command execution did not observe cancellation.");
        }
        Require(never.Disposals == 1, "Cancelling command execution did not dispose the observable subscription.");

        var unavailable = new AvailabilityObservable(initial: false);
        var disabled = ReactiveCommand.Create(() => 9, unavailable, scheduler);
        Require(!ReactiveCommandExecution.CanExecute(disabled, UnitValue()),
            "A genuinely unavailable command was admitted.");
    }

    [Test, NotInParallel(Order = 2)]
    public async Task InteractionSemanticsAsync()
    {
        using var transport = new InMemoryViewTransport();
        using var session = new WindowContentSession(transport);
        var model = new ConformanceInteractionModel();
        const string route = "conformance";
        const string name = "confirm";
        const string contract = "conformance.confirm.v1";
        using var fallback = model.Confirm.RegisterHandler(context => context.SetOutput(true));
        using var descriptor = ReactiveInteractionDescriptor.Create<ConformanceInteractionModel, string, bool>(
            name, contract, value => value.Confirm, static value => JsonSerializer.Serialize(value), static value => value.GetBoolean())
            .Attach(session, model, route);
        using var rootMount = session.AttachRootInteractionPresentation(route);

        using (RunicInteractionInvocation.Enter(session, route, "client", "connection"))
            Require(await model.Confirm.Handle("fallback"), "An interaction without an eligible browser did not fall back to .NET.");

        const string presentation = "scope:present";
        Require(transport.Call($"{route}Mount", new(StringValue: presentation, ClientKey: "client", ConnectionKey: "connection")) == "ok",
            "The interaction presentation did not mount.");
        var wait = transport.CallAsync(BridgeInteractionRouter.WaitRoute,
            new(StringValue: JsonSerializer.Serialize(new { route, presentationId = presentation,
                handlers = new[] { new { name, contract } } }), ClientKey: "client", ConnectionKey: "connection")).AsTask();
        await Task.Yield();
        Task<bool> answer;
        using (RunicInteractionInvocation.Enter(session, route, "client", "connection"))
            answer = model.Confirm.Handle("browser");
        using var request = JsonDocument.Parse(await wait);
        var root = request.RootElement;
        Require(root.GetProperty("kind").GetString() == "request" && root.GetProperty("input").GetString() == "browser",
            "The typed browser interaction request was not delivered.");
        var reply = JsonSerializer.Serialize(new
        {
            kind = "answered",
            requestId = root.GetProperty("requestId").GetString(),
            route,
            presentationId = presentation,
            ownerEpoch = root.GetProperty("ownerEpoch").GetInt64(),
            name,
            contract,
            output = false,
        });
        using var response = JsonDocument.Parse(transport.Call(BridgeInteractionRouter.ReplyRoute,
            new(StringValue: reply, ClientKey: "client", ConnectionKey: "connection")));
        Require(response.RootElement.GetProperty("kind").GetString() == "ok" && !await answer,
            "A typed browser reply did not complete the ReactiveUI interaction.");
    }

    // A leave confirmation asked through a bridged interaction: the browser answers it, and a superseded departure
    // aborts the browser handler's signal (W250-035).
    [Test, NotInParallel(Order = 3)]
    public async Task NavigationInteractionSemanticsAsync()
    {
        using var transport = new InMemoryViewTransport();
        using var session = new WindowContentSession(transport);
        await using var context = new RunicModelContext();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        var region = navigator.CreateRegion<object>(new object(), NavigationTarget.Borrow<object>(new object()));
        var editor = new ConformanceEditorModel();
        await region.PushAsync(NavigationTarget.Own<object>(editor));
        const string route = "editor";
        const string name = "confirmDiscard";
        const string contract = "conformance.confirmDiscard.v1";
        using var descriptor = ReactiveInteractionDescriptor.Create<ConformanceEditorModel, string, bool>(
            name, contract, value => value.ConfirmDiscard, static value => JsonSerializer.Serialize(value), static value => value.GetBoolean())
            .Attach(session, editor, route);
        using var rootMount = session.AttachRootInteractionPresentation(route);
        const string presentation = "editor:present";
        Require(transport.Call($"{route}Mount", new(StringValue: presentation, ClientKey: "client", ConnectionKey: "connection")) == "ok",
            "The editor's interaction presentation did not mount.");

        Task<string> Wait() => transport.CallAsync(BridgeInteractionRouter.WaitRoute,
            new(StringValue: JsonSerializer.Serialize(new { route, presentationId = presentation, handlers = new[] { new { name, contract } } }),
                ClientKey: "client", ConnectionKey: "connection")).AsTask();
        // The Back starts where a bridged command of the editor would, inside its invocation.
        Task<NavigationResult<object>> Close()
        {
            using (RunicInteractionInvocation.Enter(session, route, "client", "connection"))
                return region.BackAsync().AsTask();
        }

        // Superseded: the browser receives the question, then a cancellation of the same request.
        var control = transport.CallAsync(BridgeInteractionRouter.ControlWaitRoute, new(StringValue:
            JsonSerializer.Serialize(new { route, presentationId = presentation }), ClientKey: "client", ConnectionKey: "connection")).AsTask();
        var wait = Wait();
        await Task.Yield();
        var superseded = Close();
        using (var request = JsonDocument.Parse(await wait.WaitAsync(TimeSpan.FromSeconds(10))))
        {
            Require(request.RootElement.GetProperty("kind").GetString() == "request"
                && request.RootElement.GetProperty("input").GetString() == "discard draft",
                $"The leave confirmation did not reach the browser: {request.RootElement}.");
            var requestId = request.RootElement.GetProperty("requestId").GetString();
            var winner = await region.PushAsync(NavigationTarget.Own(new object())).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Require(winner is NavigationResult<object>.Committed
                && await superseded.WaitAsync(TimeSpan.FromSeconds(10)) is NavigationResult<object>.Superseded,
                "A push did not supersede the departure that asked the browser.");
            using var cancelled = JsonDocument.Parse(await control.WaitAsync(TimeSpan.FromSeconds(10)));
            Require(cancelled.RootElement.GetProperty("kind").GetString() == "cancelled"
                && cancelled.RootElement.GetProperty("requestId").GetString() == requestId,
                $"The superseded departure did not abort the browser handler's request: {cancelled.RootElement}.");
        }
        await region.BackAsync();

        // Answered: the browser's yes lets the editor depart and discards once.
        wait = Wait();
        await Task.Yield();
        var closing = Close();
        using (var request = JsonDocument.Parse(await wait.WaitAsync(TimeSpan.FromSeconds(10))))
        {
            var root = request.RootElement;
            var reply = JsonSerializer.Serialize(new
            {
                kind = "answered",
                requestId = root.GetProperty("requestId").GetString(),
                route,
                presentationId = presentation,
                ownerEpoch = root.GetProperty("ownerEpoch").GetInt64(),
                name,
                contract,
                output = true,
            });
            using var response = JsonDocument.Parse(transport.Call(BridgeInteractionRouter.ReplyRoute,
                new(StringValue: reply, ClientKey: "client", ConnectionKey: "connection")));
            Require(response.RootElement.GetProperty("kind").GetString() == "ok", "The browser's answer was rejected.");
        }
        var closed = await closing.WaitAsync(TimeSpan.FromSeconds(10));
        Require(closed is NavigationResult<object>.Committed && editor.Discards == 1,
            $"A browser yes gave {closed} with {editor.Discards} discards.");
    }

    [Test, NotInParallel(Order = 4)]
    public async Task SchedulerFailureAndShutdownSemanticsAsync()
    {
        var context = new RunicModelContext();
        var scheduler = new RunicReactiveSchedulerProvider().For(context);
        var reported = new List<Exception>();
        context.UnhandledTurnException += error => { lock (reported) reported.Add(error); };
        _ = Schedule(scheduler, 0, _ => throw new InvalidOperationException("scheduled failure"));
        var delivered = 0;
        _ = Schedule(scheduler, 0, _ => Interlocked.Increment(ref delivered));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref delivered) == 0 && DateTime.UtcNow < deadline)
            await context.InvokeAsync(() => { });
        Require(Volatile.Read(ref delivered) == 1, "A scheduled item following a failing item was not delivered.");
        lock (reported)
            Require(reported.Any(error => error is InvalidOperationException { Message: "scheduled failure" }),
                "A failing scheduled item was not reported through the model context.");

        await context.DisposeAsync();
        var afterDispose = 0;
        using (Schedule(scheduler, 0, _ => Interlocked.Increment(ref afterDispose))) { }
        using (ScheduleDelayed(scheduler, TimeSpan.FromMilliseconds(20), () => Interlocked.Increment(ref afterDispose)))
            await Task.Delay(100);
        Require(Volatile.Read(ref afterDispose) == 0, "Work scheduled after its model context was disposed ran.");
    }

    [Test, NotInParallel(Order = 5)]
    public async Task StreamOverflowStopsExecutionAsync()
    {
        await using var context = new RunicModelContext();
        var scheduler = new RunicReactiveSchedulerProvider().For(context);
        var producer = new OpenSequenceObservable<int>(1, 2, 3);
        var command = ReactiveCommand.CreateFromObservable<FlavorUnit, int>(_ => producer, scheduler);
        var stream = new BridgeOperationStream(maximumItems: 2);
        var result = await ReactiveCommandExecution.ExecuteStream(command, UnitValue(), stream,
            static value => value.ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Require(result.Kind is BridgeOperationResultKind.Stream
            && stream.Failure?.Kind is BridgeOperationDeliveryFailureKind.StreamOverflow,
            "A stream command did not report its overflow.");
        Require(producer.Disposals == 1, "An overflowing stream command kept its execution running.");

        Require(Throws<ArgumentOutOfRangeException>(() => ReactiveInteractionDescriptor.Create<ConformanceInteractionModel, string, bool>(
            "confirm", "conformance.confirm.v1", value => value.Confirm, static value => JsonSerializer.Serialize(value),
            static value => value.GetBoolean(), TimeSpan.Zero)),
            "An interaction descriptor accepted a non-positive timeout.");
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try { action(); return false; }
        catch (TException) { return true; }
    }

    [Test, NotInParallel(Order = 10)]
    public void PlatformImportSemantics()
    {
    #if SYSTEM_REACTIVE
        // With System.Reactive and Runic.Platform both imported, Unit stays System.Reactive.Unit.
        Unit reactive = Unit.Default;
        PlatformResult<PlatformUnit> platform = new PlatformResult<PlatformUnit>.Success(default);
        Require(reactive == FlavorUnit.Default && platform is PlatformResult<PlatformUnit>.Success,
            "System.Reactive and Runic.Platform types collided.");
    #endif
    }

    // Last: it replaces ReactiveUI's global default exception handler.
    // W130-029 M1: a ReactiveCommand also reports a declared failure on
    // ThrownExceptions. Without a subscriber it reaches RxState's default handler;
    // ObserveBridgeExceptions ignores it and still reports unexpected exceptions.
    [Test, NotInParallel(Order = 11)]
    public async Task BridgeExceptionSemanticsAsync()
    {
        await using var context = new RunicModelContext();
        var scheduler = new RunicReactiveSchedulerProvider().For(context);
        var handled = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        // ReactiveUIBuilder.WithExceptionHandler installs RxState's default handler
        // for the process, so this runs last. (The test reset helpers are internal.)
        RxAppBuilder.CreateReactiveUIBuilder().WithExceptionHandler(new ExceptionRecorder(handled.Enqueue)).BuildApp();
        {
            var unobserved = ReactiveCommand.CreateFromTask<FlavorUnit, int>(
                _ => Task.FromException<int>(new RunicFailureException("titleRequired")), scheduler);
            await RequireThrowsAsync<RunicFailureException>(
                () => ReactiveCommandExecution.Execute(unobserved, UnitValue(), CancellationToken.None),
                "A declared failure did not reach the Bridge caller.");
            await WaitUntilAsync(() => !handled.IsEmpty, "A declared failure without a ThrownExceptions subscriber did not reach the default handler.");
            Require(handled.Single() is RunicFailureException, "The default handler did not receive the declared failure.");
            handled.Clear();

            var command = ReactiveCommand.CreateFromTask<bool, int>(unexpected => Task.FromException<int>(unexpected
                ? new InvalidOperationException("disk full") : new RunicFailureException("titleRequired")), scheduler);
            var reported = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            using (command.ObserveBridgeExceptions(reported.Enqueue))
            {
                await RequireThrowsAsync<RunicFailureException>(
                    () => ReactiveCommandExecution.Execute(command, false, CancellationToken.None), "The declared failure was not thrown.");
                await RequireThrowsAsync<InvalidOperationException>(
                    () => ReactiveCommandExecution.Execute(command, true, CancellationToken.None), "The unexpected failure was not thrown.");
                await WaitUntilAsync(() => !reported.IsEmpty, "An unexpected exception did not reach the callback.");
                // Let any late default-handler delivery arrive before checking that none did.
                await Task.Delay(100);
            }
            Require(reported.Single() is InvalidOperationException && handled.IsEmpty,
                "ObserveBridgeExceptions reported a declared failure or let one reach the default handler.");

            var logger = new RecordingLogger();
            using (command.ObserveBridgeExceptions(logger))
            {
                await RequireThrowsAsync<RunicFailureException>(
                    () => ReactiveCommandExecution.Execute(command, false, CancellationToken.None), "The declared failure was not thrown.");
                await RequireThrowsAsync<InvalidOperationException>(
                    () => ReactiveCommandExecution.Execute(command, true, CancellationToken.None), "The unexpected failure was not thrown.");
                await WaitUntilAsync(() => !logger.Entries.IsEmpty, "An unexpected exception was not logged.");
                await Task.Delay(100);
            }
            Require(logger.Entries.Single().Source == "command", $"Event 1042 did not name the observed command: {logger.Entries.Single().Source}");
            Require(logger.Entries.Single() is { Id: 1042, Level: Microsoft.Extensions.Logging.LogLevel.Error, Exception: InvalidOperationException }
                && handled.IsEmpty, "The logger overload did not log only the unexpected exception as ReactiveCommandFailed.");

            // Cancelling a bridged operation: does ReactiveUI report it on ThrownExceptions?
            var cancellable = ReactiveCommand.CreateFromTask<FlavorUnit, int>(async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }, scheduler);
            var raw = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            var cancelledReports = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            var cancelledLog = new RecordingLogger();
            using (cancellable.ThrownExceptions.Subscribe(new ExceptionRecorder(raw.Enqueue)))
            using (cancellable.ObserveBridgeExceptions(cancelledReports.Enqueue))
            using (cancellable.ObserveBridgeExceptions(cancelledLog))
            using (var cancellation = new CancellationTokenSource())
            {
                var running = ReactiveCommandExecution.Execute(cancellable, UnitValue(), cancellation.Token);
                await Task.Delay(50);
                cancellation.Cancel();
                await RequireThrowsAsync<OperationCanceledException>(() => running, "The cancelled operation did not end cancelled.");
                await WaitUntilAsync(() => !raw.IsEmpty, "ReactiveUI did not report the cancellation on ThrownExceptions.");
                await Task.Delay(50);
            }
            // ReactiveUI reports a cancelled operation on ThrownExceptions; the
            // helper treats it as an outcome the Bridge reported, not an error.
            Require(raw.Single() is OperationCanceledException && cancelledReports.IsEmpty && cancelledLog.Entries.IsEmpty && handled.IsEmpty,
                "ObserveBridgeExceptions reported a cancelled operation as an unexpected exception.");
        }
    }

    // A command created without an output scheduler delivers its output and IsExecuting on the model context
    // of the turn that executes it; two contexts never cross, and work outside a turn uses the replaced
    // scheduler and is logged once (W250-014, design note W250-031 N1).
    [Test, NotInParallel(Order = 6)]
    public async Task AmbientMainThreadSchedulerSemanticsAsync()
    {
        var logs = new RecordingLoggerFactory();
        Require(RunicReactiveSchedulerProvider.InstallMainThreadScheduler(logs),
            "The model-context main-thread scheduler was not installed over ReactiveUI's default.");
        Require(RunicReactiveSchedulerProvider.InstallMainThreadScheduler(),
            "A second install did not report the installed scheduler.");

        await using var first = new RunicModelContext();
        await using var second = new RunicModelContext();
        var runs = await Task.WhenAll(ExecuteInTurnAsync(first), ExecuteInTurnAsync(second), ExecuteInTurnAsync(first));
        Require(runs[0].All(observed => observed == first) && runs[2].All(observed => observed == first)
            && runs[1].All(observed => observed == second) && runs.All(run => run.Count >= 3),
            "A command without an output scheduler did not deliver on the model context of its turn.");
        Require(logs.Logger.Entries.IsEmpty, "Work scheduled in a model turn was reported as outside a turn.");

        // ReactiveUI's token-taking CreateFromTask completes from the task's continuation, outside the turn. A bridged
        // execution carries its turn's context there, so its result and IsExecuting still arrive on that context.
        var bridged = await Task.WhenAll(ExecuteBridgedWithTokenAsync(first), ExecuteBridgedWithTokenAsync(second));
        Require(bridged[0].All(observed => observed == first) && bridged[1].All(observed => observed == second)
            && bridged.All(run => run.Count >= 3),
            "A bridged token-taking command did not deliver on the model context of its turn.");
        Require(logs.Logger.Entries.IsEmpty, "A bridged token-taking command was reported as outside a turn.");

        // Outside a turn: the replaced scheduler runs the work, and the warning is logged once.
        for (var attempt = 0; attempt != 2; attempt++)
        {
            var command = ReactiveCommand.Create(() => 1);
            var outside = new TaskCompletionSource<RunicModelContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (command.Subscribe(new TurnObserver<int>(_ => outside.TrySetResult(RunicModelContext.Current))))
            using (await Task.Run(() => command.Execute().Subscribe(new TurnObserver<int>(_ => { }))))
                Require(await outside.Task.WaitAsync(TimeSpan.FromSeconds(5)) is null,
                    "Work scheduled outside a model turn ran on a model context.");
        }
        Require(logs.Logger.Entries.Count(entry => entry.Id == 1043) == 1,
            "Work scheduled outside a model turn was not logged exactly once.");
    }

    // Creates a command without a scheduler, as a ViewModel does, and executes it from a turn of the context.
    // The body awaits, so its result is produced in the turn its continuation resumes in.
    private static async Task<List<RunicModelContext?>> ExecuteInTurnAsync(RunicModelContext context)
    {
        var observed = new List<RunicModelContext?>();
        var command = ReactiveCommand.CreateFromTask(async () =>
        {
            await Task.Delay(10);
            return 1;
        });
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = command.Subscribe(new TurnObserver<int>(_ => { lock (observed) observed.Add(RunicModelContext.Current); }));
        using var executing = command.IsExecuting.Skip(1)
            .Subscribe(new TurnObserver<bool>(_ => { lock (observed) observed.Add(RunicModelContext.Current); }));
        await context.InvokeAsync(() => { _ = command.Execute().Subscribe(new TurnObserver<int>(_ => { }, () => done.TrySetResult())); });
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => { lock (observed) return observed.Count >= 3; }, "The command did not report its execution.");
        await context.InvokeAsync(() => { });
        lock (observed) return [.. observed];
    }


    // Executes a token-taking command the way a generated bridge does, from a turn of the context.
    private static async Task<List<RunicModelContext?>> ExecuteBridgedWithTokenAsync(RunicModelContext context)
    {
        var observed = new List<RunicModelContext?>();
        var command = ReactiveCommand.CreateFromTask(async (CancellationToken cancellationToken) =>
        {
            await Task.Delay(10, cancellationToken);
            return 1;
        });
        using var output = command.Subscribe(new TurnObserver<int>(_ => { lock (observed) observed.Add(RunicModelContext.Current); }));
        using var executing = command.IsExecuting.Skip(1)
            .Subscribe(new TurnObserver<bool>(_ => { lock (observed) observed.Add(RunicModelContext.Current); }));
        Task<int>? execution = null;
        await context.InvokeAsync(() => execution = ReactiveCommandExecution.Execute(command, default(FlavorUnit), CancellationToken.None));
        Require(await execution!.WaitAsync(TimeSpan.FromSeconds(5)) == 1, "The bridged token-taking command returned the wrong result.");
        await WaitUntilAsync(() => { lock (observed) return observed.Count >= 3; }, "The command did not report its execution.");
        await context.InvokeAsync(() => { });
        lock (observed) return [.. observed];
    }
    [Test, NotInParallel(Order = 7)]
    public async Task SchedulerSemanticsAsync()
    {
        await using var context = new RunicModelContext();
        var scheduler = new RunicReactiveSchedulerProvider().For(context);
        var fifo = new List<int>();
        for (var value = 0; value != 16; value++)
            _ = Schedule(scheduler, value, current => fifo.Add(current));
        await context.InvokeAsync(() => { });
        Require(fifo.SequenceEqual(Enumerable.Range(0, 16)), "The adapter scheduler did not preserve FIFO delivery.");

        var ambient = new AsyncLocal<string?> { Value = "captured" };
        var observedAmbient = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Schedule(scheduler, 0, _ => observedAmbient.TrySetResult(ambient.Value));
        ambient.Value = null;
        Require(await observedAmbient.Task.WaitAsync(TimeSpan.FromSeconds(5)) == "captured",
            "The adapter scheduler lost its scheduling call's execution context.");

        var cancelled = 0;
        using (ScheduleDelayed(scheduler, TimeSpan.FromMilliseconds(150), () => Interlocked.Increment(ref cancelled)))
        {
        }
        await Task.Delay(250);
        Require(Volatile.Read(ref cancelled) == 0, "Disposing scheduled work did not cancel it.");
    }

    [Test, NotInParallel(Order = 8)]
    public async Task DependencyInjectionSemanticsAsync()
    {
        var services = new ServiceCollection();
        services.AddRunicReactiveModelContext();
        await using var provider = services.BuildServiceProvider();

        IRunicModelContext firstContext;
        FlavorScheduler firstScheduler;
        await using (var first = provider.CreateAsyncScope())
        {
            firstContext = first.ServiceProvider.GetRequiredService<IRunicModelContext>();
            firstScheduler = first.ServiceProvider.GetRequiredService<FlavorScheduler>();
            Require(ReferenceEquals(firstContext, first.ServiceProvider.GetRequiredService<IRunicModelContext>()),
                "A scope did not retain its model context.");
            Require(ReferenceEquals(firstScheduler, first.ServiceProvider.GetRequiredService<FlavorScheduler>()),
                "A scope did not retain its model scheduler.");
        }
        await RequireThrowsAsync<ObjectDisposedException>(() => firstContext.InvokeAsync(() => { }).AsTask(),
            "Disposing an async service scope did not dispose its model context.");

        await using (var second = provider.CreateAsyncScope())
        {
            var secondContext = second.ServiceProvider.GetRequiredService<IRunicModelContext>();
            var secondScheduler = second.ServiceProvider.GetRequiredService<FlavorScheduler>();
            Require(!ReferenceEquals(firstContext, secondContext) && !ReferenceEquals(firstScheduler, secondScheduler),
                "Distinct service scopes shared a model context or scheduler.");
        }

        await using var customContext = new RunicModelContext();
        var customProvider = new RecordingProvider();
        var customServices = new ServiceCollection();
        customServices.AddScoped<IRunicModelContext>(_ => customContext);
        customServices.AddSingleton<FlavorSchedulerProvider>(customProvider);
        customServices.AddRunicReactiveModelContext();
        await using var customContainer = customServices.BuildServiceProvider();
        await using var customScope = customContainer.CreateAsyncScope();
        Require(ReferenceEquals(customContext, customScope.ServiceProvider.GetRequiredService<IRunicModelContext>()),
            "AddRunicReactiveModelContext replaced an application model context registration.");
        _ = customScope.ServiceProvider.GetRequiredService<FlavorScheduler>();
        Require(customProvider.Calls == 1, "AddRunicReactiveModelContext replaced an application scheduler provider registration.");
    }

    // The provider keeps one scheduler per context, so every resolution for a context shares its
    // ordering state (W240-001 section 9, M7).
    [Test, NotInParallel(Order = 9)]
    public async Task SchedulerLifetimeSemanticsAsync()
    {
        await using var context = new RunicModelContext();
        var provider = new RunicReactiveSchedulerProvider();
        Require(ReferenceEquals(provider.For(context), provider.For(context)),
            "The provider returned distinct schedulers for one context.");
        await using var other = new RunicModelContext();
        Require(!ReferenceEquals(provider.For(context), provider.For(other)),
            "The provider shared a scheduler between contexts.");

        // A transient registration returns the cached scheduler, also for a singleton context.
        var services = new ServiceCollection();
        services.AddSingleton<IRunicModelContext>(context);
        services.AddRunicReactiveModelContext();
        services.AddSingleton<SingletonViewModel>();
        await using var container = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var first = container.GetRequiredService<FlavorScheduler>();
        Require(ReferenceEquals(first, container.GetRequiredService<FlavorScheduler>()),
            "Two resolutions for one context returned distinct schedulers.");
        Require(ReferenceEquals(first, container.GetRequiredService<SingletonViewModel>().Scheduler),
            "A singleton ViewModel did not receive the context's scheduler.");

        // A scoped context gets its own scheduler per scope, and one within the scope.
        var scoped = new ServiceCollection();
        scoped.AddRunicReactiveModelContext();
        await using var scopedContainer = scoped.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        FlavorScheduler inFirst;
        await using (var one = scopedContainer.CreateAsyncScope())
        {
            inFirst = one.ServiceProvider.GetRequiredService<FlavorScheduler>();
            Require(ReferenceEquals(inFirst, one.ServiceProvider.GetRequiredService<FlavorScheduler>()),
                "A scope resolved distinct schedulers for its context.");
        }
        await using (var two = scopedContainer.CreateAsyncScope())
            Require(!ReferenceEquals(inFirst, two.ServiceProvider.GetRequiredService<FlavorScheduler>()),
                "Two scopes shared a scheduler.");
    }

    // W260-015: a Runic Desktop, CS-WebUI or test-host window that opens while AddRunicReactiveModelContext() kept
    // another main-thread scheduler logs event 1090 once per scheduler type; a UI dispatcher's scheduler is silent.
    // It runs first, before AmbientMainThreadSchedulerSemanticsAsync installs the model-context scheduler.
    [Test, NotInParallel(Order = 0)]
    public async Task KeptSchedulerIsReportedWhenAWindowOpensAsync()
    {
        var previous = RxSchedulers.MainThreadScheduler;
        try
        {
            var logs = new RecordingLoggerFactory();
            RxSchedulers.MainThreadScheduler = new DispatcherSchedulerProbe(previous);
            await using (var dispatcher = WindowServices(logs))
            {
                NotifyWindowOpening(dispatcher);
                Require(logs.Logger.Entries.IsEmpty, "A UI dispatcher's scheduler was reported when a window opened.");
            }

            var kept = new ApplicationScheduler(previous);
            RxSchedulers.MainThreadScheduler = kept;
            await using var provider = WindowServices(logs);
            Require(ReferenceEquals(RxSchedulers.MainThreadScheduler, kept),
                "AddRunicReactiveModelContext replaced a scheduler the application set.");
            NotifyWindowOpening(provider);
            NotifyWindowOpening(provider);
            Require(logs.Logger.Entries.SingleOrDefault() is
                { Id: 1090, Level: Microsoft.Extensions.Logging.LogLevel.Information } entry
                && entry.Source == typeof(ApplicationScheduler).FullName,
                $"A kept scheduler was not logged once as event 1090: {string.Join(", ", logs.Logger.Entries)}");
        }
        finally
        {
            RxSchedulers.MainThreadScheduler = previous;
        }
    }

    // W260-015, maintainer decision 1: work scheduled outside a model turn is logged once per ViewModel type, so a
    // second offending ViewModel is not hidden behind the first. The window passes the application's logger factory.
    [Test, NotInParallel(Order = 12)]
    public async Task OutsideTurnWorkIsLoggedPerViewModelTypeAsync()
    {
        var logs = new RecordingLoggerFactory();
        await using var provider = WindowServices(logs);
        NotifyWindowOpening(provider);

        var first = new OffTurnFirstViewModel();
        var second = new OffTurnSecondViewModel();
        await first.PushOutsideTurnAsync();
        await first.PushOutsideTurnAsync();
        await second.PushOutsideTurnAsync();
        var outside = logs.Logger.Entries.Where(static entry => entry.Id == 1043).ToList();
        Require(outside.Count(entry => entry.Source == typeof(OffTurnFirstViewModel).FullName) == 1
            && outside.Count(entry => entry.Source == typeof(OffTurnSecondViewModel).FullName) == 1
            && outside.All(static entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning),
            $"Work outside a model turn was not logged once per ViewModel type: {string.Join(", ", outside)}");
    }

    // W260-015: ReactiveUI's builder after the install. Core services keep the model-context scheduler; platform
    // services replace it with ReactiveUI's default outside a test runner, which a window reports as event 1091 once;
    // WithRunicModelContext() keeps it. Runs after BridgeExceptionSemanticsAsync, which builds ReactiveUI first.
    [Test, NotInParallel(Order = 13)]
    public async Task BuilderAfterInstallSemanticsAsync()
    {
        Require(RunicReactiveSchedulerProvider.InstallMainThreadScheduler(), "The model-context scheduler is not installed.");
        var installed = RxSchedulers.MainThreadScheduler;
        var logs = new RecordingLoggerFactory();
        await using var provider = WindowServices(logs);
        Splat.ModeDetector.OverrideModeDetector(new ModeDetectorProbe(inUnitTestRunner: false));
        try
        {
            Splat.Builder.AppBuilder.ResetBuilderStateForTests();
            RxAppBuilder.CreateReactiveUIBuilder().BuildApp();
            Require(ReferenceEquals(RxSchedulers.MainThreadScheduler, installed),
                "BuildApp with ReactiveUI's core services replaced the model-context scheduler.");

            Splat.Builder.AppBuilder.ResetBuilderStateForTests();
            RxAppBuilder.CreateReactiveUIBuilder().WithPlatformServices().BuildApp();
            var replacement = RxSchedulers.MainThreadScheduler;
            Require(!ReferenceEquals(replacement, installed),
                "BuildApp with ReactiveUI's platform services kept the model-context scheduler.");
            NotifyWindowOpening(provider);
            NotifyWindowOpening(provider);
            Require(logs.Logger.Entries.SingleOrDefault() is
                { Id: 1091, Level: Microsoft.Extensions.Logging.LogLevel.Warning } entry
                && entry.Source == replacement.GetType().FullName,
                $"A scheduler that replaced the install was not logged once as event 1091: {string.Join(", ", logs.Logger.Entries)}");

            RxSchedulers.MainThreadScheduler = new DispatcherSchedulerProbe(replacement);
            NotifyWindowOpening(provider);
            Require(logs.Logger.Entries.Count == 1, "A UI dispatcher's scheduler that replaced the install was reported.");

            Splat.Builder.AppBuilder.ResetBuilderStateForTests();
            RxAppBuilder.CreateReactiveUIBuilder().WithPlatformServices().WithRunicModelContext().BuildApp();
            Require(ReferenceEquals(RxSchedulers.MainThreadScheduler, installed),
                "BuildApp with WithRunicModelContext() did not keep the model-context scheduler.");
            NotifyWindowOpening(provider);
            Require(logs.Logger.Entries.Count == 1, "The model-context scheduler was reported when a window opened.");

            await using var context = new RunicModelContext();
            var run = await ExecuteInTurnAsync(context);
            Require(run.Count >= 3 && run.All(observed => observed == context),
                "A command created after WithRunicModelContext() did not deliver on the model context of its turn.");
        }
        finally
        {
            Splat.ModeDetector.OverrideModeDetector(new ModeDetectorProbe(inUnitTestRunner: true));
            Splat.Builder.AppBuilder.ResetBuilderStateForTests();
            RxSchedulers.MainThreadScheduler = installed;
        }
    }

    private static ServiceProvider WindowServices(RecordingLoggerFactory logs)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(logs);
        services.AddRunicReactiveModelContext();
        services.AddRunicReactiveModelContext();
        return services.BuildServiceProvider();
    }

    // What the Desktop and CS-WebUI hosts and RunicWindowTestHost.Create run before they resolve a window's ViewModel.
    private static void NotifyWindowOpening(IServiceProvider services)
    {
        var observers = services.GetServices<IRunicWindowOpeningObserver>().ToList();
        Require(observers.Count == 1, "AddRunicReactiveModelContext did not register one window-opening observer.");
        observers[0].OnWindowOpening(services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>());
    }


    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
        Require(condition(), message);
    }

    private static FlavorUnit UnitValue()
    {
    #if SYSTEM_REACTIVE
        return FlavorUnit.Default;
    #else
        return FlavorUnit.Default;
    #endif
    }

    internal static IDisposable Schedule<T>(FlavorScheduler scheduler, T value, Action<T> action)
    {
    #if SYSTEM_REACTIVE
        return scheduler.Schedule((Value: value, Action: action), static (_, state) =>
        {
            state.Action(state.Value);
            return System.Reactive.Disposables.Disposable.Empty;
        });
    #else
        return scheduler.Schedule((Value: value, Action: action), static state => state.Action(state.Value));
    #endif
    }

    private static IDisposable ScheduleDelayed(FlavorScheduler scheduler, TimeSpan dueTime, Action action)
    {
    #if SYSTEM_REACTIVE
        return scheduler.Schedule(action, dueTime, static (_, callback) =>
        {
            callback();
            return System.Reactive.Disposables.Disposable.Empty;
        });
    #else
        return scheduler.Schedule(dueTime, action);
    #endif
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> operation, string message) where TException : Exception
    {
        try { await operation(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

sealed class SequenceObservable<T>(params T[] values) : IObservable<T>
{
    public IDisposable Subscribe(IObserver<T> observer)
    {
        foreach (var value in values) observer.OnNext(value);
        observer.OnCompleted();
        return EmptyDisposable.Instance;
    }
}

sealed class EmptyObservable<T> : IObservable<T>
{
    public IDisposable Subscribe(IObserver<T> observer)
    {
        observer.OnCompleted();
        return EmptyDisposable.Instance;
    }
}

// Emits its values and stays open until its subscriber disposes it.
sealed class OpenSequenceObservable<T>(params T[] values) : IObservable<T>
{
    public int Disposals { get; private set; }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        foreach (var value in values) observer.OnNext(value);
        return new CallbackDisposable(() => Disposals++);
    }
}

sealed class NeverObservable<T> : IObservable<T>
{
    public int Disposals { get; private set; }

    public IDisposable Subscribe(IObserver<T> observer) => new CallbackDisposable(() => Disposals++);
}

sealed class AvailabilityObservable(bool initial) : IObservable<bool>
{
    public IDisposable Subscribe(IObserver<bool> observer)
    {
        observer.OnNext(initial);
        return EmptyDisposable.Instance;
    }
}

sealed class CallbackDisposable(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

sealed class ExceptionRecorder(Action<Exception> record) : IObserver<Exception>
{
    public void OnCompleted() { }
    public void OnError(Exception error) => record(error);
    public void OnNext(Exception value) => record(value);
}

sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
{
    public System.Collections.Concurrent.ConcurrentQueue<(int Id, Microsoft.Extensions.Logging.LogLevel Level, Exception? Exception, string? Source)> Entries { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Enqueue((eventId.Id, logLevel, exception,
            (state as IEnumerable<KeyValuePair<string, object?>>)?.FirstOrDefault(pair => pair.Key is "Source" or "Scheduler").Value as string));
}

sealed class EmptyDisposable : IDisposable
{
    public static EmptyDisposable Instance { get; } = new();
    public void Dispose() { }
}

sealed class ConformanceInteractionModel
{
    public Interaction<string, bool> Confirm { get; } = new();
}

sealed class ConformanceEditorModel : INavigationDepartureGuard
{
    private readonly LeaveConfirmation _leave;

    public ConformanceEditorModel() =>
        _leave = ReactiveLeaveConfirmation.WithInteraction(ConfirmDiscard, () => "discard draft", () => true, () => Discards++);

    public Interaction<string, bool> ConfirmDiscard { get; } = new();
    public int Discards { get; private set; }
    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        _leave.CanDepartAsync(departure, cancellationToken);
}

sealed class SingletonViewModel(FlavorScheduler scheduler)
{
    public FlavorScheduler Scheduler { get; } = scheduler;
}


sealed class RecordingProvider : FlavorSchedulerProvider
{
    private readonly RunicReactiveSchedulerProvider _inner = new();
    public int Calls { get; private set; }

    public FlavorScheduler For(IRunicModelContext context)
    {
        Calls++;
        return _inner.For(context);
    }
}


sealed class TurnObserver<T>(Action<T> next, Action? completed = null) : IObserver<T>
{
    public void OnCompleted() => completed?.Invoke();
    public void OnError(Exception error) => throw new InvalidOperationException("The sequence failed.", error);
    public void OnNext(T value) => next(value);
}

sealed class RecordingLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
{
    public RecordingLogger Logger { get; } = new();
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => Logger;
    public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) => throw new NotSupportedException();
    public void Dispose() { }
}

// A scheduler an application sets before AddRunicReactiveModelContext(); it delegates to ReactiveUI's default.
sealed class ApplicationScheduler(FlavorScheduler inner) : DelegatingScheduler(inner);

sealed class ModeDetectorProbe(bool inUnitTestRunner) : Splat.IModeDetector
{
    public bool? InUnitTestRunner() => inUnitTestRunner;
}

// Background values observed on ReactiveUI's main-thread scheduler, as after Task.Run or ConfigureAwait(false).
// Each ViewModel declares its own method: the scheduler names the type that declares the scheduling code.
sealed class OffTurnFirstViewModel : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public Task PushOutsideTurnAsync() => Task.Run(() =>
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ConformanceTests.Schedule(RxSchedulers.MainThreadScheduler, 0, _ => done.TrySetResult());
        return done.Task;
    });
}

sealed class OffTurnSecondViewModel : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

    public Task PushOutsideTurnAsync() => Task.Run(() =>
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ConformanceTests.Schedule(RxSchedulers.MainThreadScheduler, 0, _ => done.TrySetResult());
        return done.Task;
    });
}

#if SYSTEM_REACTIVE
class DelegatingScheduler(FlavorScheduler inner) : FlavorScheduler
{
    public DateTimeOffset Now => inner.Now;
    public IDisposable Schedule<TState>(TState state, Func<FlavorScheduler, TState, IDisposable> action) => inner.Schedule(state, action);
    public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<FlavorScheduler, TState, IDisposable> action) =>
        inner.Schedule(state, dueTime, action);
    public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<FlavorScheduler, TState, IDisposable> action) =>
        inner.Schedule(state, dueTime, action);
}
#else
class DelegatingScheduler(FlavorScheduler inner) : FlavorScheduler
{
    public DateTimeOffset Now => inner.Now;
    public long Timestamp => inner.Timestamp;
    public void Schedule(ReactiveUI.Primitives.Concurrency.IWorkItem item) => inner.Schedule(item);
    public void Schedule(ReactiveUI.Primitives.Concurrency.IWorkItem item, long dueTimestamp) => inner.Schedule(item, dueTimestamp);
}
#endif

// Named as a UI platform's dispatcher scheduler: WPF's DispatcherSequencer, or System.Reactive's DispatcherScheduler.
namespace ConformanceProbes
{
#if SYSTEM_REACTIVE
    sealed class DispatcherScheduler(FlavorScheduler inner) : DelegatingScheduler(inner);
#else
    sealed class DispatcherSequencer(FlavorScheduler inner) : DelegatingScheduler(inner);
#endif
}
