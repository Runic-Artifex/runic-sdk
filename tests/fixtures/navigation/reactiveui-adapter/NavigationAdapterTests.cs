// Behavioral conformance of the Runic.Navigation ReactiveUI adapter, once per flavor. It references
// only the navigation adapter package, so it also proves the adapter needs no Runic.Application.Views.
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Runic.Navigation;
using TUnit.Core;
#if SYSTEM_REACTIVE
using ReactiveUI.Reactive;
using System.Reactive.Linq;
using ReactiveUI.Binding.Reactive;
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

internal sealed class NavigationAdapterTests
{
    [Test, NotInParallel(Order = 1)]
    public async Task NavigationAdapterSemanticsAsync()
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

    [Test, NotInParallel(Order = 2)]
    public async Task NavigationStateCompositionAsync()
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

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
        Require(condition(), message);
    }

    // Hold output delivery as a dispatcher would while two already-dispatched command executions start.
    // CanExecute is advisory for programmatic Execute; both actual executions carry ReactiveUI's tokens.
    [Test, NotInParallel(Order = 3)]
    public async Task DuplicateBackCommandKeepsOneConfirmationAsync()
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

    // WhenNavigatedTo and WhenNavigatedFrom follow the entry's core state in the commit turn (W250-034).
    [Test, NotInParallel(Order = 4)]
    public async Task EntryLifecycleAsync()
    {
        await using var context = new RunicModelContext();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        var region = navigator.CreateRegion<object>(new object(), NavigationTarget.Borrow<object>(new NavigationPage("home")));
        var arrivals = new List<(NavigationResume Resume, bool InTurn)>();
        var scopes = new List<DisposeProbe>();
        var exits = new Recorder<NavigationExit>();
        IDisposable? arrival = null;
        IDisposable? exitSubscription = null;
        EntryPage? editor = null;
        var push = await region.PushAsync(NavigationTarget.Create((_, entry) =>
        {
            // Constructor-time subscriptions, while the entry is pending.
            editor = new EntryPage(entry);
            arrival = entry.WhenNavigatedTo((resume, scope) =>
            {
                arrivals.Add((resume, context.IsExecuting));
                var probe = new DisposeProbe(context);
                scopes.Add(probe);
                scope.Add(probe);
            });
            exitSubscription = entry.WhenNavigatedFrom().Subscribe(exits);
            return editor;
        }));
        var id = editor!.Entry.Id;
        Require(push is NavigationResult<object>.Committed && arrivals.Count == 1 && arrivals[0].InTurn
            && arrivals[0].Resume == new NavigationResume(id, NavigationOperation.Push) && exits.Values.Length == 0,
            "WhenNavigatedTo did not run once, in the commit turn, when the push committed.");

        await region.PushAsync(NavigationTarget.Own(new NavigationPage("next")));
        Require(scopes[0].Disposed == 1 && scopes[0].DisposedInTurn && arrivals.Count == 1
            && exits.Values.SequenceEqual([new NavigationExit(id, NavigationOperation.Push, NavigationDepartureKind.Retain)]) && !exits.Completed,
            "Retaining the entry did not dispose its scope in the commit turn and emit a retained exit.");

        await region.BackAsync();
        Require(arrivals.Count == 2 && arrivals[1].InTurn && arrivals[1].Resume == new NavigationResume(id, NavigationOperation.Back)
            && scopes.Count == 2 && scopes[1].Disposed == 0,
            "Resuming the entry did not call WhenNavigatedTo with Back and a new scope.");

        await region.BackAsync();
        Require(scopes[1].Disposed == 1 && scopes[1].DisposedInTurn && scopes[0].Disposed == 1 && exits.Completed
            && exits.Values[^1] == new NavigationExit(id, NavigationOperation.Back, NavigationDepartureKind.Retire),
            "Retiring the entry did not dispose its scope once and complete WhenNavigatedFrom after a retired exit.");
        arrival!.Dispose();
        exitSubscription!.Dispose();
        Require(scopes.All(scope => scope.Disposed == 1), "Disposing a finished subscription disposed a scope again.");

        // A subscription to a retired entry completes at once.
        var late = new Recorder<NavigationExit>();
        using (editor.Entry.WhenNavigatedFrom().Subscribe(late))
            Require(late.Completed && late.Values.Length == 0, "WhenNavigatedFrom on a retired entry did not complete at once.");

        // Disposing the subscription disposes the current scope and stops later calls.
        var page = new EntryInitializePage();
        await region.PushAsync(NavigationTarget.Own(page));
        var probes = new List<DisposeProbe>();
        var subscription = page.Entry!.WhenNavigatedTo((_, scope) =>
        {
            var probe = new DisposeProbe(context);
            probes.Add(probe);
            scope.Add(probe);
        });
        // Subscribed while current: nothing is replayed; the next arrival starts.
        Require(probes.Count == 0, "WhenNavigatedTo replayed the current arrival.");
        await region.PushAsync(NavigationTarget.Own(new NavigationPage("over")));
        await region.BackAsync();
        Require(probes.Count == 1 && probes[0].Disposed == 0, "WhenNavigatedTo subscribed while current missed the next arrival.");
        subscription.Dispose();
        Require(probes[0].Disposed == 1, "Disposing WhenNavigatedTo did not dispose the current scope.");
        await region.PushAsync(NavigationTarget.Own(new NavigationPage("again")));
        await region.BackAsync();
        Require(probes.Count == 1, "A disposed WhenNavigatedTo subscription was called again.");

        // A context that closes before the departure is raised still disposes the scope when the entry retires.
        var closing = new RunicModelContext();
        var closingNavigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = closing });
        var closingRegion = closingNavigator.CreateRegion<object>(new object());
        DisposeProbe? closingProbe = null;
        await closingRegion.PushAsync(NavigationTarget.Create((_, entry) =>
        {
            entry.WhenNavigatedTo((_, scope) => scope.Add(closingProbe = new DisposeProbe(closing)));
            return new EntryPage(entry);
        }));
        await closing.DisposeAsync();
        await closingNavigator.DisposeAsync();
        await WaitUntilAsync(() => closingProbe?.Disposed == 1, "Closing the model context did not dispose the scope of a current entry.");
    }

    // Complete, dismiss and close commands end Completed, Dismissed, and stay open on a veto (design §4.3).
    [Test, NotInParallel(Order = 5)]
    public async Task ResultCommandsAsync()
    {
        await using var context = new RunicModelContext();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        var home = new NavigationPage("home");
        var region = navigator.CreateRegion<object>(new object(), NavigationTarget.Borrow<object>(home));

        async Task<(NavigationResultRequest<object, string> Request, EntryPage Page)> OpenAsync()
        {
            var request = region.PushForResult<string>(NavigationTarget.Create((_, entry) => new EntryPage(entry)));
            Require(await request.Transition is NavigationResult<object>.Committed, "The result request was not pushed.");
            return (request, (EntryPage)region.Current!);
        }

        // Complete with a produced result; no scheduler argument.
        var (request, page) = await OpenAsync();
        var produced = 0;
        using (var save = page.Entry.CreateCompleteCommand(async cancellationToken =>
        {
            await Task.Yield();
            produced++;
            return "saved";
        }))
        {
            page.Veto = true;
            var vetoed = await ReactiveCommandExecution.Execute(save, UnitValue(), CancellationToken.None);
            Require(vetoed == new NavigationOutcome(NavigationOutcomeKind.Rejected, NavigationRejection.Guard)
                && region.Current == page && !request.Completion.IsCompleted && produced == 1,
                $"A vetoed complete command gave {vetoed}; the request must stay open.");
            page.Veto = false;
            var saved = await ReactiveCommandExecution.Execute(save, UnitValue(), CancellationToken.None);
            Require(saved == new NavigationOutcome(NavigationOutcomeKind.Committed) && region.Current == home
                && await request.Completion is NavigationCompletion<string>.Completed { Value: "saved" } && produced == 2,
                $"The complete command gave {saved}, not a completed request.");
        }

        // Complete with the command's input.
        (request, page) = await OpenAsync();
        using (var pick = page.Entry.CreateCompleteCommand<string>())
        {
            var picked = await ReactiveCommandExecution.Execute(pick, "picked", CancellationToken.None);
            Require(picked.Kind == NavigationOutcomeKind.Committed
                && await request.Completion is NavigationCompletion<string>.Completed { Value: "picked" },
                $"The input complete command gave {picked}.");
        }

        // Dismiss ends the request at once, even when the guard then keeps the entry.
        (request, page) = await OpenAsync();
        using (var dismiss = page.Entry.CreateDismissCommand())
        {
            page.Veto = true;
            var kept = await ReactiveCommandExecution.Execute(dismiss, UnitValue(), CancellationToken.None);
            Require(kept.Kind == NavigationOutcomeKind.Rejected && region.Current == page
                && await request.Completion.WaitAsync(TimeSpan.FromSeconds(5)) is NavigationCompletion<string>.Dismissed,
                $"A vetoed dismiss command gave {kept}; the request must be dismissed.");
            page.Veto = false;
            var dismissed = await ReactiveCommandExecution.Execute(dismiss, UnitValue(), CancellationToken.None);
            Require(dismissed.Kind == NavigationOutcomeKind.Committed && region.Current == home, $"The dismiss command gave {dismissed}.");
        }

        // Close leaves through the guards; the request stays open on a veto and ends Dismissed when the Back commits.
        (request, page) = await OpenAsync();
        using (var close = page.Entry.CreateCloseCommand())
        {
            page.Veto = true;
            var vetoed = await ReactiveCommandExecution.Execute(close, UnitValue(), CancellationToken.None);
            Require(vetoed == new NavigationOutcome(NavigationOutcomeKind.Rejected, NavigationRejection.Guard)
                && region.Current == page && !request.Completion.IsCompleted,
                $"A vetoed close command gave {vetoed}; the request must stay open.");
            page.Veto = false;
            var closed = await ReactiveCommandExecution.Execute(close, UnitValue(), CancellationToken.None);
            Require(closed.Kind == NavigationOutcomeKind.Committed && region.Current == home
                && await request.Completion is NavigationCompletion<string>.Dismissed,
                $"The close command gave {closed}, not a dismissed request.");
        }

        // Completing an entry that has no request fails the execution and reaches ThrownExceptions.
        await region.PushAsync(NavigationTarget.Create((_, entry) => new EntryPage(entry)));
        page = (EntryPage)region.Current!;
        var thrown = new Recorder<Exception>();
        using (var orphan = page.Entry.CreateCompleteCommand(_ => Task.FromResult("lost")))
        using (orphan.ThrownExceptions.Subscribe(thrown))
        {
            await RequireThrowsAsync<InvalidOperationException>(
                () => ReactiveCommandExecution.Execute(orphan, UnitValue(), CancellationToken.None),
                "Completing an entry without a request did not fail.");
            await WaitUntilAsync(() => thrown.Values.Length == 1, "A complete command without a request did not report ThrownExceptions.");
            Require(region.Current == page, "A failed complete command left the entry.");
        }
    }

    // ReactiveLeaveConfirmation.WithInteraction keeps the LeaveConfirmation rules and fails closed (W250-035).
    [Test, NotInParallel(Order = 6)]
    public async Task InteractionLeaveConfirmationAsync()
    {
        await using var context = new RunicModelContext();
        using var log = new EventLog();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context, LoggerFactory = log });
        var home = new NavigationPage("home");
        var region = navigator.CreateRegion<object>(new object(), NavigationTarget.Borrow<object>(home));
        await region.PushAsync(NavigationTarget.Create((_, entry) => new EntryPage(entry)));
        var editor = (EntryPage)region.Current!;
        var prompts = new List<bool>();
        var discards = new List<bool>();
        editor.Leave = ReactiveLeaveConfirmation.WithInteraction(editor.ConfirmDiscard,
            () => { prompts.Add(context.IsExecuting); return $"discard {prompts.Count}"; },
            () => editor.Dirty, () => discards.Add(context.IsExecuting));

        // Clean: nothing is asked, even with no handler.
        var clean = await region.PushAsync(NavigationTarget.Own(new NavigationPage("clean")));
        Require(clean is NavigationResult<object>.Committed && prompts.Count == 0, "A clean entry asked before a push.");
        await region.BackAsync();

        // Unhandled: fails closed with Failed(Guarding) and event 1060 naming ReactiveUI's exception.
        editor.Dirty = true;
        var unhandled = await region.BackAsync();
        Require(unhandled is NavigationResult<object>.Failed { Phase: NavigationPhase.Guarding } failed
            && failed.Error.GetType().Name.StartsWith("UnhandledInteractionException", StringComparison.Ordinal)
            && unhandled.Outcome.Kind == NavigationOutcomeKind.Failed && region.Current == editor && discards.Count == 0,
            $"An unhandled interaction gave {unhandled}; the departure must fail closed.");
        Require(log.Entries.Any(entry => entry.Id == 1060 && entry.Message.Contains("UnhandledInteractionException", StringComparison.Ordinal)),
            "An unhandled interaction was not logged as NavigationGuardFailed (1060) with its exception type.");

        var asked = new List<string>();
        // A retaining departure is allowed without asking.
        using (editor.ConfirmDiscard.RegisterHandler(interaction => { asked.Add(interaction.Input); interaction.SetOutput(false); }))
        {
            var retained = await region.PushAsync(NavigationTarget.Own(new NavigationPage("child")));
            Require(retained is NavigationResult<object>.Committed && asked.Count == 0, "A retaining departure asked the interaction.");
            await region.BackAsync();

            // No: the entry stays and nothing is discarded. The prompt is created inside a model turn.
            var kept = await region.BackAsync();
            Require(kept is NavigationResult<object>.Rejected { Reason: NavigationRejection.Guard } && region.Current == editor
                && asked.SequenceEqual(["discard 2"]) && prompts.SequenceEqual([true, true]) && discards.Count == 0,
                $"A no answer gave {kept}.");
        }

        // Superseded: a later push ends the question; the guard returns without the handler, and a late yes is dropped.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateAnswers = 0;
        using (editor.ConfirmDiscard.RegisterHandler(async interaction =>
        {
            entered.TrySetResult();
            var answer = await release.Task;
            interaction.SetOutput(answer);
            Interlocked.Increment(ref lateAnswers);
        }))
        {
            var back = region.BackAsync().AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var winner = await region.PushAsync(NavigationTarget.Own(new NavigationPage("winner"))).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            var superseded = await back.WaitAsync(TimeSpan.FromSeconds(10));
            Require(superseded is NavigationResult<object>.Superseded && winner is NavigationResult<object>.Committed,
                $"A question superseded by a push gave {superseded} and {winner}.");
            release.SetResult(true);
            await WaitUntilAsync(() => Volatile.Read(ref lateAnswers) == 1, "The superseded handler did not finish.");
            Require(discards.Count == 0 && region.Current is NavigationPage { Name: "winner" }, "A superseded yes discarded or departed.");
            await region.BackAsync();
        }

        // Cancelled by the caller: the question ends with the Back.
        entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (editor.ConfirmDiscard.RegisterHandler(async interaction =>
        {
            entered.TrySetResult();
            interaction.SetOutput(await never.Task);
        }))
        {
            using var cancellation = new CancellationTokenSource();
            var back = region.BackAsync(cancellationToken: cancellation.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            var cancelled = await back.WaitAsync(TimeSpan.FromSeconds(10));
            Require(cancelled is NavigationResult<object>.Rejected { Reason: NavigationRejection.Cancelled } && region.Current == editor,
                $"A cancelled question gave {cancelled}.");
        }
        never.SetResult(false);

        // Yes: the entry departs and discard runs once, in the commit turn.
        using (editor.ConfirmDiscard.RegisterHandler(interaction => interaction.SetOutput(true)))
        {
            var left = await region.BackAsync();
            Require(left is NavigationResult<object>.Committed && region.Current == home && discards.SequenceEqual([true]),
                $"A yes answer gave {left} with {discards.Count} discards.");
        }

        // askOnRetain asks before a push too.
        await region.PushAsync(NavigationTarget.Create((_, entry) => new EntryPage(entry) { Dirty = true }));
        var asking = (EntryPage)region.Current!;
        asking.Leave = ReactiveLeaveConfirmation.WithInteraction(asking.ConfirmDiscard, () => "retain", () => asking.Dirty, askOnRetain: true);
        var retainAsks = 0;
        using (asking.ConfirmDiscard.RegisterHandler(interaction => { retainAsks++; interaction.SetOutput(false); }))
            Require(await region.PushAsync(NavigationTarget.Own(new NavigationPage("blocked"))) is NavigationResult<object>.Rejected
                && retainAsks == 1 && region.Current == asking, "askOnRetain did not ask before a retaining push.");
    }

    private static FlavorUnit UnitValue() => FlavorUnit.Default;


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


sealed class Recorder<T> : IObserver<T>
{
    private readonly Lock _lock = new();
    private readonly List<T> _values = [];
    public T[] Values { get { lock (_lock) return [.. _values]; } }
    private int _completed;
    public bool Completed => Volatile.Read(ref _completed) != 0;
    public void OnCompleted() => Volatile.Write(ref _completed, 1);
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
    public static bool CanExecute<TParam, TResult>(ReactiveCommand<TParam, TResult> command, TParam input) =>
        ((ICommand)command).CanExecute(input);

    public static async Task<TResult> Execute<TParam, TResult>(ReactiveCommand<TParam, TResult> command, TParam input,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        using var subscription = command.Execute(input).Subscribe(new CallbackObserver<TResult>(
            value => completion.TrySetResult(value), error => completion.TrySetException(error)));
        return await completion.Task;
    }
}

// A scheduler that posts to a model context. Runic.Application.Views.ReactiveUI provides the production one; the
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

// Content that takes its entry context in the constructor, with a leave confirmation answered by an interaction.
sealed class EntryPage(NavigationEntryContext entry) : INavigationDepartureGuard
{
    public NavigationEntryContext Entry { get; } = entry;
    public Interaction<string, bool> ConfirmDiscard { get; } = new();
    public bool Dirty { get; set; }
    public bool Veto { get; set; }
    public LeaveConfirmation? Leave { get; set; }
    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        Veto ? ValueTask.FromResult(false)
        : Leave is { } leave ? leave.CanDepartAsync(departure, cancellationToken)
        : ValueTask.FromResult(true);
}

sealed class EntryInitializePage : INavigationInitialize
{
    public NavigationEntryContext? Entry { get; private set; }
    public ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
    {
        Entry = entry;
        return ValueTask.CompletedTask;
    }
}

sealed class DisposeProbe(RunicModelContext context) : IDisposable
{
    private int _disposed;
    public int Disposed => Volatile.Read(ref _disposed);
    public bool DisposedInTurn { get; private set; }
    public void Dispose()
    {
        DisposedInTurn = context.IsExecuting;
        Interlocked.Increment(ref _disposed);
    }
}

sealed class EventLog : ILoggerFactory, ILogger
{
    private readonly Lock _lock = new();
    private readonly List<(int Id, string Message)> _entries = [];
    public (int Id, string Message)[] Entries { get { lock (_lock) return [.. _entries]; } }
    public ILogger CreateLogger(string categoryName) => this;
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lock) _entries.Add((eventId.Id, formatter(state, exception)));
    }
    public void Dispose() { }
}
