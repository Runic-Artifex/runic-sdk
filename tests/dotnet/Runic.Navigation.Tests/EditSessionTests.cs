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
}
