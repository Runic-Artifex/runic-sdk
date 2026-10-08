using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Runic.Navigation.Tests;

// W240-004 slice 3: container-resolved targets, entry scopes, the initial-target cycle guard,
// departure actions, DismissAsync and LeaveConfirmation (design record W240-001 §5, §6).
internal static partial class NavigationTests
{
    private static async Task RunStandaloneAsync()
    {
        await ContainerTargetsAsync();
        await ContainerTargetsWithoutServicesAsync();
        await EntryScopesAsync();
        await EntryScopeFailuresAsync();
        await EntryScopeOfAbandonedPushAsync();
        await CloseDuringFactoryAsync(late: false);
        await CloseDuringFactoryAsync(late: true);
        await InitialTargetCycleAsync();
        await OnCommittedAsync();
        await OnCommittedDroppedAsync();
        await SettlementAsync();
        await DismissAsync();
        await DismissFromInitializeAsync(forResult: true);
        await DismissFromInitializeAsync(forResult: false);
        await DismissEdgeCasesAsync();
        await LeaveConfirmationAsync();
        await LeaveConfirmationParentRerunAsync();
        await LeaveConfirmationSameRegionSupersederAsync();
        for (var seed = 0; seed < 40; seed++) await LeaveConfirmationRaceAsync(seed);
        await LeaveConfirmationInDialogAsync(complete: true);
        await LeaveConfirmationInDialogAsync(complete: false);
    }

    private static ServiceProvider StandaloneServices(Action<ServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Clock>();
        services.AddScoped<Scoped>();
        services.AddScoped<BadScoped>();
        services.AddTransient<Transient>();
        services.AddSingleton<InitSignal>();
        configure?.Invoke(services);
        // Scoped services can't be resolved from the root: entry scopes must provide them.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task ContainerTargetsAsync()
    {
        await using var provider = StandaloneServices();
        await using var fixture = new Fixture(services: provider);
        var clock = provider.GetRequiredService<Clock>();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Create<ClockPage>());
        Require(region.Current is ClockPage { Clock: var initialClock } && initialClock == clock
            && region.CurrentEntry is { Ownership: NavigationOwnership.Owned } initial && ReferenceEquals(initial.Services, provider),
            "Create<T>() as an initial target did not construct an owned page from the navigator's services.");

        var pushed = await Wait(region.PushAsync<ClockPage>());
        Require(pushed is NavigationResult<Page>.Committed { Current: { Content: ClockPage, Ownership: NavigationOwnership.Owned } },
            $"PushAsync<T>() gave {pushed}.");
        await Wait(region.PushAsync<InputClockPage, string>("hello"));
        Require(region.Current is InputClockPage { Input: "hello", Clock: var inputClock } && inputClock == clock,
            "PushAsync<T, TInput>() did not construct and initialize with the input.");
        await Wait(region.ReplaceAsync<TwoCtorPage>());
        Require(Names(region) == "clock,clock,marked", $"ReplaceAsync<T>() or [ActivatorUtilitiesConstructor] gave {Names(region)}.");
        await Wait(region.ReplaceAsync<InputClockPage, string>("again"));
        Require(region.Current is InputClockPage { Input: "again" } && Names(region) == "clock,clock,input", "ReplaceAsync<T, TInput>() failed.");
        await Wait(region.ResetAsync<ClockPage>());
        Require(Names(region) == "clock" && !region.CanGoBack, $"ResetAsync<T>() gave {Names(region)}.");
        await Wait(region.ResetAsync<InputClockPage, string>("reset"));
        Require(region.Current is InputClockPage { Input: "reset" } && !region.CanGoBack, "ResetAsync<T, TInput>() failed.");

        var request = region.PushForResult<ResultPage, int>();
        Require(await Wait(request.Transition) is NavigationResult<Page>.Committed && region.Current is ResultPage,
            "PushForResult<TViewModel, TResult>() did not push.");
        await Wait(((ResultPage)region.Current!).Entry!.CompleteAsync(7));
        Require(await Wait(request.Completion) is NavigationCompletion<int>.Completed { Value: 7 }, "The container-built result page did not complete.");

        Require(Throws<ArgumentException>(() => fixture.Navigator.CreateRegion<Page>(fixture.Root,
            NavigationTarget.Create<InputClockPage, string>("initial"))), "Create<T, TInput>() was accepted as an initial target.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(fixture.Navigator.UnretiredEntryCount == 1, $"{fixture.Navigator.UnretiredEntryCount} entries are tracked.");
    }

    private static async Task ContainerTargetsWithoutServicesAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var pushed = await Wait(region.PushAsync<ClockPage>());
        Require(pushed is NavigationResult<Page>.Failed { Phase: NavigationPhase.Preparing, Error: InvalidOperationException }
            && fixture.Logs.Has(1061, LogLevel.Error) && Names(region) == "home",
            $"A container target without services gave {pushed}.");
        var parameterless = await Wait(region.PushAsync<TwoCtorPage>());
        Require(parameterless is NavigationResult<Page>.Failed, "The marked constructor was not preferred without services.");
        Require(region.CurrentEntry!.Services.GetService(typeof(Clock)) is null, "An entry without services did not get an empty provider.");
    }

    private static async Task EntryScopesAsync()
    {
        await using var scopeless = new RunicModelContext();
        Require(Throws<ArgumentException>(() => _ = new RunicNavigator(new RunicNavigatorOptions { ModelContext = scopeless, CreateEntryScopes = true })),
            "CreateEntryScopes without Services was accepted.");
        await using var provider = StandaloneServices();
        await using var fixture = new Fixture(services: provider, entryScopes: true);
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Create<ScopedPage>());
        var initial = (ScopedPage)region.Current!;
        Require(!ReferenceEquals(region.CurrentEntry!.Services, provider)
            && ReferenceEquals(region.CurrentEntry.Services.GetRequiredService<Scoped>(), initial.Scoped),
            "The initial target did not get its own scope.");

        await Wait(region.PushAsync<ScopedPage>());
        var second = (ScopedPage)region.Current!;
        Require(!ReferenceEquals(second.Scoped, initial.Scoped), "Two entries shared a scope.");

        IServiceProvider? seen = null;
        await Wait(region.PushAsync(NavigationTarget.Create<Page>(services =>
        {
            seen = services;
            return new Page("factory");
        })));
        var factoryScoped = region.CurrentEntry!.Services.GetRequiredService<Scoped>();
        Require(seen is not null && !ReferenceEquals(seen, provider) && ReferenceEquals(region.CurrentEntry.Services, seen),
            "A factory target did not receive its entry's scope.");

        var borrowed = await Wait(region.PushAsync(NavigationTarget.Borrow(new Page("borrowed"))));
        Require(borrowed is NavigationResult<Page>.Committed { Current.Services: var borrowedServices } && ReferenceEquals(borrowedServices, provider),
            "A borrowed entry got a scope.");

        await Wait(region.ResetAsync(NavigationTarget.Borrow(new Page("end"))));
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(initial.Scoped is { Disposed: 1, OwnerDisposedFirst: true } && second.Scoped is { Disposed: 1, OwnerDisposedFirst: true }
            && factoryScoped.Disposed == 1 && initial.Disposed == 1 && second.Disposed == 1
            && initial.Transient.Disposed == 1 && second.Transient.Disposed == 1,
            "Retirement did not dispose each scope and its transients once, after the content.");
        Require(fixture.Navigator.UnretiredEntryCount == 1, "Retired scoped entries are still tracked.");
    }

    private static async Task EntryScopeFailuresAsync()
    {
        await using var provider = StandaloneServices();
        await using var fixture = new Fixture(services: provider, entryScopes: true);
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));

        // A failing initialize retires the entry: content, then scope.
        FailingPage? failing = null;
        var failed = await Wait(region.PushAsync(NavigationTarget.Create<Page>(services =>
            failing = ActivatorUtilities.CreateInstance<FailingPage>(services))));
        Require(failed is NavigationResult<Page>.Failed { Phase: NavigationPhase.Preparing }, $"A failing initialize gave {failed}.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(failing!.Scoped is { Disposed: 1, OwnerDisposedFirst: true } && failing.Disposed == 1,
            "A failed initialize did not dispose the content, then the scope.");

        // A failing factory disposes the scope it received.
        Scoped? orphan = null;
        var factory = await Wait(region.PushAsync(NavigationTarget.Create<Page>(services =>
        {
            orphan = services.GetRequiredService<Scoped>();
            throw new InvalidOperationException("factory");
        })));
        Require(factory is NavigationResult<Page>.Failed { Phase: NavigationPhase.Preparing }, $"A failing factory gave {factory}.");
        await Until(() => orphan is { Disposed: 1 }, "A failing factory's scope was not disposed.");

        // A failing initial target disposes its scope too.
        Scoped? initialOrphan = null;
        Require(Throws<InvalidOperationException>(() => fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Create<Page>(services =>
        {
            initialOrphan = services.GetRequiredService<Scoped>();
            throw new InvalidOperationException("initial");
        }))), "A failing initial target did not throw.");
        await Until(() => initialOrphan is { Disposed: 1 }, "A failing initial target's scope was not disposed.");

        // A scope whose disposal throws is logged as a cleanup step; retirement completes.
        await Wait(region.PushAsync<BadScopePage>());
        await Wait(region.BackAsync());
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(fixture.Logs.All(1064).Any(entry => entry.State["Step"] as string == "DisposeScope" && entry.Exception is BadScopeException)
            && fixture.Navigator.UnretiredEntryCount == 1, "A failing scope disposal was not logged as DisposeScope.");
    }

    private static async Task InitialTargetCycleAsync()
    {
        var holder = new NavigatorHolder();
        await using var provider = StandaloneServices(services =>
        {
            services.AddSingleton(holder);
            services.AddSingleton(sp => sp.GetRequiredService<NavigatorHolder>().Navigator!);
            services.AddSingleton<MainNavigation>();
            services.AddSingleton<ResettingNavigation>();
        });
        await using var fixture = new Fixture(services: provider);
        holder.Navigator = fixture.Navigator;

        // MainNavigation creates its region with HomePage, which needs MainNavigation.
        Exception? error = null;
        try { provider.GetRequiredService<MainNavigation>(); }
        catch (Exception caught) { error = caught; }
        var cycle = error;
        while (cycle is not null && !(cycle is InvalidOperationException && cycle.Message.Contains("dependency cycle", StringComparison.Ordinal)))
            cycle = cycle.InnerException;
        Require(cycle is InvalidOperationException { Message: var cycleMessage } && cycleMessage.Contains(nameof(HomePage), StringComparison.Ordinal)
            && cycleMessage.Contains(nameof(MainNavigation), StringComparison.Ordinal) && cycleMessage.Contains("ResetAsync<HomePage>()", StringComparison.Ordinal),
            $"The initial-target cycle was not reported: {error}");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(fixture.Navigator.UnretiredEntryCount == 0, "The failed cycle left entries behind.");

        // The documented pattern: an empty region reset after construction.
        var resetting = provider.GetRequiredService<ResettingNavigation>();
        await Wait(resetting.Region.ResetAsync<ResetHomePage>());
        Require(resetting.Region.Current is ResetHomePage { Navigation: var navigation } && navigation == resetting, "The reset pattern failed.");

        // A page created as an initial target may create its own child region with an initial target.
        var shell = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Create<ShellPage>());
        Require(shell.Current is ShellPage { Child.Current: ClockPage }, "A child region's initial target was taken for a cycle.");

        // The same owner type with a different initial target is not a cycle.
        var outer = new RegionHolder(fixture.Navigator, NavigationTarget.Create<LevelOnePage>());
        Require(outer.Region.Current is LevelOnePage { Inner.Region.Current: LevelTwoPage }, "A holder type reused at two levels was taken for a cycle.");
    }

    private static async Task OnCommittedAsync()
    {
        await using var fixture = new Fixture();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var document = new Page("document");
        await Wait(region.PushAsync(NavigationTarget.Own(document)));
        var changes = 0;
        region.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(region.Current)) Interlocked.Increment(ref changes);
        };
        Volatile.Write(ref changes, 0);
        List<string> seen = [];
        NavigationDeparture? saved = null;
        document.Guard = (departure, _) =>
        {
            saved = departure;
            departure.OnCommitted(() => seen.Add($"current={region.Current} turn={fixture.Context.IsExecuting} "
                + $"gate={Monitor.IsEntered(fixture.Navigator.Gate)} changes={Volatile.Read(ref changes)}"));
            departure.OnCommitted(() => throw new InvalidOperationException("action"));
            departure.OnCommitted(() => seen.Add("third"));
            return ValueTask.FromResult(true);
        };
        var back = await Wait(region.BackAsync());
        Require(back is NavigationResult<Page>.Committed && seen is ["current=home turn=True gate=False changes=0", "third"] && Volatile.Read(ref changes) > 0,
            $"OnCommitted actions did not run in the commit turn, in order, after the state change and before PropertyChanged: {string.Join(" | ", seen)}");
        var logged = fixture.Logs.Single(1072);
        Require(logged is { Level: LogLevel.Error, Exception: InvalidOperationException } && logged.State["EntryType"] as string == "Page"
            && logged.State["Operation"] as string == "Back", $"A failing action was not logged as 1072: {logged}.");
        Require(Throws<InvalidOperationException>(() => saved!.OnCommitted(() => { })), "OnCommitted was accepted after the guard returned.");
        Require(saved!.ToString().Contains("Retire", StringComparison.Ordinal), $"The departure's text is {saved}.");
    }

    private static async Task OnCommittedDroppedAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var document = new Page("document");
        await Wait(region.PushAsync(NavigationTarget.Own(document)));
        var ran = 0;

        // A vetoed departure drops its actions.
        document.Guard = (departure, _) =>
        {
            departure.OnCommitted(() => ran++);
            return ValueTask.FromResult(false);
        };
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard } && ran == 0,
            "A vetoed departure ran its action.");

        // So does a departure whose transition another guard vetoes after it allowed.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        document.Guard = async (departure, token) =>
        {
            if (departure.Kind != NavigationDepartureKind.Retire) return true;
            departure.OnCommitted(() => ran++);
            started.TrySetResult();
            try { await Task.Delay(-1, token); }
            catch (OperationCanceledException) { }
            return true;
        };
        var back = region.BackAsync().AsTask();
        await Wait(started.Task);
        var push = await Wait(region.PushAsync(NavigationTarget.Own(new Page("other"))));
        Require(await Wait(back) is NavigationResult<Page>.Superseded && push is NavigationResult<Page>.Committed && ran == 0,
            $"A superseded departure ran its action: {ran}.");
    }

    private static async Task SettlementAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var document = new Page("document");
        await Wait(region.PushAsync(NavigationTarget.Own(document)));

        // Rejected: ended.
        document.Guard = (_, _) => ValueTask.FromResult(false);
        await Wait(region.BackAsync());
        Require(await Wait(document.Departures[^1].Settled) is { Outcome: NavigationDepartureOutcome.Ended, SupersededBy: null },
            "A vetoed transition did not settle as ended.");

        // Superseded by a later request: names the superseder.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        document.Guard = async (departure, token) =>
        {
            if (started.TrySetResult())
            {
                try { await Task.Delay(-1, token); }
                catch (OperationCanceledException) { }
            }
            return true;
        };
        var first = region.BackAsync().AsTask();
        await Wait(started.Task);
        // A Back with a cancellable token supersedes a pending Back; a plain one would join it.
        using var superseding = new CancellationTokenSource();
        var second = region.BackAsync(cancellationToken: superseding.Token).AsTask();
        Require(await Wait(first) is NavigationResult<Page>.Superseded && await Wait(second) is NavigationResult<Page>.Committed,
            "The second Back did not supersede the first.");
        var superseded = document.Departures[^2];
        var winner = document.Departures[^1];
        var settlement = await Wait(superseded.Settled);
        Require(settlement is { Outcome: NavigationDepartureOutcome.Superseded } && settlement.SupersededBy == winner.Transition
            && ReferenceEquals(settlement.Superseder, winner.Settled) && superseded.Transition != winner.Transition,
            $"A supersession did not name its superseder: {settlement}.");
        Require(await Wait(winner.Settled) is { Outcome: NavigationDepartureOutcome.Committed, SupersededBy: null }, "A commit did not settle as committed.");

        // Superseded by a change in another region (a child commit): the superseder is unknown.
        ParentPage? parent = null;
        await Wait(region.PushAsync(NavigationTarget.Create<Page>(_ => parent = new ParentPage("parent", fixture.Navigator))));
        var guarding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        parent!.Guard = async (_, _) =>
        {
            guarding.TrySetResult();
            return await gate.Task;
        };
        var parentBack = region.BackAsync().AsTask();
        await Wait(guarding.Task);
        await Wait(parent.Child.PushAsync(NavigationTarget.Own(new Page("child"))));
        gate.SetResult(true);
        Require(await Wait(parentBack) is NavigationResult<Page>.Superseded
            && await Wait(parent.Departures[^1].Settled) is { Outcome: NavigationDepartureOutcome.Superseded, SupersededBy: null, Superseder: null },
            "A basis change named a superseder.");
    }

    private static async Task DismissAsync()
    {
        await using var fixture = new Fixture();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));

        // Current with a request: dismissed, then back.
        var picker = new InitPage("picker");
        var request = region.PushForResult<int>(NavigationTarget.Own<Page>(picker));
        await Wait(request.Transition);
        var dismissed = await Wait(picker.Entry!.DismissAsync());
        Require(dismissed is NavigationResult<object>.Committed && await Wait(request.Completion) is NavigationCompletion<int>.Dismissed
            && region.Current == home, $"Dismissing a current result entry gave {dismissed}.");
        Require(fixture.Logs.All(1070).Any(entry => entry.State["Reason"]?.ToString() == "Dismissed"), "The dismissal reason was not logged.");

        // Current without a request: back.
        var plain = new InitPage("plain");
        await Wait(region.PushAsync(NavigationTarget.Own<Page>(plain)));
        Require(await Wait(plain.Entry!.DismissAsync()) is NavigationResult<object>.Committed && region.Current == home,
            "Dismissing a current entry did not go back.");

        // Retained: the request ends, the entry stays.
        var retained = new InitPage("retained");
        var retainedRequest = region.PushForResult<int>(NavigationTarget.Own<Page>(retained));
        await Wait(retainedRequest.Transition);
        await Wait(region.PushAsync(NavigationTarget.Own(new Page("top"))));
        var notCurrent = await Wait(retained.Entry!.DismissAsync());
        Require(notCurrent is NavigationResult<object>.Rejected { Reason: NavigationRejection.NotCurrent }
            && await Wait(retainedRequest.Completion) is NavigationCompletion<int>.Dismissed && Names(region) == "home,retained,top",
            $"Dismissing a retained entry gave {notCurrent} and {Names(region)}.");

        // Current again with its request already dismissed: back.
        await Wait(region.BackAsync());
        Require(await Wait(retained.Entry.DismissAsync()) is NavigationResult<object>.Committed && Names(region) == "home",
            "Dismissing an entry whose request had ended did not go back.");

        // Retired: nothing.
        Require(await Wait(retained.Entry.DismissAsync()) is NavigationResult<object>.Rejected { Reason: NavigationRejection.NotCurrent },
            "Dismissing a retired entry did something.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(picker.Disposed == 1 && plain.Disposed == 1 && retained.Disposed == 1 && fixture.Navigator.UnretiredEntryCount == 1,
            "Dismissed entries were not retired once.");
    }

    private static async Task DismissFromInitializeAsync(bool forResult)
    {
        await using var fixture = new Fixture();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var page = new InitPage("pending");
        NavigationResult<object>? inner = null;
        var tokenCancelled = false;
        page.Initialize = async (entry, token) =>
        {
            // Returns at once: it must not await the push that awaits this hook.
            inner = await entry.DismissAsync(CancellationToken.None);
            tokenCancelled = token.IsCancellationRequested;
        };
        NavigationResultRequest<Page, int>? request = null;
        var pushed = forResult
            ? await Wait((request = region.PushForResult<int>(NavigationTarget.Own<Page>(page))).Transition)
            : await Wait(region.PushAsync(NavigationTarget.Own<Page>(page)));
        Require(inner is NavigationResult<object>.Rejected { Reason: NavigationRejection.Cancelled } && tokenCancelled
            && pushed is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled } && region.Current == home,
            $"Dismissing from InitializeAsync gave {inner} and {pushed}.");
        if (request is not null)
            Require(await Wait(request.Completion) is NavigationCompletion<int>.Dismissed, "The pending request was not dismissed.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(page.Disposed == 1 && fixture.Navigator.UnretiredEntryCount == 1, "The dismissed pending entry was not retired.");
    }

    private static async Task LeaveConfirmationAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var document = new Page("document");
        await Wait(region.PushAsync(NavigationTarget.Own(document)));
        var dirty = true;
        var answer = true;
        var veto = false;
        var outsideTurn = 0;
        var prompts = 0;
        var discards = 0;
        var leave = new LeaveConfirmation(
            () =>
            {
                if (!fixture.Context.IsExecuting) outsideTurn++;
                return dirty;
            },
            (_, _) =>
            {
                prompts++;
                return ValueTask.FromResult(answer);
            },
            () => discards++);
        document.Guard = async (departure, token) => await leave.CanDepartAsync(departure, token) && !veto;

        // A covering push retains the entry: no question.
        await Wait(region.PushAsync(NavigationTarget.Own(new Page("cover"))));
        await Wait(region.BackAsync());
        Require(prompts == 0 && region.Current == document, "A retaining departure asked.");

        // No: the entry stays, nothing is discarded.
        answer = false;
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard } && prompts == 1 && discards == 0,
            "A declined confirmation did not veto.");

        // Yes, but another guard vetoes: the yes ends with its transition; the next departure asks again.
        answer = true;
        veto = true;
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Rejected && prompts == 2 && discards == 0,
            "A vetoed yes discarded.");
        veto = false;
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Committed && prompts == 3 && discards == 1 && outsideTurn == 0,
            $"A confirmed departure asked {prompts} times, discarded {discards} times, read state outside turns {outsideTurn} times.");

        // No changes: no question.
        var clean = new Page("clean");
        await Wait(region.PushAsync(NavigationTarget.Own(clean)));
        dirty = false;
        clean.Guard = leave.CanDepartAsync;
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Committed && prompts == 3 && discards == 1, "A clean departure asked.");

        // askOnRetain asks for a covering push too.
        var strict = new Page("strict");
        await Wait(region.PushAsync(NavigationTarget.Own(strict)));
        var retainPrompts = 0;
        strict.Guard = new LeaveConfirmation(() => true, (_, _) => ValueTask.FromResult(++retainPrompts > 0), askOnRetain: true).CanDepartAsync;
        await Wait(region.PushAsync(NavigationTarget.Own(new Page("cover"))));
        Require(retainPrompts == 1, "askOnRetain did not ask for a retaining departure.");
    }

    // W240-001 deviation 14: a parent transition supersedes a child transition whose guard already
    // asked. The parent's transition runs the child's guard again; the standing yes answers it.
    private static async Task LeaveConfirmationParentRerunAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        ParentPage? parent = null;
        await Wait(region.PushAsync(NavigationTarget.Create<Page>(_ => parent = new ParentPage("parent", fixture.Navigator))));
        var document = new Page("document");
        await Wait(parent!.Child.PushAsync(NavigationTarget.Own(document)));
        var prompts = 0;
        var discards = 0;
        var leave = new LeaveConfirmation(() => true, (_, _) =>
        {
            Interlocked.Increment(ref prompts);
            return ValueTask.FromResult(true);
        }, () => Interlocked.Increment(ref discards));
        document.Guard = leave.CanDepartAsync;
        var resuming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        parent.ChildRoot.Resume = async (_, token) =>
        {
            resuming.TrySetResult();
            await Task.Delay(-1, token);
        };

        var childBack = parent.Child.BackAsync().AsTask();
        await Wait(resuming.Task);
        Require(prompts == 1 && discards == 0, "The child Back did not ask once before preparing.");
        var parentBack = await Wait(region.BackAsync());
        Require(parentBack is NavigationResult<Page>.Committed && await Wait(childBack) is NavigationResult<Page>.Superseded,
            $"The parent Back gave {parentBack}.");
        Require(prompts == 1 && discards == 1 && document.Departures.Count == 2,
            $"The rerun asked {prompts} times and discarded {discards} times over {document.Departures.Count} departures.");
    }

    // Competing departures of one entry: whichever transition commits discards exactly once. Backs admitted while
    // another is pending join it, so every committed result reports the same pop.
    private static async Task LeaveConfirmationRaceAsync(int seed)
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var document = new Page("document");
        var id = ((NavigationResult<Page>.Committed)await Wait(region.PushAsync(NavigationTarget.Own(document)))).Current!.Id;
        var random = new Random(seed);
        var prompts = 0;
        var discards = 0;
        var leave = new LeaveConfirmation(() => true, async (_, _) =>
        {
            Interlocked.Increment(ref prompts);
            await Task.Yield();
            return true;
        }, () => Interlocked.Increment(ref discards));
        document.Guard = leave.CanDepartAsync;
        // System.Random is not thread-safe: draw every value before the tasks start.
        var yields = Enumerable.Range(0, random.Next(2, 6)).Select(_ => random.Next(2) == 0).ToArray();
        var backs = yields.Select(yieldFirst => Task.Run(async () =>
        {
            if (yieldFirst) await Task.Yield();
            return await region.BackAsync(new NavigationRequestOptions(id));
        })).ToArray();
        var results = await Wait(Task.WhenAll(backs));
        var committed = results.OfType<NavigationResult<Page>.Committed>().ToArray();
        Require(committed.Length >= 1 && committed.All(result => result.Retired.SequenceEqual([id])) && region.Current is { Name: "home" }
            && document.Disposed == 1
            && Volatile.Read(ref discards) == 1 && Volatile.Read(ref prompts) is >= 1 and <= 5,
            $"seed {seed}: {string.Join(", ", results.Select(result => result.GetType().Name))}, {prompts} prompts, {discards} discards.");
    }

    private static async Task LeaveConfirmationInDialogAsync(bool complete)
    {
        await using var fixture = new Fixture();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var dialog = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        var document = new Page("document");
        await Wait(main.PushAsync(NavigationTarget.Own(document)));
        var discards = 0;
        InitPage? confirm = null;
        var leave = LeaveConfirmation.InDialog(dialog, () => NavigationTarget.Own<Page>(confirm = new InitPage("confirm")),
            () => true, () => discards++);
        document.Guard = leave.CanDepartAsync;
        var back = main.BackAsync().AsTask();
        await Until(() => confirm is not null && dialog.Current == confirm, "The dialog was not shown.");
        if (complete)
        {
            Require(await Wait(confirm!.Entry!.CompleteAsync(true)) is NavigationResult<object>.Committed, "The dialog did not complete.");
            Require(await Wait(back) is NavigationResult<Page>.Committed && discards == 1 && main.Current is { Name: "home" },
                "A confirmed dialog did not leave and discard.");
        }
        else
        {
            Require(await Wait(confirm!.Entry!.DismissAsync()) is NavigationResult<object>.Committed, "The dialog did not dismiss.");
            Require(await Wait(back) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard } && discards == 0
                && main.Current == document, "A dismissed dialog did not keep the entry.");
        }
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(dialog.Current is null && confirm!.Disposed == 1, "The dialog did not leave its region.");
    }

    // A Create entry's scope is disposed when its push is superseded during the factory, or
    // cancelled during initialize.
    private static async Task EntryScopeOfAbandonedPushAsync()
    {
        await using var provider = StandaloneServices();
        await using var fixture = new Fixture(services: provider, entryScopes: true);
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));

        Scoped? factoryScoped = null;
        Page? built = null;
        using var inFactory = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // Without a hook scheduler the factory may run in the caller's frame: push from the pool.
        var push = Task.Run(() => region.PushAsync(NavigationTarget.Create<Page>(services =>
        {
            factoryScoped = services.GetRequiredService<Scoped>();
            inFactory.Set();
            release.Wait(Timeout);
            return built = new Page("built");
        })).AsTask());
        Require(await Wait(Task.Run(() => inFactory.Wait(Timeout))), "The factory did not start.");
        var other = region.PushAsync(NavigationTarget.Own(new Page("other"))).AsTask();
        release.Set();
        var pushed = await Wait(push);
        var overtaking = await Wait(other);
        Require(pushed is NavigationResult<Page>.Superseded && overtaking is NavigationResult<Page>.Committed,
            $"The push was not superseded during its factory: {pushed}, {overtaking}.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(factoryScoped is { Disposed: 1 } && built is { Disposed: 1 }, "A push superseded during its factory leaked its scope.");

        var signal = provider.GetRequiredService<InitSignal>();
        using var cancel = new CancellationTokenSource();
        var initializing = region.PushAsync<ScopedInitPage>(cancellationToken: cancel.Token).AsTask();
        var page = await Wait(signal.Started.Task);
        await cancel.CancelAsync();
        Require(await Wait(initializing) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled },
            "The push was not cancelled during initialize.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(page.Scoped is { Disposed: 1, OwnerDisposedFirst: true } && page.Disposed == 1,
            "A push cancelled during initialize did not dispose its content, then its scope.");
    }

    // Closing the navigator while a factory runs. With a short close timeout the pending entry
    // retires before the factory returns, so the late content is disposed by the preparing path;
    // its disposal throws, and the scope is still disposed.
    private static async Task CloseDuringFactoryAsync(bool late)
    {
        await using var provider = StandaloneServices();
        var fixture = new Fixture(services: provider, entryScopes: true,
            closeTimeout: late ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(10));
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        Scoped? scoped = null;
        Page? built = null;
        using var inFactory = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // Without a hook scheduler the factory may run in the caller's frame: push from the pool.
        var push = Task.Run(() => region.PushAsync(NavigationTarget.Create<Page>(services =>
        {
            scoped = services.GetRequiredService<Scoped>();
            inFactory.Set();
            release.Wait(Timeout);
            built = new Page("built");
            if (late) built.OnDispose = () => throw new InvalidOperationException("dispose");
            return built;
        })).AsTask());
        Require(await Wait(Task.Run(() => inFactory.Wait(Timeout))), "The factory did not start.");
        var disposal = fixture.Navigator.DisposeAsync().AsTask();
        if (late) await Wait(disposal);
        else await Until(() => fixture.Navigator.IsClosed, "The navigator did not start closing.");
        release.Set();
        Require(await Wait(push) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Closed }, "A push closed during its factory committed.");
        await Wait(disposal);
        await Until(() => scoped is { Disposed: 1 } && built is { Disposed: 1 }, "Closing during the factory leaked the content or its scope.");
        if (late)
            await Until(() => fixture.Logs.All(1064).Any(entry => entry.State["Step"] as string == "Dispose"),
                "The late content's failing disposal was not logged.");
        Require(fixture.Navigator.UnretiredEntryCount == 0, "Closing during the factory left entries.");
        await fixture.Context.DisposeAsync();
    }

    private static async Task DismissEdgeCasesAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));

        // Current with a request, but its Back is vetoed: the request stays dismissed, the entry stays.
        var picker = new InitPage("picker") { Guard = (_, _) => ValueTask.FromResult(false) };
        var request = region.PushForResult<int>(NavigationTarget.Own<Page>(picker));
        await Wait(request.Transition);
        var vetoed = await Wait(picker.Entry!.DismissAsync());
        Require(vetoed is NavigationResult<object>.Rejected { Reason: NavigationRejection.Guard }
            && await Wait(request.Completion) is NavigationCompletion<int>.Dismissed && region.Current == picker,
            $"Dismissing an entry whose Back is vetoed gave {vetoed}.");
        picker.Guard = null;
        await Wait(region.BackAsync());

        // Pending, but its push was already superseded: the real outcome, not Cancelled.
        var pending = new InitPage("pending");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NavigationResult<object>? inner = null;
        pending.Initialize = async (entry, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(-1, token); }
            catch (OperationCanceledException) { }
            inner = await entry.DismissAsync(CancellationToken.None);
        };
        var push = region.PushAsync(NavigationTarget.Own<Page>(pending)).AsTask();
        await Wait(entered.Task);
        var other = await Wait(region.PushAsync(NavigationTarget.Own(new Page("other"))));
        Require(await Wait(push) is NavigationResult<Page>.Superseded && other is NavigationResult<Page>.Committed
            && inner is NavigationResult<object>.Superseded, $"Dismissing a superseded pending entry gave {inner}.");
    }

    // A later request of the same region supersedes a departure whose guard already said yes;
    // its own guard run reuses the yes.
    private static async Task LeaveConfirmationSameRegionSupersederAsync()
    {
        await using var fixture = new Fixture();
        var home = new Page("home");
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        var document = new Page("document");
        await Wait(region.PushAsync(NavigationTarget.Own(document)));
        var prompts = 0;
        var discards = 0;
        document.Guard = new LeaveConfirmation(() => true, (_, _) =>
        {
            Interlocked.Increment(ref prompts);
            return ValueTask.FromResult(true);
        }, () => Interlocked.Increment(ref discards)).CanDepartAsync;
        var resuming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumes = 0;
        home.Resume = async (_, token) =>
        {
            if (Interlocked.Increment(ref resumes) > 1) return;
            resuming.TrySetResult();
            await Task.Delay(-1, token);
        };
        var first = region.BackAsync().AsTask();
        await Wait(resuming.Task);
        // A Back with a cancellable token supersedes a pending Back; a plain one would join it.
        using var superseding = new CancellationTokenSource();
        var second = await Wait(region.BackAsync(cancellationToken: superseding.Token));
        Require(second is NavigationResult<Page>.Committed && await Wait(first) is NavigationResult<Page>.Superseded,
            $"The second Back gave {second}.");
        Require(prompts == 1 && discards == 1 && document.Departures.Count == 2,
            $"The superseder asked {prompts} times and discarded {discards} times over {document.Departures.Count} departures.");
    }

    // ---- Standalone helpers ----------------------------------------------

    private sealed class Clock;

    private sealed class Scoped : IDisposable
    {
        private int _disposed;
        public Page? Owner { get; set; }
        public int Disposed => Volatile.Read(ref _disposed);
        public bool OwnerDisposedFirst { get; private set; }

        public void Dispose()
        {
            OwnerDisposedFirst = Owner is { Disposed: 1 };
            Interlocked.Increment(ref _disposed);
        }
    }

    private sealed class BadScopeException() : Exception("bad scope");

    private sealed class BadScoped : IDisposable
    {
        public void Dispose() => throw new BadScopeException();
    }

    private sealed class NavigatorHolder
    {
        public RunicNavigator? Navigator { get; set; }
    }

    private sealed class ClockPage(Clock clock) : Page("clock")
    {
        public Clock Clock { get; } = clock;
    }

    private sealed class InputClockPage(Clock clock) : Page("input"), INavigationInitialize<string>
    {
        public Clock Clock { get; } = clock;
        public string? Input { get; private set; }

        public ValueTask InitializeAsync(NavigationEntryContext entry, string input, CancellationToken cancellationToken)
        {
            Input = input;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TwoCtorPage : Page
    {
        public TwoCtorPage() : base("default") { }

        [ActivatorUtilitiesConstructor]
        public TwoCtorPage(Clock clock) : base("marked") => Clock = clock;

        public Clock? Clock { get; }
    }

    private sealed class ResultPage(Clock clock) : Page("result"), INavigationInitialize
    {
        public Clock Clock { get; } = clock;
        public NavigationEntryContext? Entry { get; private set; }

        public ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
        {
            Entry = entry;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Transient : IDisposable
    {
        public int Disposed;
        public void Dispose() => Interlocked.Increment(ref Disposed);
    }

    private sealed class ScopedPage : Page
    {
        public ScopedPage(Scoped scoped, Transient transient) : base("scoped")
        {
            Scoped = scoped;
            Transient = transient;
            scoped.Owner = this;
        }

        public Scoped Scoped { get; }
        public Transient Transient { get; }
    }

    private sealed class FailingPage : Page, INavigationInitialize
    {
        public FailingPage(Scoped scoped) : base("failing")
        {
            Scoped = scoped;
            scoped.Owner = this;
        }

        public Scoped Scoped { get; }

        public ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("initialize");
    }

    private sealed class BadScopePage(BadScoped scoped) : Page("bad")
    {
        public BadScoped Scoped { get; } = scoped;
    }

    private sealed class InitSignal
    {
        public TaskCompletionSource<ScopedInitPage> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ScopedInitPage : Page, INavigationInitialize
    {
        private readonly InitSignal _signal;

        public ScopedInitPage(Scoped scoped, InitSignal signal) : base("init")
        {
            Scoped = scoped;
            scoped.Owner = this;
            _signal = signal;
        }

        public Scoped Scoped { get; }

        public async ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
        {
            _signal.Started.TrySetResult(this);
            await Task.Delay(-1, cancellationToken);
        }
    }

    // One holder type reused at two levels, each with its own initial target type.
    private sealed class RegionHolder
    {
        public RegionHolder(RunicNavigator navigator, INavigationTarget<Page> initial) => Region = navigator.CreateRegion(this, initial);

        public NavigationRegion<Page> Region { get; }
    }

    private sealed class LevelOnePage : Page
    {
        public LevelOnePage(RunicNavigator navigator) : base("one") => Inner = new RegionHolder(navigator, NavigationTarget.Create<LevelTwoPage>());

        public RegionHolder Inner { get; }
    }

    private sealed class LevelTwoPage(Clock clock) : Page("two")
    {
        public Clock Clock { get; } = clock;
    }

    private sealed class MainNavigation
    {
        public MainNavigation(RunicNavigator navigator) =>
            Region = navigator.CreateRegion<Page>(this, NavigationTarget.Create<HomePage>());

        public NavigationRegion<Page> Region { get; }
    }

    private sealed class HomePage(MainNavigation navigation) : Page("home")
    {
        public MainNavigation Navigation { get; } = navigation;
    }

    private sealed class ResettingNavigation
    {
        public ResettingNavigation(RunicNavigator navigator) => Region = navigator.CreateRegion<Page>(this);

        public NavigationRegion<Page> Region { get; }
    }

    private sealed class ResetHomePage(ResettingNavigation navigation) : Page("home")
    {
        public ResettingNavigation Navigation { get; } = navigation;
    }

    private sealed class ShellPage : Page
    {
        public ShellPage(RunicNavigator navigator) : base("shell") =>
            Child = navigator.CreateRegion<Page>(this, NavigationTarget.Create<ClockPage>());

        public NavigationRegion<Page> Child { get; }
    }
}
