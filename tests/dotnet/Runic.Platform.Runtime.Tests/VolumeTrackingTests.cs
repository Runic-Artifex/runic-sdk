using System.Collections.Immutable;
using Runic.Platform;
using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.Runtime.Tests;

// The watch's diff of two listings (I-218): a BSD name that passed to another disk between two listings is the old
// volume's removal and the new one's addition, never a change of the old one.
internal sealed class VolumeTrackingTests
{
    private static readonly DesktopVolume Image = new("disk18s1", "RUNIC APFS") { MountPaths = ["/Volumes/RUNIC APFS"], FileSystem = "apfs", DriveId = "disk18" };
    private static readonly DesktopVolume Simulator = new("disk18s1", "iOS 26.4.1 Simulator") { FileSystem = "apfs", DriveId = "disk19", IsSystem = true };

    [Test, DisplayName("VOLUMES: an ID that passed to a volume of another identity is a removal and an addition")]
    public void NameReusedByAnotherVolume()
    {
        var changes = Diff([new(Image, "7")], [new(Simulator, "9")]);
        Check(changes is [DesktopVolumeChange.Removed("disk18s1"), DesktopVolumeChange.Added { Volume: var added }] && added == Simulator,
            $"Removed, then Added: {string.Join("; ", changes)}");
        // Even with every property equal, another identity is another volume.
        changes = Diff([new(Image, "7")], [new(Image, "8")]);
        Check(changes is [DesktopVolumeChange.Removed("disk18s1"), DesktopVolumeChange.Added], $"an identical reappearance: {string.Join("; ", changes)}");
    }

    [Test, DisplayName("VOLUMES: the same identity with other properties is a change; unchanged is nothing")]
    public void SameIdentity()
    {
        var unmounted = Image with { MountPaths = [], CanMount = true };
        var changes = Diff([new(Image, "7")], [new(unmounted, "7")]);
        Check(changes is [DesktopVolumeChange.Changed { Volume: var changed }] && changed == unmounted, $"Changed: {string.Join("; ", changes)}");
        Check(Diff([new(Image, "7")], [new(Image with { MountPaths = ["/Volumes/RUNIC APFS"] }, "7")]).IsEmpty, "an equal listing reports nothing");
    }

    [Test, DisplayName("VOLUMES: without an identity on either side the ID decides, as on Windows")]
    public void WithoutIdentity()
    {
        Check(Diff([new(Image, null)], [new(Simulator, null)]) is [DesktopVolumeChange.Changed], "both unknown: a change");
        Check(Diff([new(Image, "7")], [new(Simulator, null)]) is [DesktopVolumeChange.Changed], "one unknown: a change");
        Check(Diff([new(Image, null)], [new(Image, null)]).IsEmpty, "both unknown and equal: nothing");
    }

    [Test, DisplayName("VOLUMES: in one listing, removals come first, then additions and changes in ID order")]
    public void ReuseWithinOneListing()
    {
        var stick = new DesktopVolume("disk4s2", "STICK") { MountPaths = ["/Volumes/STICK"] };
        var other = new DesktopVolume("disk20s1", "Other");
        // disk4s2 is ejected and disk18s1 passes to the simulator between two listings; disk20s1 is new and disk4s2
        // reappears with the same name as another disk.
        var changes = Diff(
            [new(Image, "7"), new(stick, "3"), new(new DesktopVolume("disk9s1", "Kept"), "2")],
            [new(Simulator, "9"), new(other, "10"), new(new DesktopVolume("disk9s1", "Kept"), "2"), new(stick with { DisplayName = "STICK 2" }, "11")]);
        string listed = string.Join("; ", changes);
        Check(changes is
            [
                DesktopVolumeChange.Removed("disk18s1"),
                DesktopVolumeChange.Removed("disk4s2"),
                DesktopVolumeChange.Added { Volume.Id: "disk18s1", Volume.DisplayName: "iOS 26.4.1 Simulator" },
                DesktopVolumeChange.Added { Volume.Id: "disk20s1" },
                DesktopVolumeChange.Added { Volume.Id: "disk4s2", Volume.DisplayName: "STICK 2" },
            ], $"removals, then additions in ID order: {listed}");
        // A consumer applying the changes in order ends with the current listing.
        var applied = new Dictionary<string, DesktopVolume>(StringComparer.Ordinal)
        {
            ["disk18s1"] = Image, ["disk4s2"] = stick, ["disk9s1"] = new DesktopVolume("disk9s1", "Kept"),
        };
        foreach (var change in changes)
        {
            if (change is DesktopVolumeChange.Removed(var id)) Check(applied.Remove(id), $"{id} was present");
            else if (change is DesktopVolumeChange.Added(var volume)) Check(applied.TryAdd(volume.Id, volume), $"{volume.Id} was absent");
        }
        Check(applied["disk18s1"] == Simulator && applied["disk4s2"].DisplayName == "STICK 2" && applied.Count == 4, $"the applied listing: {listed}");
    }

    private static ImmutableArray<DesktopVolumeChange> Diff(ImmutableArray<TrackedVolume> previous, ImmutableArray<TrackedVolume> current) =>
        [.. VolumeTracking.Diff(previous, current)];

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
