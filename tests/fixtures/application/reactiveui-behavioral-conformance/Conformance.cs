using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;

#if SYSTEM_REACTIVE
using ReactiveUI.Reactive;
using ReactiveUI.Binding.Reactive;
using Runic.Application.Views.ReactiveUI.Reactive;
using FlavorUnit = System.Reactive.Unit;
using FlavorScheduler = System.Reactive.Concurrency.IScheduler;
using FlavorSchedulerProvider = Runic.Application.Views.ReactiveUI.Reactive.IRunicReactiveSchedulerProvider;
#elif DEFAULT_FLAVOR
using ReactiveUI;
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

sealed class EmptyDisposable : IDisposable
{
    public static EmptyDisposable Instance { get; } = new();
    public void Dispose() { }
}

sealed class ConformanceInteractionModel
{
    public Interaction<string, bool> Confirm { get; } = new();
}
