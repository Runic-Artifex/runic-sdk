using TUnit.Core;

namespace Runic.Desktop.Tests;

// A window remembers released capability names in groups, so a late call is answered as released (#490). The names
// released with no registration in between form one group; whole groups are forgotten, oldest first.
public sealed class ReleasedCapabilityMemoryTests
{
    [Test]
    public void OneLargeReleaseIsRememberedWhole()
    {
        using var window = new WebUiWindow();
        var count = WebUiWindow.ReleasedNameMemory + 1_000;
        var bindings = Enumerable.Range(0, count).Select(index => window.Bind($"content{index}Snapshot", Ignore)).ToArray();
        foreach (var binding in bindings) binding.Dispose();

        Assert.Equal((1, count), window.ReleasedMemory);
        Assert.True(window.WasRecentlyReleased("content0Snapshot"));
        Assert.True(window.WasRecentlyReleased($"content{count - 1}Snapshot"));
        Assert.False(window.WasRecentlyReleased("contentNeverSnapshot"));
    }

    [Test]
    public void GroupsAreEvictedWholeWithinTheGroupBound()
    {
        using var window = new WebUiWindow();
        // Each registration ends the run of releases before it, so every release here is a group of its own.
        var extra = 10;
        for (var group = 0; group < WebUiWindow.ReleasedGroupMemory + extra; group++)
        {
            window.Bind($"single{group}", Ignore).Dispose();
        }

        Assert.Equal((WebUiWindow.ReleasedGroupMemory, WebUiWindow.ReleasedGroupMemory), window.ReleasedMemory);
        Assert.False(window.WasRecentlyReleased("single0"));
        Assert.False(window.WasRecentlyReleased($"single{extra - 1}"));
        Assert.True(window.WasRecentlyReleased($"single{extra}"));
        Assert.True(window.WasRecentlyReleased($"single{WebUiWindow.ReleasedGroupMemory + extra - 1}"));
    }

    [Test]
    public void GroupsAreEvictedWholeWithinTheNameBound()
    {
        using var window = new WebUiWindow();
        var size = WebUiWindow.ReleasedNameMemory / 2 + 1;
        ReleaseGroup(window, "first", size);
        ReleaseGroup(window, "second", 3);
        Assert.Equal((2, size + 3), window.ReleasedMemory);

        // A third group passes the name bound: the oldest group is forgotten whole, the newer ones are kept.
        ReleaseGroup(window, "third", size);
        Assert.Equal((2, size + 3), window.ReleasedMemory);
        Assert.False(window.WasRecentlyReleased("first0"));
        Assert.False(window.WasRecentlyReleased($"first{size - 1}"));
        Assert.True(window.WasRecentlyReleased("second0"));
        Assert.True(window.WasRecentlyReleased($"third{size - 1}"));
    }

    [Test]
    public void ANameReleasedAgainBelongsToItsNewestGroup()
    {
        using var window = new WebUiWindow();
        ReleaseGroup(window, "page", 3);
        // page0 is bound and released again, in a group of its own.
        window.Bind("page0", Ignore).Dispose();
        Assert.Equal((2, 3), window.ReleasedMemory);

        for (var group = 0; group < WebUiWindow.ReleasedGroupMemory - 1; group++)
        {
            window.Bind($"single{group}", Ignore).Dispose();
        }

        // The first group was forgotten, but not the name it shared with a newer group.
        Assert.Equal(WebUiWindow.ReleasedGroupMemory, window.ReleasedMemory.Groups);
        Assert.False(window.WasRecentlyReleased("page1"));
        Assert.True(window.WasRecentlyReleased("page0"));
    }

    private static void ReleaseGroup(WebUiWindow window, string prefix, int size)
    {
        var bindings = Enumerable.Range(0, size).Select(index => window.Bind($"{prefix}{index}", Ignore)).ToArray();
        foreach (var binding in bindings) binding.Dispose();
    }

    private static void Ignore(WebUiEvent webUiEvent)
    {
    }
}
