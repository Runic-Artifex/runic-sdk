using TUnit.Core;

namespace Runic.Navigation.Tests;

// W260-009 (wpf-migration F13): leaving borrowed content, as a tab switch leaves its tab, asks the current page of every
// region the content owns and of the regions below them, as retained departures. The regions keep their entries, and a
// guard that keeps its page keeps the borrowed content current.
internal sealed partial class NavigationTests
{
    [Test]
    public async Task TabSwitchAsksKeptChildRegionsAsync()
    {
        await using var fixture = new Fixture();
        var customers = new ParentPage("customers", fixture.Navigator);
        var reports = new Page("reports");
        var tabs = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow<Page>(customers));
        ParentPage? editor = null;
        await Wait(customers.Child.PushAsync(NavigationTarget.Create<Page>(_ => editor = new ParentPage("editor", fixture.Navigator))));
        var detail = new Page("detail");
        await Wait(editor!.Child.PushAsync(NavigationTarget.Own(detail)));
        var editorEntry = customers.Child.CurrentEntry!;
        var dirty = true;
        var asked = new Journal();
        detail.Guard = (departure, _) => Answer(asked, "detail", departure, true);
        editor.Guard = (departure, _) => Answer(asked, "editor", departure, !dirty);
        customers.Guard = (departure, _) => Answer(asked, "customers", departure, true);
        reports.Guard = (departure, _) => Answer(asked, "reports", departure, true);

        // Keep: the dirty page vetoes, and the tab stays selected with every page.
        var kept = await Wait(tabs.ReplaceAsync(NavigationTarget.Borrow<Page>(reports)));
        Require(kept is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard, By: var by } && by == editorEntry.Id,
            $"A dirty child page did not keep the tab: {kept}.");
        Require(asked.Text == "detail Retain Replace,editor Retain Replace", $"The guards were asked as {asked.Text}.");
        Require(tabs.Current == customers && Names(customers.Child) == "root,editor" && Names(editor.Child) == "root,detail",
            "A kept tab switch changed a region.");

        // Allow: the switch commits, deepest page first, and the tab's regions keep their entries and content.
        dirty = false;
        var switched = await Wait(tabs.ReplaceAsync(NavigationTarget.Borrow<Page>(reports)));
        Require(switched is NavigationResult<Page>.Committed && tabs.Current == reports, $"The allowed tab switch gave {switched}.");
        Require(asked.Text == "detail Retain Replace,editor Retain Replace,detail Retain Replace,editor Retain Replace,customers Retire Replace",
            $"The guards were asked as {asked.Text}.");
        Require(Names(customers.Child) == "root,editor" && Names(editor.Child) == "root,detail" && ReferenceEquals(customers.Child.CurrentEntry, editorEntry)
            && editor.Disposed == 0 && detail.Disposed == 0 && customers.Disposed == 0,
            "Leaving the tab changed its regions or disposed their pages.");

        // Leaving a tab without regions asks only the tab, and coming back finds the kept pages.
        var back = await Wait(tabs.ReplaceAsync(NavigationTarget.Borrow<Page>(customers)));
        Require(back is NavigationResult<Page>.Committed && asked.Text.EndsWith(",customers Retire Replace,reports Retire Replace", StringComparison.Ordinal)
            && customers.Child.Current == editor && editor.Child.Current == detail, $"Returning to the tab gave {back}, asked {asked.Text}.");
    }

    // Every departure that retires borrowed content asks its regions, not only a replace; regions that are empty or closed
    // are skipped, and a region of the root that a tab only refers to is not the tab's and is not asked.
    [Test]
    public async Task BorrowedRetirementAsksOnlyItsLiveRegionsAsync()
    {
        await using var fixture = new Fixture();
        var asked = new Journal();
        var tab = new ParentPage("tab", fixture.Navigator);
        var empty = fixture.Navigator.CreateRegion<Page>(tab);
        var shared = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("shared")));
        ((Page)shared.Current!).Guard = (departure, _) => Answer(asked, "shared", departure, false);
        tab.ChildRoot.Guard = (departure, _) => Answer(asked, "root", departure, true);
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        await Wait(region.PushAsync(NavigationTarget.Borrow<Page>(tab)));
        var back = await Wait(region.BackAsync());
        Require(back is NavigationResult<Page>.Committed && asked.Text == "root Retain Back" && empty.Current is null,
            $"Going back from borrowed content gave {back}, asked {asked.Text}.");
        await Wait(region.PushAsync(NavigationTarget.Borrow<Page>(tab)));
        var clear = await Wait(region.ClearAsync());
        Require(clear is NavigationResult<Page>.Committed && asked.Text == "root Retain Back,root Retain Clear" && tab.Child.Current == tab.ChildRoot,
            $"Clearing borrowed content gave {clear}, asked {asked.Text}.");
    }

    // As for any other guard: a cancelled or superseded tab switch cancels the child page's guard and keeps the tab.
    [Test]
    public async Task TabSwitchChildGuardCancellationAsync()
    {
        await using var fixture = new Fixture();
        var customers = new ParentPage("customers", fixture.Navigator);
        var tabs = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow<Page>(customers));
        var editor = new Page("editor");
        await Wait(customers.Child.PushAsync(NavigationTarget.Own(editor)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new List<CancellationToken>();
        editor.Guard = async (_, token) =>
        {
            lock (tokens) tokens.Add(token);
            started.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
            return true;
        };

        using (var cancellation = new CancellationTokenSource())
        {
            var switching = tabs.ReplaceAsync(NavigationTarget.Borrow<Page>(new Page("reports")), cancellationToken: cancellation.Token).AsTask();
            await Wait(started.Task);
            cancellation.Cancel();
            var cancelled = await Wait(switching);
            Require(cancelled is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Cancelled } && tabs.Current == customers
                && customers.Child.Current == editor && tokens is [{ IsCancellationRequested: true }],
                $"A cancelled tab switch gave {cancelled} or left its child guard running.");
        }

        started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = tabs.ReplaceAsync(NavigationTarget.Borrow<Page>(new Page("reports"))).AsTask();
        await Wait(started.Task);
        editor.Guard = (_, _) => ValueTask.FromResult(false);
        var second = await Wait(tabs.ReplaceAsync(NavigationTarget.Borrow<Page>(new Page("settings"))));
        var superseded = await Wait(first);
        Require(superseded is NavigationResult<Page>.Superseded && tokens is [_, { IsCancellationRequested: true }],
            $"A later tab switch did not supersede the first one's child guard: {superseded}.");
        Require(second is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard } && tabs.Current == customers
            && customers.Child.Current == editor, $"The later tab switch gave {second}.");
    }

    // A commit in a kept region while the tab switch asks supersedes the switch: the answer was for another page.
    [Test]
    public async Task KeptRegionCommitSupersedesTabSwitchAsync()
    {
        await using var fixture = new Fixture();
        var customers = new ParentPage("customers", fixture.Navigator);
        var tabs = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow<Page>(customers));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        customers.Guard = async (_, _) =>
        {
            started.TrySetResult();
            return await gate.Task;
        };
        var switching = tabs.ReplaceAsync(NavigationTarget.Borrow<Page>(new Page("reports"))).AsTask();
        await Wait(started.Task);
        var pushed = await Wait(customers.Child.PushAsync(NavigationTarget.Own(new Page("editor"))));
        Require(pushed is NavigationResult<Page>.Committed, $"A push into the tab's region during the switch gave {pushed}.");
        gate.SetResult(true);
        var switched = await Wait(switching);
        Require(switched is NavigationResult<Page>.Superseded && tabs.Current == customers && Names(customers.Child) == "root,editor",
            $"A commit in the tab's region did not supersede the tab switch: {switched}.");
    }

    private static ValueTask<bool> Answer(Journal asked, string name, NavigationDeparture departure, bool allow)
    {
        asked.Add($"{name} {departure.Kind} {departure.Operation}");
        return ValueTask.FromResult(allow);
    }
}
