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

    // I-030: the base region's BackToAsync and ClearHistoryAsync, for hosts typed NavigationRegion, behave like the typed
    // members (OperationsAsync), which hide them with the same parameters.
    [Test]
    public async Task BaseRegionBackToAndClearHistoryMatchTheTypedRegionAsync()
    {
        foreach (var name in new[] { nameof(NavigationRegion.BackToAsync), nameof(NavigationRegion.ClearHistoryAsync) })
        {
            var baseMethod = typeof(NavigationRegion).GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            var typedMethod = typeof(NavigationRegion<Page>).GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Require(baseMethod is { ReturnType: var baseReturn } && baseReturn == typeof(ValueTask<NavigationResult<object>>)
                    && typedMethod is { ReturnType: var typedReturn } && typedReturn == typeof(ValueTask<NavigationResult<Page>>)
                    && baseMethod.GetParameters().Select(parameter => (parameter.ParameterType, parameter.Name, parameter.HasDefaultValue))
                        .SequenceEqual(typedMethod.GetParameters().Select(parameter => (parameter.ParameterType, parameter.Name, parameter.HasDefaultValue))),
                $"The base region's {name} does not mirror the typed member.");
        }

        await using var fixture = new Fixture();
        var a = new Page("a");
        var typed = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(a));
        NavigationRegion region = typed;
        var b = new Page("b");
        var c = new Page("c");
        var d = new Page("d");
        await Wait(typed.PushAsync(NavigationTarget.Own(b)));
        var bId = typed.CurrentEntry!.Id;
        await Wait(typed.PushAsync(NavigationTarget.Own(c)));
        await Wait(typed.PushAsync(NavigationTarget.Own(d)));

        var backTo = await Wait(region.BackToAsync(bId));
        Require(backTo is NavigationResult<object>.Committed { Retired: [{ Value: 4 }, { Value: 3 }], Current: { Id: var backToId, Content: var backToContent } }
                && backToId == bId && ReferenceEquals(backToContent, b) && Names(typed) == "a,b"
                && d.Disposed == 1 && c.Disposed == 1 && b.Resumed == 1,
            $"The base region's BackTo did not retire top-down: {backTo} {Names(typed)}");
        var same = await Wait(region.BackToAsync(bId));
        Require(same is NavigationResult<object>.Committed { Retired.Count: 0 } && Names(typed) == "a,b",
            "The base region's BackTo the current entry changed history.");
        var missing = await Wait(region.BackToAsync(new NavigationEntryId(99)));
        Require(missing is NavigationResult<object>.Rejected { Reason: NavigationRejection.EntryNotFound },
            $"The base region's BackTo a missing entry gave {missing}.");
        var stale = await Wait(region.BackToAsync(typed.History[0].Id, new() { ExpectedCurrent = new NavigationEntryId(99) }));
        Require(stale is NavigationResult<object>.Rejected { Reason: NavigationRejection.NotCurrent } && Names(typed) == "a,b",
            $"The base region's BackTo ignored its options: {stale}.");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Require(await Wait(region.BackToAsync(typed.History[0].Id, cancellationToken: cancelled.Token))
                    is NavigationResult<object>.Rejected { Reason: NavigationRejection.Cancelled } && Names(typed) == "a,b",
                "The base region's BackTo ignored its cancellation token.");
            Require(await Wait(region.ClearHistoryAsync(cancellationToken: cancelled.Token))
                    is NavigationResult<object>.Rejected { Reason: NavigationRejection.Cancelled } && Names(typed) == "a,b",
                "The base region's ClearHistory ignored its cancellation token.");
        }

        var cleared = await Wait(region.ClearHistoryAsync());
        Require(cleared is NavigationResult<object>.Committed { Retired: [{ Value: 1 }], Current.Content: var current }
                && ReferenceEquals(current, b) && Names(typed) == "b" && !region.CanGoBack && a.Disposed == 0,
            $"The base region's ClearHistory did not keep only the current entry: {cleared} {Names(typed)}");
        await Wait(fixture.Navigator.WhenIdleAsync());
    }

    // #648: the selection is a sealed class that only the navigator creates, with the approved members; Select is a new
    // value of the open NavigationOperation set, and the logs name it.
    [Test]
    public void SelectionSurfaceIsTheApprovedOne()
    {
        var type = typeof(NavigationSelection);
        Require(type.IsSealed && typeof(INotifyPropertyChanged).IsAssignableFrom(type)
                && type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).All(constructor => constructor.IsAssembly),
            "NavigationSelection can be created or derived from outside Runic.Navigation.");
        var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(member => member is not MethodInfo { IsSpecialName: true }).Select(member => member.Name).Order(StringComparer.Ordinal);
        Require(members.SequenceEqual(["Add", "Admission", "IsBusy", "IsTransitioning", "Navigator", "PropertyChanged", "Regions", "RemoveAsync",
                "SelectAsync", "Selected", "SelectedIndex", "ToString", "WhenIdleAsync"]),
            $"NavigationSelection has the members {string.Join(", ", members)}.");
        Require(type.GetProperty(nameof(NavigationSelection.Regions))?.PropertyType == typeof(IReadOnlyList<NavigationRegion>)
                && type.GetProperty(nameof(NavigationSelection.Selected))?.PropertyType == typeof(NavigationRegion)
                && type.GetMethod(nameof(NavigationSelection.SelectAsync))?.ReturnType == typeof(ValueTask<NavigationResult<object>>)
                && type.GetMethod(nameof(NavigationSelection.RemoveAsync))?.GetParameters().Select(parameter => parameter.ParameterType)
                    .SequenceEqual([typeof(NavigationRegion), typeof(NavigationRequestOptions), typeof(CancellationToken)]) == true,
            "NavigationSelection's members don't have the approved types.");
        var create = typeof(RunicNavigator).GetMethod(nameof(RunicNavigator.CreateSelection));
        Require(create?.ReturnType == type && create.GetParameters().Select(parameter => (parameter.ParameterType, parameter.Name))
                .SequenceEqual([(typeof(object), "owner"), (typeof(IEnumerable<NavigationRegion>), "regions"), (typeof(NavigationSelectionOptions), "options")]),
            "CreateSelection does not have the approved signature.");
        var options = new NavigationSelectionOptions();
        Require(typeof(NavigationSelectionOptions).IsSealed && options.SelectedIndex == 0 && options.Admission == NavigationAdmission.Supersede
                && options == new NavigationSelectionOptions(),
            "NavigationSelectionOptions is not a sealed record with the default index and admission.");
        Require((int)NavigationOperation.Select == 7 && RunicNavigator.OperationName(NavigationOperation.Select) == "Select"
                && RunicNavigator.OperationName(NavigationOperation.Clear) == "Clear",
            "Select is not the next NavigationOperation, or the logs don't name it.");
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
        Require(started.Wait(TestTimeouts.Scale(TimeSpan.FromSeconds(5))), "The blocking turn did not start.");
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
