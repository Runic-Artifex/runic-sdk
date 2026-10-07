using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;

#if SYSTEM_REACTIVE
using System.Reactive;
using ReactiveUI.Reactive;
using ReactiveUI.Reactive.Builder;
using Runic.Platform;
using ReactiveUI.Binding.Reactive;
using Runic.Application.Views.ReactiveUI.Reactive;
using FlavorUnit = System.Reactive.Unit;
using FlavorScheduler = System.Reactive.Concurrency.IScheduler;
using FlavorSchedulerProvider = Runic.Application.Views.ReactiveUI.Reactive.IRunicReactiveSchedulerProvider;
#elif DEFAULT_FLAVOR
using ReactiveUI;
using ReactiveUI.Builder;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Concurrency;
using Runic.Application.Views.ReactiveUI;
using FlavorUnit = ReactiveUI.Primitives.RxVoid;
using FlavorScheduler = ReactiveUI.Primitives.Concurrency.ISequencer;
using FlavorSchedulerProvider = Runic.Application.Views.ReactiveUI.IRunicReactiveSchedulerProvider;
#else
#error A ReactiveUI flavor must be selected.
#endif

await CommandSemanticsAsync();
await SchedulerSemanticsAsync();
await DependencyInjectionSemanticsAsync();
await InteractionSemanticsAsync();
await SchedulerFailureAndShutdownSemanticsAsync();
await StreamOverflowStopsExecutionAsync();
PlatformImportSemantics();
// Last: it replaces ReactiveUI's global default exception handler.
await BridgeExceptionSemanticsAsync();
Console.WriteLine("REACTIVEUI_BEHAVIORAL_CONFORMANCE_OK");

static async Task CommandSemanticsAsync()
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

static async Task SchedulerSemanticsAsync()
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

static async Task DependencyInjectionSemanticsAsync()
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

static async Task InteractionSemanticsAsync()
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

static async Task SchedulerFailureAndShutdownSemanticsAsync()
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

static async Task StreamOverflowStopsExecutionAsync()
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

static bool Throws<TException>(Action action) where TException : Exception
{
    try { action(); return false; }
    catch (TException) { return true; }
}

static void PlatformImportSemantics()
{
#if SYSTEM_REACTIVE
    // With System.Reactive and Runic.Platform both imported, Unit stays System.Reactive.Unit.
    Unit reactive = Unit.Default;
    PlatformResult<PlatformUnit> platform = new PlatformResult<PlatformUnit>.Success(default);
    Require(reactive == FlavorUnit.Default && platform is PlatformResult<PlatformUnit>.Success,
        "System.Reactive and Runic.Platform types collided.");
#endif
}

// W130-029 M1: a ReactiveCommand also reports a declared failure on
// ThrownExceptions. Without a subscriber it reaches RxState's default handler;
// ObserveBridgeExceptions ignores it and still reports unexpected exceptions.
static async Task BridgeExceptionSemanticsAsync()
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
        }
        Require(logger.Entries.Single() is { Id: 1042, Level: Microsoft.Extensions.Logging.LogLevel.Error, Exception: InvalidOperationException }
            && handled.IsEmpty, "The logger overload did not log only the unexpected exception as ReactiveCommandFailed.");
    }
}

static async Task WaitUntilAsync(Func<bool> condition, string message)
{
    for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
    Require(condition(), message);
}

static FlavorUnit UnitValue()
{
#if SYSTEM_REACTIVE
    return FlavorUnit.Default;
#else
    return FlavorUnit.Default;
#endif
}

static IDisposable Schedule<T>(FlavorScheduler scheduler, T value, Action<T> action)
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

static IDisposable ScheduleDelayed(FlavorScheduler scheduler, TimeSpan dueTime, Action action)
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

static async Task RequireThrowsAsync<TException>(Func<Task> operation, string message) where TException : Exception
{
    try { await operation(); }
    catch (TException) { return; }
    throw new InvalidOperationException(message);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
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
    public System.Collections.Concurrent.ConcurrentQueue<(int Id, Microsoft.Extensions.Logging.LogLevel Level, Exception? Exception)> Entries { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) => Entries.Enqueue((eventId.Id, logLevel, exception));
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
