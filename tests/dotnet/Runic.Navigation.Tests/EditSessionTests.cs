using System.ComponentModel;
using System.Runtime.CompilerServices;
using TUnit.Core;

namespace Runic.Navigation.Tests;

// C2: a form's baseline, IsDirty with notification, Accept and Revert, and the pairing with LeaveConfirmation.
internal sealed partial class NavigationTests
{
    private sealed class EditedNote : INotifyPropertyChanged
    {
        public string Title { get; set => Set(ref field, value); } = "Old";
        public string Body { get; set => Set(ref field, value); } = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set(ref string field, string value, [CallerMemberName] string? name = null)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new(name));
        }
    }

    [Test]
    public void EditSessionFollowsTheOwnerAndAcceptsAndReverts()
    {
        var note = new EditedNote();
        using var session = new RunicEditSession<(string Title, string Body)>(note, () => (note.Title, note.Body), saved => (note.Title, note.Body) = saved);
        var changes = new List<string>();
        session.PropertyChanged += (_, args) => changes.Add($"{args.PropertyName}={session.IsDirty}");
        Require(!session.IsDirty && session.Baseline == ("Old", ""), "A new session is dirty.");

        note.Title = "New";
        Require(session.IsDirty && session.Current == ("New", ""), "A change did not make the session dirty.");
        note.Title = "Old";
        Require(!session.IsDirty, "Changing the value back did not make the session clean.");
        note.Body = "Text";
        session.Accept();
        Require(!session.IsDirty && session.Baseline == ("Old", "Text"), "Accept did not make the current values the baseline.");

        note.Title = "Draft";
        session.Revert();
        Require(!session.IsDirty && note.Title == "Old" && note.Body == "Text", "Revert did not restore the baseline.");
        Require(changes.SequenceEqual(["IsDirty=True", "IsDirty=False", "IsDirty=True", "Baseline=True", "IsDirty=False", "IsDirty=True", "IsDirty=False"]),
            $"Notifications: {string.Join(", ", changes)}");

        // A save that normalized the values: the session compares with what was written.
        session.Accept(("Old", "Text "));
        Require(session.IsDirty, "Accept(baseline) did not compare the current values with it.");
    }

    [Test]
    public void EditSessionWithoutAnOwnerRefreshesAndDisposeStopsFollowing()
    {
        var note = new EditedNote();
        var session = new RunicEditSession<string>(null, () => note.Title, title => note.Title = title);
        note.Title = "New";
        Require(!session.IsDirty, "A session without an owner followed a change.");
        session.Refresh();
        Require(session.IsDirty, "Refresh did not compare.");
        session.Dispose();
        Require(Throws<ObjectDisposedException>(session.Revert) && Throws<ObjectDisposedException>(session.Accept), "A disposed session accepted or reverted.");

        var owned = new RunicEditSession<string>(note, () => note.Title, title => note.Title = title);
        owned.Dispose();
        note.Title = "Other";
        Require(!owned.IsDirty, "A disposed session followed its owner.");
    }

    [Test]
    public void AMutableSnapshotNeedsACopyAndAStructuralComparer()
    {
        var tags = new List<string> { "a" };
        // The same instance in the baseline and now: never dirty.
        var shared = new RunicEditSession<List<string>>(null, () => tags, saved => { });
        tags.Add("b");
        shared.Refresh();
        Require(!shared.IsDirty, "A shared mutable snapshot became dirty.");
        // A copy and a comparer of its items.
        var copied = new RunicEditSession<List<string>>(null, () => [.. tags], saved => { tags.Clear(); tags.AddRange(saved); }, new ItemsComparer());
        tags.Add("c");
        copied.Refresh();
        Require(copied.IsDirty, "A copied snapshot with a structural comparer did not become dirty.");
        copied.Revert();
        Require(!copied.IsDirty && tags.SequenceEqual(["a", "b"]), "Revert did not restore the copy.");
    }

    private sealed class ItemsComparer : IEqualityComparer<List<string>>
    {
        public bool Equals(List<string>? left, List<string>? right) => left is null ? right is null : right is not null && left.SequenceEqual(right);
        public int GetHashCode(List<string> value) => value.Count;
    }

    [Test]
    public async Task AcceptingWhatASaveWroteKeepsEditsMadeDuringTheSaveDirtyAsync()
    {
        var note = new EditedNote();
        using var session = new RunicEditSession<(string, string)>(note, () => (note.Title, note.Body), saved => (note.Title, note.Body) = saved);
        note.Title = "Saved";
        var submitted = (note.Title, note.Body);
        var store = new TaskCompletionSource();
        var save = Task.Run(async () => { await store.Task; session.Accept(submitted); });
        note.Title = "Typed during the save";
        store.SetResult();
        await save;
        Require(session.IsDirty && session.Baseline == ("Saved", ""), "Accept(submitted) took the edit made during the save as saved.");
    }

    [Test]
    public async Task EditSessionDrivesALeaveConfirmationAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var note = new EditedNote();
        using var session = new RunicEditSession<(string, string)>(note, () => (note.Title, note.Body), saved => (note.Title, note.Body) = saved);
        var prompts = 0;
        var leave = new LeaveConfirmation(() => session.IsDirty, (_, _) => { prompts++; return ValueTask.FromResult(true); }, session.Revert);

        var clean = new Page("clean") { Guard = leave.CanDepartAsync };
        await Wait(region.PushAsync(NavigationTarget.Own(clean)));
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Committed && prompts == 0, "A clean session asked.");

        var edited = new Page("edited") { Guard = leave.CanDepartAsync };
        await Wait(region.PushAsync(NavigationTarget.Own(edited)));
        note.Title = "Unsaved";
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Committed && prompts == 1, "A dirty session did not ask.");
        Require(note.Title == "Old" && !session.IsDirty, "The confirmed departure did not revert the session.");
    }

    [Test]
    public async Task PresentationsWriteTheirDraftsBeforeDepartureGuardsAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var note = new EditedNote();
        using var session = new RunicEditSession<string>(note, () => note.Title, title => note.Title = title);
        // A presentation whose user typed into a field that has not written yet: the flush writes it.
        var typed = "Typed, not yet written";
        using var registration = RunicDraftFlush.Register(fixture.Context, async _ =>
        {
            await Task.Yield();
            await fixture.Context.InvokeAsync(() => { if (typed is not null) note.Title = typed; typed = null; });
        });
        var prompts = 0;
        var leave = new LeaveConfirmation(() => session.IsDirty, (_, _) => { prompts++; return ValueTask.FromResult(false); });
        var edited = new Page("edited") { Guard = leave.CanDepartAsync };
        await Wait(region.PushAsync(NavigationTarget.Own(edited)));
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Rejected { Reason: NavigationRejection.Guard } && prompts == 1,
            "The guard did not see the draft that the presentation wrote first.");

        // Another model context's presentations are not asked.
        await using var other = new RunicModelContext();
        var otherAsked = 0;
        using var foreign = RunicDraftFlush.Register(other, _ => { otherAsked++; return ValueTask.CompletedTask; });
        session.Revert();
        await Wait(region.PushAsync(NavigationTarget.Own(new Page("again") { Guard = leave.CanDepartAsync })));
        typed = null;
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Committed && otherAsked == 0,
            "A departure flushed another model context's presentations.");

        // Registrations are removed by identity: the same delegate registered twice stays once after one removal.
        var calls = 0;
        Func<CancellationToken, ValueTask> counted = _ => { calls++; return ValueTask.CompletedTask; };
        var first = RunicDraftFlush.Register(other, counted);
        using var second = RunicDraftFlush.Register(other, counted);
        first.Dispose();
        first.Dispose();
        await RunicDraftFlush.FlushAsync(other);
        Require(calls == 1, $"Removing one registration of a delegate removed {2 - calls}.");

        // A flush that fails does not decide the departure.
        using var failing = RunicDraftFlush.Register(fixture.Context, _ => throw new InvalidOperationException("The page is gone."));
        session.Revert();
        await Wait(region.PushAsync(NavigationTarget.Own(new Page("last") { Guard = leave.CanDepartAsync })));
        Require(await Wait(region.BackAsync()) is NavigationResult<Page>.Committed && prompts == 1, "A failing flush blocked the departure.");
    }
}
