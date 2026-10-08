// Behavioral conformance of the Runic.Navigation ReactiveUI adapter, once per flavor. It references
// only the navigation adapter package, so it also proves the adapter needs no Runic.Application.
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Runic.Navigation;
#if SYSTEM_REACTIVE
using ReactiveUI.Reactive;
using Runic.Navigation.ReactiveUI.Reactive;
using FlavorUnit = System.Reactive.Unit;
using FlavorScheduler = System.Reactive.Concurrency.IScheduler;
using FlavorSchedulerProvider = Runic.Navigation.ReactiveUI.Reactive.IRunicReactiveSchedulerProvider;
#else
using ReactiveUI;
using ReactiveUI.Primitives.Concurrency;
using Runic.Navigation.ReactiveUI;
using FlavorUnit = ReactiveUI.Primitives.RxVoid;
using FlavorScheduler = ReactiveUI.Primitives.Concurrency.ISequencer;
using FlavorSchedulerProvider = Runic.Navigation.ReactiveUI.IRunicReactiveSchedulerProvider;
#endif

await SchedulerSemanticsAsync();
await DependencyInjectionSemanticsAsync();
await SchedulerLifetimeSemanticsAsync();
await NavigationAdapterSemanticsAsync();
Console.WriteLine("NAVIGATION_REACTIVEUI_ADAPTER_OK");

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

// The provider keeps one scheduler per context, so every resolution for a context shares its
// ordering state (W240-001 section 9, M7).
static async Task SchedulerLifetimeSemanticsAsync()
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

static async Task NavigationAdapterSemanticsAsync()
{
    await using var context = new RunicModelContext();
    var scheduler = new RunicReactiveSchedulerProvider().For(context);
    await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
    var home = new NavigationPage("home");
    var region = navigator.CreateRegion<NavigationPage>(new object(), NavigationTarget.Borrow(home));

    var currents = new Recorder<NavigationPage?>();
    var entries = new Recorder<NavigationEntry<NavigationPage>?>();
    using var currentSubscription = region.WhenCurrentChanged().Subscribe(currents);
    using (region.WhenEntryChanged().Subscribe(entries))
    {
        Require(currents.Values.SequenceEqual([home]) && entries.Values.Single() == region.CurrentEntry,
            "A subscription did not receive the current content and entry first.");

        var document = new NavigationPage("document");
        await region.PushAsync(NavigationTarget.Own(document));
        // The same borrowed instance in a new entry: a new entry, not a new Current.
        await region.PushAsync(NavigationTarget.Borrow(home));
        await region.PushAsync(NavigationTarget.Borrow(home));
        Require(currents.Values.SequenceEqual([home, document, home]),
            $"WhenCurrentChanged emitted {string.Join(", ", currents.Values.Select(page => page?.Name))}.");
        Require(entries.Values.Length == 4 && entries.Values.Distinct().Count() == 4 && entries.Values[^1] == region.CurrentEntry,
            $"WhenEntryChanged emitted {entries.Values.Length} entries; expected one per entry.");
    }
    var entryCount = entries.Values.Length;

    var back = region.CreateBackCommand(scheduler);
    var outputs = new Recorder<NavigationResult<NavigationPage>>();
    using var outputSubscription = back.Subscribe(outputs);
    await WaitUntilAsync(() => ReactiveCommandExecution.CanExecute(back, UnitValue()), "The back command was disabled with history.");
    var result = await ReactiveCommandExecution.Execute(back, UnitValue(), CancellationToken.None);
    Require(result is NavigationResult<NavigationPage>.Committed { Current.Content: var resumed } && resumed == home
        && region.History.Count == 2 && entries.Values.Length == entryCount,
        $"The back command gave {result}; a disposed subscription must not emit.");
    await WaitUntilAsync(() => outputs.Values.Length == 1, "The back command did not emit its result.");
    Require(outputs.Values[0] == result, "The command output was not the navigation result.");

    // Disabled while a transition is in flight, also one the command did not start: a guard holds a Back.
    var guarding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var guarded = new NavigationPage("guarded")
    {
        Guard = async () =>
        {
            guarding.TrySetResult();
            return await release.Task;
        },
    };
    await region.PushAsync(NavigationTarget.Own(guarded));
    await WaitUntilAsync(() => ReactiveCommandExecution.CanExecute(back, UnitValue()), "The back command was disabled after a push.");
    var vetoed = region.BackAsync().AsTask();
    await guarding.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await WaitUntilAsync(() => !ReactiveCommandExecution.CanExecute(back, UnitValue()), "The back command was enabled while transitioning.");
    release.SetResult(false);
    Require(await vetoed is NavigationResult<NavigationPage>.Rejected { Reason: NavigationRejection.Guard } && region.Current == guarded,
        "A vetoed Back was not returned as a rejection.");
    await WaitUntilAsync(() => ReactiveCommandExecution.CanExecute(back, UnitValue()), "The back command stayed disabled after the veto.");
    guarded.Guard = null;

    // Disabled without history; an emptied region emits null.
    await region.ClearHistoryAsync();
    await WaitUntilAsync(() => !ReactiveCommandExecution.CanExecute(back, UnitValue()), "The back command was enabled without history.");
    await region.ClearAsync();
    Require(currents.Values[^1] is null && region.Current is null, "WhenCurrentChanged did not emit null for an empty region.");
    var late = new Recorder<NavigationPage?>();
    using (region.WhenCurrentChanged().Subscribe(late))
        Require(late.Values.SequenceEqual([(NavigationPage?)null]), "A subscription to an empty region did not receive null.");

    // An observer that throws on the initial value: the subscription is removed before the
    // exception propagates, so later changes no longer reach it.
    var throwing = new ThrowingObserver<NavigationPage?>();
    await RequireThrowsAsync<InvalidOperationException>(() => { region.WhenCurrentChanged().Subscribe(throwing); return Task.CompletedTask; },
        "The initial emission's exception did not propagate from Subscribe.");
    await region.PushAsync(NavigationTarget.Own(new NavigationPage("after")));
    Require(throwing.Calls == 1, $"A subscription whose initial emission threw still observed the region ({throwing.Calls} calls).");
}

static async Task WaitUntilAsync(Func<bool> condition, string message)
{
    for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
    Require(condition(), message);
}

static FlavorUnit UnitValue() => FlavorUnit.Default;


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


sealed class SingletonViewModel(FlavorScheduler scheduler)
{
    public FlavorScheduler Scheduler { get; } = scheduler;
}

sealed class Recorder<T> : IObserver<T>
{
    private readonly Lock _lock = new();
    private readonly List<T> _values = [];
    public T[] Values { get { lock (_lock) return [.. _values]; } }
    public void OnCompleted() { }
    public void OnError(Exception error) => throw new InvalidOperationException("The sequence failed.", error);
    public void OnNext(T value) { lock (_lock) _values.Add(value); }
}

sealed class ThrowingObserver<T> : IObserver<T>
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void OnNext(T value)
    {
        Interlocked.Increment(ref _calls);
        throw new InvalidOperationException("The observer failed.");
    }
}

sealed class NavigationPage(string name) : INavigationDepartureGuard
{
    public string Name { get; } = name;
    public Func<Task<bool>>? Guard { get; set; }
    public async ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        Guard is not { } guard || await guard();
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

sealed class CallbackObserver<T>(Action<T> next, Action<Exception> error) : IObserver<T>
{
    public void OnCompleted() { }
    public void OnError(Exception exception) => error(exception);
    public void OnNext(T value) => next(value);
}

static class ReactiveCommandExecution
{
    public static bool CanExecute<TResult>(ReactiveCommand<FlavorUnit, TResult> command, FlavorUnit input) =>
        ((ICommand)command).CanExecute(input);

    public static async Task<TResult> Execute<TResult>(ReactiveCommand<FlavorUnit, TResult> command, FlavorUnit input,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        using var subscription = command.Execute(input).Subscribe(new CallbackObserver<TResult>(
            value => completion.TrySetResult(value), error => completion.TrySetException(error)));
        return await completion.Task;
    }
}
