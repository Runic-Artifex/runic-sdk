using TUnit.Core;

namespace Runic.Navigation.Tests;

// I-031 (0.11 B5): NavigationRequestOptions.RetainView decides per entry whether a presentation keeps the entry's view
// while it is retained. The navigator only records it on the entry the request creates; the entry's lifetime is unchanged,
// also when a presentation holds back the release of retired content (B6, RunicOperationRetirement.Cancel and Drain).
internal sealed partial class NavigationTests
{
    [Test]
    public async Task RetainViewIsRecordedOnTheEntryTheRequestCreatesAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        Require(region.CurrentEntry!.RetainView is null, "An initial entry has a RetainView value.");
        Require(new NavigationRequestOptions().RetainView is null, "RetainView is not null by default.");

        var keep = new NavigationRequestOptions { RetainView = true };
        var rebuild = new NavigationRequestOptions { RetainView = false };
        await Wait(region.PushAsync(NavigationTarget.Own(new Page("kept")), keep));
        var kept = region.CurrentEntry!;
        await Wait(region.PushAsync(NavigationTarget.Own(new Page("rebuilt")), rebuild));
        var rebuilt = region.CurrentEntry!;
        await Wait(region.PushAsync(NavigationTarget.Own(new Page("default"))));
        var followed = region.CurrentEntry!;
        Require(kept.RetainView == true && rebuilt.RetainView == false && followed.RetainView is null,
            $"Push recorded {kept.RetainView}, {rebuilt.RetainView}, {followed.RetainView}.");
        Require(region.History.Select(entry => entry.RetainView).SequenceEqual([null, true, false]),
            "The retained entries lost their RetainView values.");

        // A request that creates no entry ignores it and changes no entry's value.
        var back = await Wait(region.BackAsync(rebuild with { RetainView = true }));
        Require(back is NavigationResult<Page>.Committed && region.CurrentEntry == rebuilt && rebuilt.RetainView == false,
            $"Back with RetainView gave {back} and {rebuilt.RetainView}.");
        await Wait(region.BackToAsync(kept.Id, rebuild));
        Require(region.CurrentEntry == kept && kept.RetainView == true, "BackTo changed the entry's RetainView.");

        // Replace, Reset and PushForResult record it like Push; the typed and object views of an entry agree.
        await Wait(region.ReplaceAsync(NavigationTarget.Own(new Page("replaced")), keep));
        Require(region.CurrentEntry!.RetainView == true, "Replace did not record RetainView.");
        var request = region.PushForResult<int>(NavigationTarget.Own(new Page("result")), rebuild);
        await Wait(request.Transition);
        Require(region.CurrentEntry!.RetainView == false, "PushForResult did not record RetainView.");
        await Wait(region.ResetAsync(NavigationTarget.Own(new Page("reset")), keep));
        NavigationEntry untyped = region.CurrentEntry!;
        Require(untyped.RetainView == true && region.History.Count == 0, "Reset did not record RetainView.");
    }

    [Test]
    public async Task RetainViewKeepsTheEntryLifetimeWhileAReleaseWaitsForWorkAsync()
    {
        await using var fixture = new Fixture();
        var region = fixture.Navigator.CreateRegion<Page>(fixture.Root, NavigationTarget.Borrow(new Page("home")));
        var presentation = new KeepingPresentation();
        using var attachment = fixture.Navigator.AttachPresentation(presentation);

        // Retained: an entry whose view is kept and one whose view is rebuilt stay in the history, and neither is
        // forgotten by the presentation nor released.
        var kept = new Page("kept");
        var released = new Page("released");
        await Wait(region.PushAsync(NavigationTarget.Own(kept), new NavigationRequestOptions { RetainView = true }));
        var keptEntry = region.CurrentEntry!;
        var keptWork = presentation.Keep(kept);
        await Wait(region.PushAsync(NavigationTarget.Own(released), new NavigationRequestOptions { RetainView = false }));
        var releasedEntry = region.CurrentEntry!;
        var releasedWork = presentation.Keep(released);
        await Wait(region.PushAsync(NavigationTarget.Own(new Page("top"))));
        Require(keptEntry.State == NavigationEntryState.Retained && releasedEntry.State == NavigationEntryState.Retained
                && presentation.Forgotten.Count == 0 && kept.Disposed == 0 && released.Disposed == 0,
            "Retaining an entry forgot or released it, depending on RetainView.");

        // Retired while the presentation keeps work for it, as for an operation declared Cancel or Drain: the entry
        // leaves the stack at the commit, so a presentation drops its kept view at once, and the content is released
        // only after the work ends, whatever RetainView says.
        var back = await Wait(region.BackToAsync(keptEntry.Id));
        Require(back is NavigationResult<Page>.Committed committed && committed.Retired.Contains(releasedEntry.Id),
            $"BackTo gave {back}.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(releasedEntry.State == NavigationEntryState.Retired && releasedEntry.RetainView == false
                && !region.History.Any(entry => entry.Id == releasedEntry.Id) && presentation.Forgotten.Contains(released)
                && released.Disposed == 0,
            "The retired entry with RetainView = false did not leave the stack at once, or was released under its work.");
        var clear = await Wait(region.ClearAsync());
        Require(clear is NavigationResult<Page>.Committed && region.CurrentEntry is null && region.History.Count == 0, $"Clear gave {clear}.");
        await Wait(fixture.Navigator.WhenIdleAsync());
        Require(keptEntry.State == NavigationEntryState.Retired && keptEntry.RetainView == true && presentation.Forgotten.Contains(kept)
                && kept.Disposed == 0 && fixture.Navigator.DeferredReleaseCount == 2,
            "The retired entry with RetainView = true was not retired at once, or was released under its work.");

        releasedWork.SetResult();
        keptWork.SetResult();
        await Until(() => kept.Disposed == 1 && released.Disposed == 1 && fixture.Navigator.DeferredReleaseCount == 0,
            "The retired contents were not released after their work ended.");
    }
}
