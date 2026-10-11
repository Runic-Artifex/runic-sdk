// Behavioral conformance of the Runic.Navigation ReactiveUI adapter, once per flavor. It references
// only the navigation adapter package, so it also proves the adapter needs no Runic.Application.
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Runic.Navigation;
#if SYSTEM_REACTIVE
using ReactiveUI.Reactive;
using System.Reactive.Linq;
using Runic.Navigation.ReactiveUI.Reactive;
using FlavorUnit = System.Reactive.Unit;
using FlavorScheduler = System.Reactive.Concurrency.IScheduler;
#else
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using Runic.Navigation.ReactiveUI;
using FlavorUnit = ReactiveUI.Primitives.RxVoid;
using FlavorScheduler = ReactiveUI.Primitives.Concurrency.ISequencer;
#endif

await NavigationAdapterSemanticsAsync();
await NavigationStateCompositionAsync();
await DuplicateBackCommandKeepsOneConfirmationAsync();
Console.WriteLine("NAVIGATION_REACTIVEUI_ADAPTER_OK");

static async Task NavigationAdapterSemanticsAsync()
{
    await using var context = new RunicModelContext();
    var scheduler = ContextScheduler.For(context);
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
    var outputs = new Recorder<NavigationOutcome>();
    using var outputSubscription = back.Subscribe(outputs);
    await WaitUntilAsync(() => ReactiveCommandExecution.CanExecute(back, UnitValue()), "The back command was disabled with history.");
    var result = await ReactiveCommandExecution.Execute(back, UnitValue(), CancellationToken.None);
    Require(result == new NavigationOutcome(NavigationOutcomeKind.Committed) && region.Current == home
        && region.History.Count == 2 && entries.Values.Length == entryCount,
        $"The back command gave {result}; a disposed subscription must not emit.");
    await WaitUntilAsync(() => outputs.Values.Length == 1, "The back command did not emit its result.");
    Require(outputs.Values[0] == result, "The command output was not the navigation outcome.");

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
    // The region raised IsTransitioning before the guard ran, and CanExecute follows it without a scheduler hop.
    Require(region.IsTransitioning && !ReactiveCommandExecution.CanExecute(back, UnitValue()),
        "The back command was enabled while transitioning.");
    release.SetResult(false);
    Require(await vetoed is NavigationResult<NavigationPage>.Rejected { Reason: NavigationRejection.Guard } && region.Current == guarded,
        "A vetoed Back was not returned as a rejection.");
    await WaitUntilAsync(() => ReactiveCommandExecution.CanExecute(back, UnitValue()), "The back command stayed disabled after the veto.");

    guarded.Guard = null;

    // The command reports a vetoed Back as a rejected outcome, which a View can receive.
    var vetoing = new NavigationPage("vetoing") { Guard = () => Task.FromResult(false) };
    await region.PushAsync(NavigationTarget.Own(vetoing));
    using (var defaultScheduled = region.CreateBackCommand())
    {
        await WaitUntilAsync(() => ReactiveCommandExecution.CanExecute(defaultScheduled, UnitValue()),
            "A back command without a scheduler was disabled with history.");
        var rejected = await ReactiveCommandExecution.Execute(defaultScheduled, UnitValue(), CancellationToken.None);
        Require(rejected == new NavigationOutcome(NavigationOutcomeKind.Rejected, NavigationRejection.Guard) && region.Current == vetoing,
            $"A vetoed back command gave {rejected}.");
    }
    vetoing.Guard = null;
    await region.BackAsync();

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

static async Task NavigationStateCompositionAsync()
{
    await using var context = new RunicModelContext();
    await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
    var region = navigator.CreateRegion<NavigationPage>(new object(), NavigationTarget.Borrow(new NavigationPage("home")));
    var history = new Recorder<bool>();
    var busy = new Recorder<bool>();
    using var historySubscription = region.WhenCanGoBackChanged().Subscribe(history);
    var busySubscription = region.WhenIsTransitioningChanged().Subscribe(busy);
    Require(history.Values.SequenceEqual([false]) && busy.Values.SequenceEqual([false]),
        "State subscriptions did not emit their initial values synchronously.");

    // A native ReactiveUI command composes the region's state with application state.
    var scheduler = ContextScheduler.For(context);
    using var push = ReactiveCommand.CreateFromTask(
        token => region.PushAsync(NavigationTarget.Own(new NavigationPage("document")), cancellationToken: token).AsTask(),
        region.WhenIsTransitioningChanged().Select(transitioning => !transitioning), scheduler);
    Require(await ReactiveCommandExecution.Execute(push, UnitValue(), CancellationToken.None)
        is NavigationResult<NavigationPage>.Committed, "A native reactive Push did not preserve the core outcome.");
    Require(history.Values.SequenceEqual([false, true]), "Back availability did not become true after Push.");

    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    region.Current!.Guard = () => { entered.TrySetResult(); return release.Task; };
    var back = region.BackAsync().AsTask();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await WaitUntilAsync(() => busy.Values[^1] && !ReactiveCommandExecution.CanExecute(push, UnitValue()),
        "The state pipeline did not disable Push while another caller's guard was pending.");
    Require(history.Values.SequenceEqual([false, true]), "Transitioning incorrectly changed Back history availability.");
    release.SetResult(false);
    Require(await back is NavigationResult<NavigationPage>.Rejected { Reason: NavigationRejection.Guard },
        "A guard veto did not preserve its core rejection.");
    await WaitUntilAsync(() => !busy.Values[^1] && ReactiveCommandExecution.CanExecute(push, UnitValue()),
        "The pipeline stayed busy after the guard veto.");

    // Disposed and failed initial subscriptions must detach from subsequent region changes.
    busySubscription.Dispose();
    var busyCount = busy.Values.Length;
    var throwing = new ThrowingObserver<bool>();
    await RequireThrowsAsync<InvalidOperationException>(() =>
        { region.WhenIsTransitioningChanged().Subscribe(throwing); return Task.CompletedTask; },
        "A state observer's initial exception did not propagate.");
    await region.ClearHistoryAsync();
    Require(history.Values.SequenceEqual([false, true, false]), "Clearing history did not emit false.");
    Require(busy.Values.Length == busyCount && throwing.Calls == 1,
        "A disposed or failed initial state subscription continued observing.");
}

static async Task WaitUntilAsync(Func<bool> condition, string message)
{
    for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
    Require(condition(), message);
}

// Hold output delivery as a dispatcher would while two already-dispatched command executions start.
// CanExecute is advisory for programmatic Execute; both actual executions carry ReactiveUI's tokens.
static async Task DuplicateBackCommandKeepsOneConfirmationAsync()
{
    await using var context = new RunicModelContext();
    await using var outputContext = new RunicModelContext();
    using var logs = new BackJoinLog();
    await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context, LoggerFactory = logs });
    var home = new NavigationPage("home");
    var main = navigator.CreateRegion<NavigationPage>(new object(), NavigationTarget.Borrow(home));
    var dialogs = navigator.CreateRegion<ConfirmPage>(new object());
    var document = new NavigationPage("document");
    var prompts = 0;
    var discards = 0;
    document.DepartureGuard = LeaveConfirmation.InDialog(dialogs, () =>
    {
        Interlocked.Increment(ref prompts);
        return NavigationTarget.Own(new ConfirmPage());
    }, () => true, () => discards++).CanDepartAsync;
    await main.PushAsync(NavigationTarget.Own(document));
    using var back = main.CreateBackCommand(ContextScheduler.For(outputContext));
    await WaitUntilAsync(() => ReactiveCommandExecution.CanExecute(back, UnitValue()), "Back was not enabled.");
    using var held = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    Require(outputContext.TryPost(() => { held.Set(); release.Wait(); }), "Could not hold the output scheduler.");
    Require(held.Wait(TimeSpan.FromSeconds(5)), "The output scheduler did not pause.");
    try
    {
        var first = ReactiveCommandExecution.Execute(back, UnitValue(), CancellationToken.None);
        var second = ReactiveCommandExecution.Execute(back, UnitValue(), CancellationToken.None);
        await WaitUntilAsync(() => dialogs.Current?.Entry is not null, "The confirmation did not open.");
        var prompt = dialogs.Current!;
        await WaitUntilAsync(() => logs.Joins == 1, "The token-bearing second command did not join the first Back.");
        Require(prompts == 1 && dialogs.Current == prompt && !prompt.Entry!.Retirement.IsCancellationRequested,
            "The second command execution retired the existing confirmation.");
        await prompt.Entry!.CompleteAsync(true);
        await WaitUntilAsync(() => !main.IsTransitioning, "The confirmed Back did not commit.");
        release.Set();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Require(results.All(result => result.Kind == NavigationOutcomeKind.Committed) && main.Current == home
            && prompts == 1 && discards == 1 && dialogs.Current is null,
            "Duplicate Back commands did not share one confirmation and one commit.");
    }
    finally { release.Set(); }
}

static FlavorUnit UnitValue() => FlavorUnit.Default;


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
    public Func<NavigationDeparture, CancellationToken, ValueTask<bool>>? DepartureGuard { get; set; }
    public async ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        DepartureGuard is { } departureGuard ? await departureGuard(departure, cancellationToken)
        : Guard is not { } guard || await guard();
}
sealed class ConfirmPage : INavigationInitialize
{
    public NavigationEntryContext? Entry { get; private set; }
    public ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
    {
        Entry = entry;
        return ValueTask.CompletedTask;
    }
}
sealed class BackJoinLog : ILoggerFactory, ILogger
{
    private int _joins;
    public int Joins => Volatile.Read(ref _joins);
    public ILogger CreateLogger(string categoryName) => this;
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (eventId.Id == 1074) Interlocked.Increment(ref _joins);
    }
    public void Dispose() { }
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

// A scheduler that posts to a model context. Runic.Application.ReactiveUI provides the production one; the
// navigation adapter takes any scheduler, so this fixture keeps to Runic.Navigation.
static class ContextScheduler
{
    public static FlavorScheduler For(IRunicModelContext context) =>
#if SYSTEM_REACTIVE
        new System.Reactive.Concurrency.SynchronizationContextScheduler(new ContextPost(context), alwaysPost: true);
#else
        new SynchronizationContextSequencer(new ContextPost(context));
#endif

    private sealed class ContextPost(IRunicModelContext context) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => context.TryPost(() => callback(state));

        public override SynchronizationContext CreateCopy() => this;
    }
}
