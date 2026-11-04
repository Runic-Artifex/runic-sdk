using Runic.Application.Views;

namespace Runic.Application.Testing;

/// <summary>One region of a region collection in a View's state (<see cref="RunicRegionCollectionAttribute"/>).</summary>
/// <param name="Key">The region's key, which <c>SelectRegionAsync</c> and <c>CloseRegionAsync</c> name.</param>
/// <param name="Content">The region's current content, or <see langword="null"/> while the region is empty.</param>
public sealed record RunicRegionSlot(string Key, PageReference? Content);

/// <summary>
/// A region collection in a View's state (<see cref="RunicRegionCollectionAttribute"/>, <c>region-collections/1</c>):
/// its regions in order and the selected index.
/// </summary>
/// <param name="Regions">The regions, in the selection's order.</param>
/// <param name="Selected">The index of the selected region, or <see langword="null"/> when there is no region.</param>
public sealed record RunicRegionCollectionState(IReadOnlyList<RunicRegionSlot> Regions, int? Selected)
{
    /// <summary>The selected region, or <see langword="null"/>.</summary>
    public RunicRegionSlot? SelectedRegion => Selected is { } index ? Regions[index] : null;

    /// <summary>The keys of the regions, in order.</summary>
    public IReadOnlyList<string> Keys => [.. Regions.Select(region => region.Key)];
}
