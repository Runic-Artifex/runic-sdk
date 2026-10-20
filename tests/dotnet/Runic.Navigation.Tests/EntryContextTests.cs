using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Core;

namespace Runic.Navigation.Tests;

// W250-033 (N3): constructor-injected NavigationEntryContext, INavigationAware and the entry's
// State/StateChanged (design notes W250-031 §4.6, W250-032 §3-§4).
internal sealed partial class NavigationTests
{
    // ---- Constructor injection ---------------------------------------------

    [Test]
    public async Task EntryContextInjectionAsync()
    {
        await using var provider = StandaloneServices();
        await using var fixture = new Fixture(services: provider);
        var clock = provider.GetRequiredService<Clock>();

        // An initial target: the entry is allocated before the content.
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Create<ContextPage>());
        var initial = (ContextPage)region.Current!;
        Require(initial.Entry.Id == region.CurrentEntry!.Id && initial.StateAtConstruction == NavigationEntryState.Pending
            && initial.Entry.State == NavigationEntryState.Active && initial.Clock == clock
            && ReferenceEquals(initial.ServicesAtConstruction, provider),
            "Create<T>() as an initial target did not receive its own pending entry's context.");

        await Wait(region.PushAsync<ContextPage>());
        Require(region.Current is ContextPage { StateAtConstruction: NavigationEntryState.Pending } pushed
            && pushed.Entry.Id == region.CurrentEntry!.Id && pushed.Entry.Id != initial.Entry.Id && pushed.Entry.State == NavigationEntryState.Active,
            "PushAsync<T>() did not inject the pushed entry's context.");

        var request = region.PushForResult<ContextPage, int>();
        Require(await Wait(request.Transition) is NavigationResult<Page>.Committed && region.Current is ContextPage,
            "PushForResult<T, TResult>() did not push.");
        var resultPage = (ContextPage)region.Current!;
        Require(resultPage.Entry.Id == region.CurrentEntry!.Id, "PushForResult<T, TResult>() injected another entry's context.");
        await Wait(resultPage.Entry.CompleteAsync(3));
        Require(await Wait(request.Completion) is NavigationCompletion<int>.Completed { Value: 3 },
            "The injected context did not complete its own PushForResult request.");

        await Wait(region.ReplaceAsync<ContextPage>());
        Require(region.Current is ContextPage replaced && replaced.Entry.Id == region.CurrentEntry!.Id,
            "ReplaceAsync<T>() did not inject the new entry's context.");
        await Wait(region.ResetAsync<ContextPage>());
        Require(region.Current is ContextPage reset && reset.Entry.Id == region.CurrentEntry!.Id && !region.CanGoBack,
            "ResetAsync<T>() did not inject the new entry's context.");

        // Typed input: the constructor and the initialize hook receive the same context.
        await Wait(region.PushAsync<InputContextPage, string>("typed"));
        Require(region.Current is InputContextPage { Input: "typed" } typed && ReferenceEquals(typed.Entry, typed.Initialized)
            && typed.Entry.Id == region.CurrentEntry!.Id,
            "PushAsync<T, TInput>() did not inject the context it initializes with.");
        // Both factory forms: the factory receives the new entry's context with the entry's provider.
        // Both factory forms: the provider the factory receives answers the context while it runs.
        await Wait(region.PushAsync(NavigationTarget.Create<Page>((services, entry) =>
            new ContextPage(entry, services.GetRequiredService<Clock>()))));
        Require(region.Current is ContextPage factory && factory.Entry.Id == region.CurrentEntry!.Id,
            "A Create(factory) target did not resolve its entry's context.");
        await Wait(region.PushAsync(NavigationTarget.Create<InputContextPage, string>((_, entry) =>
            new InputContextPage(entry), "factory")));
        Require(region.Current is InputContextPage { Input: "factory" } inputFactory && ReferenceEquals(inputFactory.Entry, inputFactory.Initialized)
            && inputFactory.Entry.Id == region.CurrentEntry!.Id,
            "A Create(factory, input) target did not resolve its entry's context.");

        var second = fixture.Navigator.CreateRegion<Page>(new object(), NavigationTarget.Create<Page>((services, entry) =>
            new ContextPage(entry, services.GetRequiredService<Clock>())));
        Require(second.Current is ContextPage initialFactory && initialFactory.Entry.Id == second.CurrentEntry!.Id,
            "A Create(factory) initial target did not resolve its entry's context.");
    }

    // A navigator without Services constructs from an empty provider that has no IsService check.
    [Test]
    public async Task EntryContextInjectionWithoutServicesAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Create<InputlessContextPage>());
        Require(region.Current is InputlessContextPage initial && initial.Entry.Id == region.CurrentEntry!.Id,
            "An initial target without services did not receive its context.");
        await Wait(region.PushAsync<InputContextPage, string>("bare"));
        Require(region.Current is InputContextPage { Input: "bare" } pushed && pushed.Entry.Id == region.CurrentEntry!.Id,
            "A push without services did not inject its context.");
    }

    [Test]
    public async Task EntryContextInjectionWithEntryScopesAsync()
    {
        await using var provider = StandaloneServices();
        await using var fixture = new Fixture(services: provider, entryScopes: true);
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Create<ScopedContextPage>());
        await Wait(region.PushAsync<ScopedContextPage>());
        var initial = (ScopedContextPage)region.History[0].Content;
        var pushed = (ScopedContextPage)region.Current!;
        Require(initial.ScopeMatched && pushed.ScopeMatched && !ReferenceEquals(initial.Scoped, pushed.Scoped)
            && ReferenceEquals(pushed.Entry.Services, region.CurrentEntry!.Services),
            "The context's Services was not the entry's scope while the content was constructed.");
    }

    [Test]
    public async Task EntryContextOutsideConstructionAsync()
    {
        // Without the registration, a provider that is not constructing content doesn't know the context.
        await using (var plain = StandaloneServices())
        {
            await using var plainFixture = new Fixture(services: plain);
            var plainRegion = plainFixture.Navigator.CreateRegion<Page>(plainFixture.Root, NavigationTarget.Create<ContextPage>());
            Require(plainRegion.CurrentEntry!.Services.GetService(typeof(NavigationEntryContext)) is null,
                "The entry's provider resolved a context after construction.");
        }

        await using var provider = StandaloneServices(services =>
        {
            services.AddRunicNavigation();
            services.AddTransient<NeedsEntry>();
        });
        await using var fixture = new Fixture(services: provider);
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));

        // A service resolved for the content can't receive the context, and the message says why.
        var failed = await Wait(region.PushAsync<NeedsEntryPage>());
        Require(failed is NavigationResult<Page>.Failed { Phase: NavigationPhase.Preparing, Error: InvalidOperationException { Message: var message } }
            && message.Contains("NavigationEntryContext can't be resolved from the container", StringComparison.Ordinal)
            && Names(region) == "home",
            $"A service that depends on the context gave {failed}.");
        Require(Throws<InvalidOperationException>(() => provider.GetService(typeof(NavigationEntryContext))),
            "The root provider resolved a NavigationEntryContext.");

        // The registration doesn't get in the way of the content's own constructor.
        await Wait(region.PushAsync<ContextPage>());
        Require(region.Current is ContextPage injected && injected.Entry.Id == region.CurrentEntry!.Id,
            "The registration replaced the injected context.");

        // A factory receives the context as its argument; the provider it receives is the entry's own and doesn't know it.
        IServiceProvider? seen = null;
        NavigationEntryContext? given = null;
        var resolvable = true;
        await Wait(region.PushAsync(NavigationTarget.Create<Page>((services, entry) =>
        {
            seen = services;
            given = entry;
            resolvable = !Throws<InvalidOperationException>(() => services.GetService(typeof(NavigationEntryContext)));
            return new Page("factory");
        })));
        Require(given is not null && given.Id == region.CurrentEntry!.Id && ReferenceEquals(region.CurrentEntry.Services, seen)
            && ReferenceEquals(seen, provider) && !resolvable,
            "A factory did not receive its entry's context next to the entry's own provider.");
    }

    // ---- INavigationAware and StateChanged ----------------------------------

    [Test]
    public async Task EntryLifecycleOrderAsync()
    {
        await using var fixture = new Fixture();
        var steps = new Steps();
        var model = fixture.Context;
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, Aware("a", steps, model));
        region.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(region.Current)) steps.Add(model.IsExecuting ? "region:Current" : "!region:Current");
        };
        // The initial entry arrives in CreateRegion, on the caller's thread, outside a turn.
        Require(steps.Take() == "!a:state Pending>Active Reset,!a:to Reset", $"The initial target arrived as {steps.Last}.");
        var a = (AwarePage)region.Current!;
        a.Guard = (departure, _) =>
        {
            departure.OnCommitted(() => steps.Add("a:committed"));
            return ValueTask.FromResult(true);
        };

        // Push: the departure's actions, the entry that left, the entry that arrived, then the region.
        await Wait(region.PushAsync(Aware("b", steps, model)));
        Require(steps.Take() == "a:committed,a:state Active>Retained Push,a:from Retain Push,b:state Pending>Active Push,b:to Push,region:Current",
            $"Push raised {steps.Last}.");
        Require(region.CurrentEntry!.State == NavigationEntryState.Active && ((AwarePage)region.Current!).StateInNotification == NavigationEntryState.Active
            && a.StateInNotification == NavigationEntryState.Retained,
            "A notification did not see the committed state.");

        await Wait(region.BackAsync());
        Require(steps.Take() == "b:state Active>Retired Back,b:from Retire Back,a:state Retained>Active Back,a:to Back,region:Current",
            $"Back raised {steps.Last}.");

        await Wait(region.PushAsync(Aware("c", steps, model)));
        await Wait(region.PushAsync(Aware("d", steps, model)));
        steps.Take();
        // A retained entry that retires changes state without a second OnNavigatedFrom.
        await Wait(region.BackToAsync(a.Entry.Id));
        Require(steps.Take() == "d:state Active>Retired BackTo,d:from Retire BackTo,c:state Retained>Retired BackTo,a:state Retained>Active BackTo,a:to BackTo,region:Current",
            $"BackTo raised {steps.Last}.");

        await Wait(region.ReplaceAsync(Aware("e", steps, model)));
        Require(steps.Take() == "a:committed,a:state Active>Retired Replace,a:from Retire Replace,e:state Pending>Active Replace,e:to Replace,region:Current",
            $"Replace raised {steps.Last}.");

        await Wait(region.PushAsync(Aware("g", steps, model)));
        steps.Take();
        await Wait(region.ResetAsync(Aware("f", steps, model)));
        Require(steps.Take() == "g:state Active>Retired Reset,g:from Retire Reset,e:state Retained>Retired Reset,f:state Pending>Active Reset,f:to Reset,region:Current",
            $"Reset raised {steps.Last}.");

        await Wait(region.ClearAsync());
        Require(steps.Take() == "f:state Active>Retired Clear,f:from Retire Clear,region:Current", $"Clear raised {steps.Last}.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        await Wait(model.InvokeAsync(() => { }));
        Require(steps.Take() == "", $"Retirement raised {steps.Last}.");
        Require(steps.Mismatches == 0, "A notification carried another entry's id.");
    }

    [Test]
    public async Task EntryNotificationsOnRegionCloseAsync()
    {
        var fixture = new Fixture();
        var steps = new Steps();
        var model = fixture.Context;
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, Aware("home", steps, model));
        var parent = new Page("parent");
        await Wait(main.PushAsync(NavigationTarget.Own(parent)));
        var child = fixture.Navigator.CreateRegion<Page>(parent, Aware("kid", steps, model));
        await Wait(child.PushAsync(Aware("top", steps, model)));
        steps.Take();

        // The parent's Back closes the child region: its current entry leaves in the clearing turn, with Clear.
        await Wait(main.BackAsync());
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(steps.Take() == "home:state Retained>Active Back,home:to Back,top:state Active>Retired Clear,top:from Retire Clear,kid:state Retained>Retired Clear",
            $"Closing the child region raised {steps.Last}.");

        // Navigator disposal clears the remaining regions in turns.
        await fixture.Navigator.DisposeAsync();
        Require(steps.Take() == "home:state Active>Retired Clear,home:from Retire Clear", $"Disposal raised {steps.Last}.");
        await model.DisposeAsync();
    }

    [Test]
    public async Task UncommittedTransitionsNeverArriveAsync()
    {
        await using var fixture = new Fixture();
        var steps = new Steps();
        var model = fixture.Context;
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, Aware("home", steps, model));
        steps.Take();
        var created = new ConcurrentQueue<InitAwarePage>();

        // Superseded: the first push is still initializing when the second one is admitted.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = region.PushAsync(InitAware("slow", steps, model, created, async token =>
        {
            started.TrySetResult();
            await never.Task.WaitAsync(token);
        }));
        await Wait(started.Task);
        var fast = await Wait(region.PushAsync(Aware("fast", steps, model)));
        Require(await Wait(slow) is NavigationResult<Page>.Superseded && fast is NavigationResult<Page>.Committed,
            "The second push did not supersede the first.");

        // Cancelled before the commit, and failed in preparation.
        using (var cancel = new CancellationTokenSource())
        {
            var cancelled = region.PushAsync(InitAware("cancelled", steps, model, created, async token =>
            {
                await cancel.CancelAsync();
                await never.Task.WaitAsync(token);
            }), cancellationToken: cancel.Token);
            Require(await Wait(cancelled) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled },
                "The cancelled push was not rejected.");
        }
        var failed = await Wait(region.PushAsync(InitAware("failed", steps, model, created,
            _ => throw new InvalidOperationException("initialize failed"))));
        Require(failed is NavigationResult<Page>.Failed { Phase: NavigationPhase.Preparing }, $"The failing push gave {failed}.");

        // Rejected by a guard: the factory never runs, and the current entry gets no OnNavigatedFrom.
        ((AwarePage)region.Current!).Guard = (_, _) => ValueTask.FromResult(false);
        var vetoed = await Wait(region.PushAsync(Aware("vetoed", steps, model)));
        Require(vetoed is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard }, $"The vetoed push gave {vetoed}.");

        await Wait(fixture.Navigator.WhenIdleAsync());
        // Each pending entry that didn't commit reports its retirement in a posted turn.
        await Until(() => steps.Count == 7, $"The uncommitted entries' retirements were not raised: {steps.Text}.");
        var raised = steps.Take().Split(',').Order(StringComparer.Ordinal);
        Require(string.Join(",", raised) == "cancelled:state Pending>Retired Push,failed:state Pending>Retired Push,"
            + "fast:state Pending>Active Push,fast:to Push,home:from Retain Push,home:state Active>Retained Push,slow:state Pending>Retired Push",
            $"Uncommitted transitions raised {string.Join(",", raised)}.");
        Require(created.Count == 3 && created.All(page => page.Arrivals == 0 && page.Exits == 0 && page.StateInNotification == NavigationEntryState.Retired),
            "An entry whose transition did not commit was navigated to or from.");
    }

    [Test]
    public async Task EntryNotificationFailuresAreLoggedAsync()
    {
        await using var fixture = new Fixture();
        var steps = new Steps();
        var model = fixture.Context;
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var pushed = await Wait(region.PushAsync(NavigationTarget.Create<Page>((_, entry) =>
        {
            var page = new AwarePage("throwing", steps, model, entry) { Throw = true };
            page.Entry.StateChanged += (_, _) => throw new InvalidOperationException("handler");
            page.Entry.StateChanged += (_, args) => steps.Add($"after:{args.State}");
            return page;
        })));
        Require(pushed is NavigationResult<Page>.Committed && region.Current is AwarePage { Name: "throwing" },
            $"A throwing notification undid the commit: {pushed}.");
        Require(steps.Take() == "throwing:state Pending>Active Push,after:Active,throwing:to Push",
            $"A throwing handler skipped the others: {steps.Last}.");
        var logged = fixture.Logs.All(1075).Select(entry => (string)entry.State["Notification"]!).Order(StringComparer.Ordinal).ToArray();
        Require(logged is ["OnNavigatedTo", "StateChanged"] && fixture.Logs.All(1075).All(entry => entry.Level == LogLevel.Error
            && (string)entry.State["Operation"]! == "Push" && (string)entry.State["EntryType"]! == nameof(AwarePage)),
            $"The failures were logged as {string.Join(", ", logged)}.");
    }

    // The README's Testing example: a ViewModel tested through a real navigator, without a UI host.
    [Test]
    public async Task ReadmeTestingThroughRealNavigatorAsync()
    {
        await using var services = StandaloneServices();
        await using var model = new RunicModelContext();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = model, Services = services });
        var dialog = navigator.CreateRegion<object>(this);

        var request = dialog.PushForResult<ConfirmDialog, bool>();
        Require((await Wait(request.Transition)).IsCommitted, "The dialog push did not commit.");
        await Wait(((ConfirmDialog)dialog.Current!).Confirm());

        Require((await Wait(request.Completion) as NavigationCompletion<bool>.Completed)?.Value == true && dialog.Current is null,
            "Confirm did not complete the request and go back.");
    }

    // ---- Entry context helpers ---------------------------------------------

    private sealed class ConfirmDialog(NavigationEntryContext entry)
    {
        public async Task Confirm() => await entry.CompleteAsync(true);
    }

    private static INavigationTarget<Page> Aware(string name, Steps steps, IRunicModelContext model) =>
        NavigationTarget.Create<Page>((_, entry) => new AwarePage(name, steps, model, entry));

    private static INavigationTarget<Page> InitAware(string name, Steps steps, IRunicModelContext model,
        ConcurrentQueue<InitAwarePage> created, Func<CancellationToken, Task> initialize) =>
        NavigationTarget.Create<Page>((_, entry) =>
        {
            var page = new InitAwarePage(name, steps, model, entry, initialize);
            created.Enqueue(page);
            return page;
        });

    // An ordered record of notifications; "!" marks one that ran outside a model turn.
    private sealed class Steps
    {
        private readonly List<string> _items = [];
        public string Last { get; private set; } = "";
        public int Mismatches;

        public void Add(string item) { lock (_items) _items.Add(item); }

        public int Count { get { lock (_items) return _items.Count; } }

        public string Text { get { lock (_items) return string.Join(",", _items); } }

        public string Take()
        {
            lock (_items)
            {
                Last = string.Join(",", _items);
                _items.Clear();
                return Last;
            }
        }
    }

    private class AwarePage : Page, INavigationAware
    {
        private readonly Steps _steps;
        private readonly IRunicModelContext _model;

        public AwarePage(string name, Steps steps, IRunicModelContext model, NavigationEntryContext entry) : base(name)
        {
            _steps = steps;
            _model = model;
            Entry = entry;
            entry.StateChanged += (sender, args) =>
            {
                if (!ReferenceEquals(sender, entry)) Interlocked.Increment(ref steps.Mismatches);
                Record($"{Name}:state {args.PreviousState}>{args.State} {args.Operation}");
            };
        }

        public NavigationEntryContext Entry { get; }
        public bool Throw { get; init; }
        public int Arrivals;
        public int Exits;
        // The entry's state in the last notification.
        public NavigationEntryState StateInNotification { get; private set; }

        public void OnNavigatedTo(NavigationArrival arrival)
        {
            Interlocked.Increment(ref Arrivals);
            if (arrival.Entry != Entry.Id) Interlocked.Increment(ref _steps.Mismatches);
            Record($"{Name}:to {arrival.Operation}");
            if (Throw) throw new InvalidOperationException("arrival");
        }

        public void OnNavigatedFrom(NavigationExit exit)
        {
            Interlocked.Increment(ref Exits);
            if (exit.Entry != Entry.Id) Interlocked.Increment(ref _steps.Mismatches);
            Record($"{Name}:from {exit.Kind} {exit.Operation}");
        }

        private void Record(string step)
        {
            StateInNotification = Entry.State;
            _steps.Add(_model.IsExecuting ? step : "!" + step);
        }
    }

    private sealed class InitAwarePage(string name, Steps steps, IRunicModelContext model, NavigationEntryContext entry,
        Func<CancellationToken, Task> initialize) : AwarePage(name, steps, model, entry), INavigationInitialize
    {
        public ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken) =>
            new(initialize(cancellationToken));
    }

    private sealed class ContextPage(NavigationEntryContext entry, Clock clock) : Page("context")
    {
        public NavigationEntryContext Entry { get; } = entry;
        public Clock Clock { get; } = clock;
        public NavigationEntryState StateAtConstruction { get; } = entry.State;
        public IServiceProvider ServicesAtConstruction { get; } = entry.Services;
    }

    private sealed class InputlessContextPage(NavigationEntryContext entry) : Page("bare")
    {
        public NavigationEntryContext Entry { get; } = entry;
    }

    private sealed class InputContextPage(NavigationEntryContext entry) : Page("input"), INavigationInitialize<string>
    {
        public NavigationEntryContext Entry { get; } = entry;
        public NavigationEntryContext? Initialized { get; private set; }
        public string? Input { get; private set; }

        public ValueTask InitializeAsync(NavigationEntryContext entry, string input, CancellationToken cancellationToken)
        {
            Initialized = entry;
            Input = input;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScopedContextPage : Page
    {
        public ScopedContextPage(NavigationEntryContext entry, Scoped scoped) : base("scoped")
        {
            Entry = entry;
            Scoped = scoped;
            ScopeMatched = ReferenceEquals(entry.Services.GetRequiredService<Scoped>(), scoped);
        }

        public NavigationEntryContext Entry { get; }
        public Scoped Scoped { get; }
        public bool ScopeMatched { get; }
    }

    private sealed class NeedsEntry(NavigationEntryContext entry)
    {
        public NavigationEntryContext Entry { get; } = entry;
    }

    private sealed class NeedsEntryPage(NeedsEntry needs) : Page("needs")
    {
        public NeedsEntry Needs { get; } = needs;
    }
}
