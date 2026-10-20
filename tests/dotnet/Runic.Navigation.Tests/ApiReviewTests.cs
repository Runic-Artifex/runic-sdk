using System.ComponentModel;
using System.Reflection;
using TUnit.Core;

namespace Runic.Navigation.Tests;

// W319-003 B4: the 1.0 API review of Runic.Navigation (D24 vocabulary, D32 nav-04 base classes,
// D38 nav-06 event shape, nav-10, section 4 row 7 nav-14).
internal sealed partial class NavigationTests
{
    // nav-04: the content-type-independent region and entry are abstract classes that only the navigator can create,
    // like Task and Task<T>, so members can be added in a minor release.
    [Test]
    public void RegionAndEntryAreClosedBaseClasses()
    {
        var assembly = typeof(RunicNavigator).Assembly;
        Require(assembly.GetType("Runic.Navigation.INavigationRegion") is null && assembly.GetType("Runic.Navigation.INavigationEntry") is null,
            "INavigationRegion or INavigationEntry is still public.");
        foreach (var (baseType, typed) in new[] { (typeof(NavigationRegion), typeof(NavigationRegion<>)), (typeof(NavigationEntry), typeof(NavigationEntry<>)) })
        {
            Require(baseType.IsAbstract && typed.BaseType == baseType && typed.IsSealed,
                $"{typed.Name} does not derive from the abstract {baseType.Name}.");
            var constructors = baseType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(constructor => constructor.IsPublic || constructor.IsFamily || constructor.IsFamilyOrAssembly);
            Require(!constructors.Any(), $"{baseType.Name} can be derived from outside Runic.Navigation.");
        }
        Require(typeof(NavigationRegion).GetMethod(nameof(NavigationRegion.ReplaceBorrowedAsync))?
                .GetCustomAttribute<EditorBrowsableAttribute>()?.State == EditorBrowsableState.Never,
            "ReplaceBorrowedAsync is not hidden from IntelliSense (nav-10).");
        Require(typeof(NavigationRegionDeparture).GetProperty(nameof(NavigationRegionDeparture.Region))?.PropertyType == typeof(NavigationRegion),
            "NavigationRegionDeparture.Region is not typed as the base region.");
    }

    // nav-04: the base region exposes the region's own typed entries, and the region's notifications.
    [Test]
    public async Task BaseRegionPresentsTheTypedRegion()
    {
        await using var fixture = new Fixture();
        var home = new Page("home");
        var typed = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(home));
        NavigationRegion region = typed;
        var changes = new List<(object? Sender, string? Name)>();
        region.PropertyChanged += (sender, args) => changes.Add((sender, args.PropertyName));
        await Wait(typed.PushAsync(NavigationTarget.Own(new Page("document"))));

        Require(region.ContentType == typeof(Page) && ReferenceEquals(region.Current, typed.Current)
            && ReferenceEquals(region.CurrentEntry, typed.CurrentEntry) && region.CurrentEntry is NavigationEntry<Page>
            && region.History.Count == 1 && ReferenceEquals(region.History[0], typed.History[0])
            && ReferenceEquals(region.History[0].Content, home) && region.CanGoBack && ReferenceEquals(region.Navigator, fixture.Navigator),
            "The base region does not present the typed region's state and entries.");
        Require(changes.Contains((typed, nameof(NavigationRegion.Current))) && changes.Contains((typed, nameof(NavigationRegion.CurrentEntry))),
            $"The base region's PropertyChanged did not report the push: {string.Join(", ", changes.Select(change => change.Name))}.");

        Require(Throws<ArgumentException>(() => _ = region.ReplaceBorrowedAsync(new object()).AsTask()), "ReplaceBorrowedAsync accepted foreign content.");
        var other = new Page("other");
        Require(await Wait(region.ReplaceBorrowedAsync(other)) is NavigationResult<object>.Committed { Current.Content: var replaced }
                && ReferenceEquals(replaced, other) && ReferenceEquals(typed.Current, other)
                && typed.CurrentEntry is { Ownership: NavigationOwnership.Borrowed },
            "ReplaceBorrowedAsync did not replace the entry with borrowed content.");
        Require(await Wait(region.BackAsync()) is NavigationResult<object>.Committed && ReferenceEquals(typed.Current, home),
            "The base region's BackAsync did not go back.");
        await Wait(fixture.Navigator.WhenIdleAsync());
    }

    // D24: one vocabulary for the guard verb, the arrival record and the arrival hook.
    [Test]
    public void NavigationVocabularyIsArrivalAndDeparture()
    {
        var assembly = typeof(RunicNavigator).Assembly;
        foreach (var removed in new[] { "INavigationActivated", "INavigationActivated`1", "NavigationResume", "NavigationActivationState" })
            Require(assembly.GetType("Runic.Navigation." + removed) is null, $"{removed} is still public.");
        Require(typeof(INavigationRegionGuard).GetMethod("CanDepartAsync") is not null && typeof(INavigationRegionGuard).GetMethod("CanLeaveAsync") is null,
            "INavigationRegionGuard does not use the departure verb.");
        Require(typeof(INavigationArrived).GetMethod(nameof(INavigationArrived.OnArrivedAsync))?.GetParameters()
                .Select(parameter => parameter.ParameterType).SequenceEqual([typeof(NavigationEntryContext), typeof(NavigationArrival), typeof(CancellationToken)]) == true,
            "INavigationArrived.OnArrivedAsync does not take (entry, arrival, token).");
        Require(typeof(INavigationAware).GetMethod(nameof(INavigationAware.OnNavigatedTo))?.GetParameters().Single().ParameterType == typeof(NavigationArrival)
            && typeof(INavigationResume).GetMethod(nameof(INavigationResume.ResumeAsync))?.GetParameters()[0].ParameterType == typeof(NavigationArrival),
            "OnNavigatedTo or ResumeAsync do not receive a NavigationArrival.");
        Require(typeof(NavigationOutcome).GetProperty("Arrival")?.PropertyType == typeof(NavigationArrivalState)
            && typeof(NavigationResult<object>.Committed).GetProperty("ArrivalError") is not null
            && typeof(NavigationOutcome).GetProperty("Activation") is null,
            "The results still report activation.");
    }

    // nav-06: the unhandled-turn event follows the EventHandler pattern, with the context as sender, and tells a posted
    // turn that threw from one that the context dropped.
    [Test]
    public async Task UnhandledTurnExceptionNamesTheContextAndDrops()
    {
        Require(typeof(IRunicModelContext).GetEvent(nameof(IRunicModelContext.UnhandledTurnException))?.EventHandlerType
                == typeof(EventHandler<RunicModelTurnExceptionEventArgs>),
            "UnhandledTurnException is not an EventHandler<RunicModelTurnExceptionEventArgs>.");
        var context = new RunicModelContext();
        var reported = new List<(object? Sender, RunicModelTurnExceptionEventArgs Args)>();
        context.UnhandledTurnException += (sender, args) => { lock (reported) reported.Add((sender, args)); };
        Require(context.TryPost(() => throw new FormatException("posted")), "The failing turn was rejected.");
        await context.InvokeAsync(() => { });
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Require(context.TryPost(() => { started.Set(); release.Wait(); }), "The blocking turn was rejected.");
        Require(started.Wait(TimeSpan.FromSeconds(5)), "The blocking turn did not start.");
        Require(context.TryPost(() => { }), "The queued turn was rejected before shutdown.");
        var disposing = context.DisposeAsync().AsTask();
        release.Set();
        await disposing;
        lock (reported)
            Require(reported.Count == 2
                && reported[0] is { Args: { Exception: FormatException, Dropped: false } } && ReferenceEquals(reported[0].Sender, context)
                && reported[1] is { Args: { Exception: ObjectDisposedException, Dropped: true } } && ReferenceEquals(reported[1].Sender, context),
                $"The reports were {string.Join(", ", reported.Select(report => $"{report.Args.Exception.GetType().Name}/{report.Args.Dropped}"))}.");
        Require(Throws<ArgumentNullException>(() => _ = new RunicModelTurnExceptionEventArgs(null!, dropped: false)),
            "The event data accepted a null exception.");
    }

    // nav-14: InDialog takes askOnRetain like the constructor, so a push that only retains the entry asks too.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task LeaveConfirmationInDialogAsksOnRetain(bool askOnRetain)
    {
        await using var fixture = new Fixture();
        var main = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var dialog = fixture.Navigator.CreateRegion<Page>(fixture.Root);
        var document = new Page("document");
        await Wait(main.PushAsync(NavigationTarget.Own(document)));
        var discards = 0;
        InitPage? confirm = null;
        document.Guard = LeaveConfirmation.InDialog(dialog, () => NavigationTarget.Own<Page>(confirm = new InitPage("confirm")),
            () => true, () => discards++, askOnRetain).CanDepartAsync;
        var push = main.PushAsync(NavigationTarget.Own(new Page("details"))).AsTask();
        if (askOnRetain)
        {
            await Until(() => confirm is not null && dialog.Current == confirm, "The dialog was not shown for a retaining push.");
            Require(await Wait(confirm!.Entry!.CompleteAsync(true)) is NavigationResult<object>.Committed, "The dialog did not complete.");
        }
        Require(await Wait(push) is NavigationResult<Page>.Committed && main.Current is { Name: "details" },
            "The retaining push did not commit.");
        Require((confirm is not null) == askOnRetain && discards == (askOnRetain ? 1 : 0),
            $"askOnRetain {askOnRetain} asked {(confirm is null ? "no" : "a")} dialog and discarded {discards} times.");
        await Wait(fixture.Navigator.WhenIdleAsync());
    }
}
