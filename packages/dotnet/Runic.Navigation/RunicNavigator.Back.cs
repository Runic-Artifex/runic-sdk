namespace Runic.Navigation;

// 0.16 D2: a window's Back (mouse X1, Alt+Left, a browser's Back) without naming a region.
public sealed partial class RunicNavigator
{
    /// <summary>
    /// Goes back in the innermost presented region that can: what a window's Back button, the mouse's Back button or
    /// Alt+Left does. A result entry (<see cref="NavigationRegion{TContent}.PushForResult{TResult}(INavigationTarget{TContent}, NavigationRequestOptions?, CancellationToken)"/>)
    /// that is current is dismissed, as a dialog closes on Back; another region goes <c>BackAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The region is found from the regions and selections no entry owns, the most recently created first; within a region,
    /// the regions and selections its current entry's content owns, the most recently created first, are tried before the
    /// region itself, and a selection is followed only into its selected region. The first region found that has a current
    /// result entry, or a retained entry to go back to, goes back.
    /// </para>
    /// <para>
    /// The request is an ordinary one, guarded and returned as a result. With no such region it is
    /// <see cref="NavigationRejection.NoHistory"/> and nothing runs.
    /// </para>
    /// <para>
    /// Called on the navigator, every region counts as presented. A presentation host's Back counts only the regions it
    /// presents: a Runic Views page's Back (<c>navigation-path/1</c>) skips a region that no Bridge of the window presents,
    /// and the regions below it.
    /// </para>
    /// </remarks>
    /// <param name="options">The request's options.</param>
    /// <param name="cancellationToken">Cancels the request before it commits.</param>
    public ValueTask<NavigationResult<object>> BackAsync(NavigationRequestOptions? options = null, CancellationToken cancellationToken = default) =>
        PresentedBackAsync(null, options, cancellationToken);

    // A presentation host's Back: only the regions it presents count (Runic Views: the regions a window's Bridges present).
    internal ValueTask<NavigationResult<object>> PresentedBackAsync(Func<NavigationRegion, bool>? presented, NavigationRequestOptions? options,
        CancellationToken cancellationToken)
    {
        NavigationRegionCore? target;
        NavigationEntryCore? result = null;
        lock (Gate)
        {
            target = _closing ? null : BackTargetLocked(presented);
            if (target?.CurrentEntry is { ResultRequest: not null } current) result = current;
        }
        if (target?.Public is not { } region)
            return new(new NavigationResult<object>.Rejected(_closing ? NavigationRejection.Closed : NavigationRejection.NoHistory, null));
        return result is not null ? result.Context.DismissAsync(cancellationToken) : region.BackAsync(options, cancellationToken);
    }

    // The innermost presented region that can go back, or null. Call under the gate.
    private NavigationRegionCore? BackTargetLocked(Func<NavigationRegion, bool>? presented)
    {
        var contents = EntryContentsLocked();
        for (var index = _regions.Count - 1; index >= 0; index--)
        {
            var region = _regions[index];
            if (region.Closed || contents.Contains(region.Owner) || region.Selection is not null) continue;
            if (BackTargetLocked(region, 0, presented) is { } found) return found;
        }
        return null;
    }

    private NavigationRegionCore? BackTargetLocked(NavigationRegionCore region, int depth, Func<NavigationRegion, bool>? presented)
    {
        if (region.Closed || depth > NavigationPath.MaximumDepth) return null;
        // A selection is followed into its selected region.
        if (region.SelectionUnit is { } selection)
            return selection.Selected is { } selected ? BackTargetLocked(selected, depth + 1, presented) : null;
        // A region nothing presents is not where the user is, and neither are the regions below it.
        if (presented is not null && (region.Public is not { } shown || !presented(shown))) return null;
        var current = region.CurrentEntry;
        if (current?.Content is { } content && _regionsByOwner.TryGetValue(content, out var children))
            for (var index = children.Count - 1; index >= 0; index--)
            {
                var child = children[index];
                if (child.Selection is not null) continue; // reached through its selection
                if (BackTargetLocked(child, depth + 1, presented) is { } found) return found;
            }
        return current is { ResultRequest: not null } || region.Stack.Length > 1 ? region : null;
    }
}
