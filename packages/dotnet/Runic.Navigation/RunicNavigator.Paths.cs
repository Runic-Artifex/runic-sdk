using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Navigation;

// 0.16 D2: navigation paths. GetPath snapshots the region tree under the gate and maps routes outside it; RestoreAsync
// reconciles regions with a path through ordinary, guarded requests.
public sealed partial class RunicNavigator
{
    /// <summary>
    /// Gets the navigator's state as a serializable <see cref="NavigationPath"/>: every region and selection that no entry's
    /// content owns, by position in creation order, each with its entries and, below each entry, the regions and selections
    /// its content owns; the selected region of each selection; and each entry's route.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state is read at one moment; a request in flight is not part of it. An entry that no route of
    /// <paramref name="routes"/> describes is recorded with a <see langword="null"/> route, with the regions below it.
    /// Closed regions and pending entries are left out.
    /// </para>
    /// <para>
    /// Routes come from <see cref="INavigationRouteMap.GetRoute"/>, which runs on the caller's thread after the state is
    /// read. The path holds no content: persist it as the application persists its own state, for example with
    /// <see cref="NavigationPath.ToJson"/> next to the window placement.
    /// </para>
    /// </remarks>
    /// <param name="routes">The route map, or <see langword="null"/> for the <see cref="INavigationRouteMap"/> of <see cref="Services"/>.</param>
    /// <exception cref="InvalidOperationException"><paramref name="routes"/> is null and <see cref="Services"/> has no <see cref="INavigationRouteMap"/>.</exception>
    public NavigationPath GetPath(INavigationRouteMap? routes = null)
    {
        var map = RouteMap(routes);
        PathSnapshot snapshot;
        lock (Gate) snapshot = SnapshotRootLocked();
        return new NavigationPath(
            [.. snapshot.Regions.Select(region => ToPath(region, map))],
            [.. snapshot.Selections.Select(selection => ToPath(selection, map))]);
    }

    /// <summary>
    /// Rebuilds <paramref name="path"/>, for example one that <see cref="GetPath"/> saved before a restart: each region is
    /// reconciled with the path's entries through ordinary navigation requests, so departure guards, preparation hooks,
    /// <see cref="INavigationAware"/> notifications and arrival hooks run as for any navigation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For each region, the entries whose routes already match the path from the bottom stay, with their content. Then
    /// the rest of the region goes: <c>BackToAsync</c> the last entry that stays, or <c>ResetAsync</c> with the path's
    /// first entry when none stays, or <c>ClearAsync</c> when the path's region is empty. The path's remaining entries
    /// are pushed one at a time. After an entry is in place, the regions and selections its content owns are restored the
    /// same way, before the entry above it is pushed; a selection then selects its saved region with
    /// <see cref="NavigationSelection.SelectAsync"/>, and a region collection gets its missing tabs from
    /// <see cref="NavigationSelectionOptions.RestoreRegion"/>.
    /// </para>
    /// <para>
    /// Restoring fails softly: what can't be restored is left out and reported in
    /// <see cref="NavigationRestoreResult.Issues"/> (log event 1220), and the rest is restored. An entry whose route no
    /// route matches stops its region by default, so the region keeps the entries below it and none above it
    /// (<see cref="NavigationRestoreOptions.UnmappedEntries"/>). A request that does not commit, because a guard refused or
    /// the request failed, stops that region; its sibling regions are still restored. Entries and tabs beyond
    /// <see cref="NavigationRestoreOptions.MaximumEntriesPerRegion"/> and <see cref="NavigationRestoreOptions.MaximumRegionsPerSelection"/>
    /// are left out. Regions and selections that the owner has but the path doesn't name, and tabs beyond the path's, are
    /// left as they are. A tab that <see cref="NavigationSelectionOptions.RestoreRegion"/> created and that nothing could be
    /// restored into is discarded (<see cref="DiscardRegionAsync(NavigationRegion)"/>).
    /// </para>
    /// <para>
    /// Call it once the window's regions exist, such as after the window opened. Don't call it from a navigation hook of this
    /// navigator: its requests would be <see cref="NavigationRejection.Reentrant"/>.
    /// </para>
    /// </remarks>
    /// <param name="path">The path to rebuild.</param>
    /// <param name="options">The route map, what an unmapped entry does, and the bounds; the defaults without it.</param>
    /// <param name="cancellationToken">Cancels the requests not yet committed; the restore then stops.</param>
    /// <exception cref="InvalidOperationException">No route map was passed in <paramref name="options"/> or registered in <see cref="Services"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound of <paramref name="options"/> is less than 1.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask<NavigationRestoreResult> RestoreAsync(NavigationPath path, NavigationRestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        options = NavigationRestoreOptions.Check(options);
        return new(RestoreRootAsync(path, options, cancellationToken));
    }

    private async Task<NavigationRestoreResult> RestoreRootAsync(NavigationPath path, NavigationRestoreOptions options,
        CancellationToken cancellationToken)
    {
        var restore = new PathRestore(this, RouteMap(options.Routes), cancellationToken) { Limits = options };
        NavigationRegion[] regions;
        NavigationSelection[] selections;
        lock (Gate) (regions, selections) = RootChildrenLocked();
        await this.AfterUserCode(restore.ChildrenAsync(regions, selections, path.Regions, path.Selections, ""));
        return restore.Result();
    }

    internal INavigationRouteMap RouteMap(INavigationRouteMap? routes) =>
        routes ?? Services?.GetService<INavigationRouteMap>()
        ?? throw new InvalidOperationException(
            "No navigation route map: pass one, or register it with services.AddRunicNavigationRoutes(...) in the navigator's services.");

    internal NavigationRegionPath GetRegionPath(NavigationRegionCore region, INavigationRouteMap routes)
    {
        RegionSnapshot snapshot;
        lock (Gate) snapshot = SnapshotRegionLocked(region, 0);
        return ToPath(snapshot, routes);
    }

    internal NavigationSelectionPath GetSelectionPath(NavigationSelectionCore selection, INavigationRouteMap routes)
    {
        SelectionSnapshot snapshot;
        lock (Gate) snapshot = SnapshotSelectionLocked(selection, 0);
        return ToPath(snapshot, routes);
    }

    internal async Task<NavigationRestoreResult> RestoreRegionAsync(NavigationRegion region, NavigationRegionPath path,
        NavigationRestoreOptions? options, CancellationToken cancellationToken)
    {
        options = NavigationRestoreOptions.Check(options);
        var restore = new PathRestore(this, RouteMap(options.Routes), cancellationToken) { Limits = options };
        await this.AfterUserCode(restore.RegionAsync(region, path, ""));
        return restore.Result();
    }

    internal async Task<NavigationRestoreResult> RestoreSelectionAsync(NavigationSelection selection, NavigationSelectionPath path,
        NavigationRestoreOptions? options, CancellationToken cancellationToken)
    {
        options = NavigationRestoreOptions.Check(options);
        var restore = new PathRestore(this, RouteMap(options.Routes), cancellationToken) { Limits = options };
        await this.AfterUserCode(restore.SelectionAsync(selection, path, ""));
        return restore.Result();
    }

    // Opens a route in a region (NavigationRegion.OpenRouteAsync). Null when no route matches.
    internal async Task<NavigationResult<object>?> OpenRouteAsync(NavigationRegion region, string route, NavigationRouteOpen mode,
        INavigationRouteMap? routes, NavigationRequestOptions? options, CancellationToken cancellationToken)
    {
        var map = RouteMap(routes);
        if (map.CreateTarget(region, route) is not NavigationTargetCore target)
        {
            NavigationLog.NavigationRouteUnmatched(Logger, region.Core.ContentTypeName, region.Core.Id);
            return null;
        }
        var canonical = target.Route ?? route.Trim('/');
        var stack = region.Core.Stack;
        switch (mode)
        {
            case NavigationRouteOpen.Push:
                if (stack.Length > 0 && RouteOf(map, stack[^1]) == canonical) return Unchanged(stack[^1]);
                return await this.AfterUserCode(RequestAsync(region, NavigationOperation.Push, target, null, options, cancellationToken));
            case NavigationRouteOpen.Replace:
                return await this.AfterUserCode(RequestAsync(region, NavigationOperation.Replace, target, null, options, cancellationToken));
            case NavigationRouteOpen.Reset:
                return await this.AfterUserCode(RequestAsync(region, NavigationOperation.Reset, target, null, options, cancellationToken));
            case NavigationRouteOpen.BackToOrPush:
                for (var index = stack.Length - 1; index >= 0; index--)
                {
                    if (RouteOf(map, stack[index]) != canonical) continue;
                    return index == stack.Length - 1
                        ? Unchanged(stack[index])
                        : await this.AfterUserCode(RequestAsync(region, NavigationOperation.BackTo, null, stack[index].Id, options, cancellationToken));
                }
                return await this.AfterUserCode(RequestAsync(region, NavigationOperation.Push, target, null, options, cancellationToken));
            case NavigationRouteOpen.Stack:
                var restore = new PathRestore(this, map, cancellationToken) { Options = options };
                List<NavigationEntryPath> entries = [.. RouteStack(map, region, canonical).Select(static path => new NavigationEntryPath(path))];
                // A root page without a route, such as a borrowed home page, stays below the route's pages.
                if (stack.Length > 0 && RouteOf(map, stack[0]) is null) entries.Insert(0, new NavigationEntryPath(null));
                await this.AfterUserCode(restore.RegionAsync(region, new NavigationRegionPath(entries), "", childrenToo: false));
                return restore.LastResult ?? (region.Core.CurrentEntry is { } current ? Unchanged(current) : null);
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown route open mode.");
        }
    }

    // The route and each of its prefixes that a route matches for the region, shortest first.
    internal static List<string> RouteStack(INavigationRouteMap map, NavigationRegion region, string route)
    {
        var segments = route.Trim('/').Split('/');
        List<string> stack = [];
        for (var length = 1; length < segments.Length; length++)
        {
            var prefix = string.Join('/', segments, 0, length);
            if (map.CreateTarget(region, prefix) is NavigationTargetCore target) stack.Add(target.Route ?? prefix);
        }
        stack.Add(route);
        return stack;
    }

    private static NavigationResult<object> Unchanged(NavigationEntryCore current) =>
        new NavigationResult<object>.Committed(current.View<object>(), []);

    private static string? RouteOf(INavigationRouteMap routes, NavigationEntryCore entry) =>
        entry.Content is null ? null : routes.GetRoute(entry.View<object>());

    private static Task<NavigationResult<object>> RequestAsync(NavigationRegion region, NavigationOperation operation,
        NavigationTargetCore? target, NavigationEntryId? backTo, NavigationRequestOptions? options, CancellationToken cancellationToken)
    {
        if (target is not null && !region.ContentType.IsAssignableFrom(target.ContentType))
            throw new InvalidOperationException(
                $"The route map created a {target.ContentType.Name} target for a region of {region.ContentType.Name}.");
        return NavigationResults.MapTask<object>(region.Core.Start(operation, target, backTo, options, cancellationToken));
    }

    // A region that a restore or a route opening created for a tab and then abandoned, because nothing could be restored
    // into it or Add refused it: it closes without asking guards (it was never presented), its entries retire, and it
    // leaves its owner, so it is no root region of a later GetPath.
    internal async Task DiscardRegionAsync(NavigationRegionCore region)
    {
        foreach (var entry in await this.AfterUserCode(CloseRegionAsync(region)))
            await this.AfterUserCode(RetireAsync(entry, null));
        lock (Gate)
            if (_regionsByOwner.TryGetValue(region.Owner, out var owned))
            {
                owned.Remove(region);
                if (owned.Count == 0) _regionsByOwner.Remove(region.Owner);
            }
    }

    /// <summary>
    /// Closes <paramref name="region"/> without asking its guards and retires its entries, for a region the application
    /// created for a tab and then didn't add to its selection, such as one whose first page a guard or an unmatched link
    /// kept out. It is no longer one of its owner's regions, so <see cref="GetPath"/> doesn't record it.
    /// </summary>
    /// <remarks>Only for a region that is in no selection and that no page presents.</remarks>
    /// <exception cref="InvalidOperationException">The region is in a selection.</exception>
    public ValueTask DiscardRegionAsync(NavigationRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!ReferenceEquals(region.Navigator, this)) throw new ArgumentException($"{region} belongs to another navigator.", nameof(region));
        lock (Gate)
            if (region.Core.Selection is not null)
                throw new InvalidOperationException($"{region} is in a selection: remove it with NavigationSelection.RemoveAsync.");
        return new(DiscardRegionAsync(region.Core));
    }

    // ---- Snapshot (under the gate) ------------------------------------------------------------------------------------

    private sealed record PathSnapshot(RegionSnapshot[] Regions, SelectionSnapshot[] Selections);

    private sealed record RegionSnapshot(EntrySnapshot[] Entries);

    private sealed record EntrySnapshot(NavigationEntryCore Entry, RegionSnapshot[] Regions, SelectionSnapshot[] Selections);

    private sealed record SelectionSnapshot(RegionSnapshot[] Regions, int Selected);

    // The contents of the committed entries of live regions: the owners whose regions are child regions.
    private HashSet<object> EntryContentsLocked()
    {
        var contents = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var region in _regions)
            if (!region.Closed)
                foreach (var entry in region.Stack)
                    if (entry.Content is { } content) contents.Add(content);
        return contents;
    }

    private (NavigationRegion[] Regions, NavigationSelection[] Selections) RootChildrenLocked()
    {
        var contents = EntryContentsLocked();
        List<NavigationRegion> regions = [];
        List<NavigationSelection> selections = [];
        foreach (var region in _regions)
        {
            if (region.Closed || contents.Contains(region.Owner)) continue;
            if (region.SelectionUnit is { Public: { } selection }) selections.Add(selection);
            else if (region.Selection is null && region.Public is { } plain) regions.Add(plain);
        }
        return ([.. regions], [.. selections]);
    }

    private (NavigationRegion[] Regions, NavigationSelection[] Selections) ChildrenLocked(object owner)
    {
        List<NavigationRegion> regions = [];
        List<NavigationSelection> selections = [];
        if (_regionsByOwner.TryGetValue(owner, out var owned))
            foreach (var region in owned)
            {
                if (region.Closed) continue;
                if (region.SelectionUnit is { Public: { } selection }) selections.Add(selection);
                else if (region.Selection is null && region.Public is { } plain) regions.Add(plain);
            }
        return ([.. regions], [.. selections]);
    }

    internal (NavigationRegion[] Regions, NavigationSelection[] Selections) Children(object owner)
    {
        lock (Gate) return ChildrenLocked(owner);
    }

    private PathSnapshot SnapshotRootLocked()
    {
        var (regions, selections) = RootChildrenLocked();
        return new PathSnapshot(
            [.. regions.Select(region => SnapshotRegionLocked(region.Core, 0))],
            [.. selections.Select(selection => SnapshotSelectionLocked(selection.Core, 0))]);
    }

    private RegionSnapshot SnapshotRegionLocked(NavigationRegionCore region, int depth)
    {
        if (region.Closed) return new RegionSnapshot([]);
        var stack = region.Stack;
        var entries = new EntrySnapshot[stack.Length];
        for (var index = 0; index < stack.Length; index++)
        {
            var entry = stack[index];
            RegionSnapshot[] regions = [];
            SelectionSnapshot[] selections = [];
            // Bounded like NavigationPath.FromJson, so a path that GetPath writes can always be read.
            if (entry.Content is { } content && depth < NavigationPath.MaximumDepth)
            {
                var (owned, ownedSelections) = ChildrenLocked(content);
                regions = [.. owned.Select(child => SnapshotRegionLocked(child.Core, depth + 1))];
                selections = [.. ownedSelections.Select(child => SnapshotSelectionLocked(child.Core, depth + 1))];
            }
            entries[index] = new EntrySnapshot(entry, regions, selections);
        }
        return new RegionSnapshot(entries);
    }

    private SelectionSnapshot SnapshotSelectionLocked(NavigationSelectionCore selection, int depth)
    {
        var state = selection.State;
        return new SelectionSnapshot([.. state.Regions.Select(region => SnapshotRegionLocked(region.Core, depth))], state.SelectedIndex);
    }

    private static NavigationRegionPath ToPath(RegionSnapshot region, INavigationRouteMap routes) =>
        new([.. region.Entries.Select(entry => new NavigationEntryPath(RouteOf(routes, entry.Entry))
        {
            Regions = [.. entry.Regions.Select(child => ToPath(child, routes))],
            Selections = [.. entry.Selections.Select(child => ToPath(child, routes))],
        })]);

    private static NavigationSelectionPath ToPath(SelectionSnapshot selection, INavigationRouteMap routes) =>
        new([.. selection.Regions.Select(region => ToPath(region, routes))], selection.Selected);

    // ---- Restore ------------------------------------------------------------------------------------------------------

    private sealed class PathRestore(RunicNavigator navigator, INavigationRouteMap routes, CancellationToken cancellationToken)
    {
        private readonly List<NavigationRestoreIssue> _issues = [];

        // The engine's await helper (NavigationAwait) for this restore's navigator.
        private ConfiguredValueTaskAwaitable AfterUserCode(Task pending) => navigator.AfterUserCode(pending);

        private ConfiguredValueTaskAwaitable AfterUserCode(ValueTask pending) => navigator.AfterUserCode(pending);

        private ConfiguredValueTaskAwaitable<T> AfterUserCode<T>(Task<T> pending) => navigator.AfterUserCode(pending);

        private ConfiguredValueTaskAwaitable<T> AfterUserCode<T>(ValueTask<T> pending) => navigator.AfterUserCode(pending);

        public NavigationRequestOptions? Options { get; init; }

        public NavigationRestoreOptions Limits { get; init; } = NavigationRestoreOptions.Default;

        // The result of the last request made, for OpenRouteAsync.
        public NavigationResult<object>? LastResult { get; private set; }

        public NavigationRestoreResult Result() => new([.. _issues]);

        public async Task ChildrenAsync(NavigationRegion[] regions, NavigationSelection[] selections,
            IReadOnlyList<NavigationRegionPath> regionPaths, IReadOnlyList<NavigationSelectionPath> selectionPaths, string location)
        {
            for (var index = 0; index < regionPaths.Count; index++)
            {
                var at = $"{location}regions[{index}]";
                if (index < regions.Length) await this.AfterUserCode(RegionAsync(regions[index], regionPaths[index], at + "/"));
                else Issue(NavigationRestoreIssueKind.MissingRegion, at);
            }
            for (var index = 0; index < selectionPaths.Count; index++)
            {
                var at = $"{location}selections[{index}]";
                if (index < selections.Length) await this.AfterUserCode(SelectionAsync(selections[index], selectionPaths[index], at + "/"));
                else Issue(NavigationRestoreIssueKind.MissingSelection, at);
            }
        }

        public async Task SelectionAsync(NavigationSelection selection, NavigationSelectionPath path, string location)
        {
            // The live region each saved region was restored into, by the saved index.
            var count = Math.Min(path.Regions.Count, Limits.MaximumRegionsPerSelection);
            if (count < path.Regions.Count) Issue(NavigationRestoreIssueKind.LimitExceeded, $"{location}regions[{count}..]");
            var restored = new NavigationRegion?[count];
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var at = $"{location}regions[{index}]";
                var live = selection.Regions;
                if (index < live.Count)
                {
                    restored[index] = live[index];
                    await this.AfterUserCode(RegionAsync(live[index], path.Regions[index], at + "/"));
                    continue;
                }
                var created = selection.Core.RestoreRegion is { } create ? create(selection) : null;
                if (created is null || !ReferenceEquals(created.Navigator, navigator) || created.Core.Stack.Length > 0
                    || created.Core.Selection is not null || created.Core.Closed)
                {
                    Issue(NavigationRestoreIssueKind.MissingSelectionRegion, at);
                    continue;
                }
                var added = false;
                try
                {
                    await this.AfterUserCode(RegionAsync(created, path.Regions[index], at + "/"));
                    // Nothing was restored into it, so no empty tab is added.
                    if (created.Core.Stack.Length == 0) continue;
                    try
                    {
                        await this.AfterUserCode(navigator.ModelContext.InvokeAsync(() => selection.Add(created), cancellationToken));
                        restored[index] = created;
                        added = true;
                    }
                    catch (Exception error) when (error is ArgumentException or ObjectDisposedException)
                    {
                        Issue(NavigationRestoreIssueKind.MissingSelectionRegion, at);
                    }
                }
                finally
                {
                    // A tab that isn't added would stay a root region of the owner: GetPath would record it.
                    if (!added) await this.AfterUserCode(navigator.DiscardRegionAsync(created.Core));
                }
            }
            if (path.Selected >= 0 && path.Selected < restored.Length && restored[path.Selected] is { } target
                && !ReferenceEquals(selection.Selected, target) && selection.Regions.Contains(target))
            {
                var result = await this.AfterUserCode(selection.SelectAsync(target, Options, cancellationToken));
                if (!Committed(result, $"{location}selected", null)) return;
            }
        }

        public async Task RegionAsync(NavigationRegion region, NavigationRegionPath path, string location, bool childrenToo = true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stack = region.Core.Stack;
            IReadOnlyList<NavigationEntryPath> saved = path.Entries;
            if (saved.Count > Limits.MaximumEntriesPerRegion)
            {
                Issue(NavigationRestoreIssueKind.LimitExceeded, $"{location}entries[{Limits.MaximumEntriesPerRegion}..]");
                saved = [.. saved.Take(Limits.MaximumEntriesPerRegion)];
            }
            var kept = 0;
            while (kept < saved.Count && kept < stack.Length && SameEntry(saved[kept].Route, RouteOf(routes, stack[kept]), kept)) kept++;

            // The targets of the entries to push, in order. An entry no route matches stops the region (the default) or is
            // left out (NavigationUnmappedEntries.Skip).
            List<(int Index, NavigationTargetCore Target)> targets = [];
            for (var index = kept; index < saved.Count; index++)
            {
                if (saved[index].Route is { } route && routes.CreateTarget(region, route) is NavigationTargetCore target)
                {
                    targets.Add((index, target));
                    continue;
                }
                Issue(NavigationRestoreIssueKind.UnmappedRoute, $"{location}entries[{index}]", saved[index].Route);
                if (Limits.UnmappedEntries == NavigationUnmappedEntries.Skip) continue;
                if (index + 1 < saved.Count) Issue(NavigationRestoreIssueKind.AboveUnmapped, $"{location}entries[{index + 1}..]");
                break;
            }

            var next = 0;
            if (kept < stack.Length)
            {
                NavigationResult<object>? result = null;
                if (kept > 0) result = await this.AfterUserCode(RequestAsync(region, NavigationOperation.BackTo, null, stack[kept - 1].Id, Options, cancellationToken));
                else if (targets.Count > 0) result = await this.AfterUserCode(RequestAsync(region, NavigationOperation.Reset, targets[next++].Target, null, Options, cancellationToken));
                else if (saved.Count == 0) result = await this.AfterUserCode(RequestAsync(region, NavigationOperation.Clear, null, null, Options, cancellationToken));
                if (result is not null)
                {
                    LastResult = result;
                    var at = kept > 0 ? $"{location}entries[{kept}]" : targets.Count > 0 ? $"{location}entries[{targets[0].Index}]" : location.TrimEnd('/');
                    if (!Committed(result, at, kept == 0 && targets.Count > 0 ? targets[0].Target.Route : null)) return;
                }
            }

            if (childrenToo)
                for (var index = 0; index < kept; index++)
                    if (stack[index].Content is { } content)
                        await this.AfterUserCode(OwnerAsync(content, saved[index], $"{location}entries[{index}]/"));

            // The entry Reset made current.
            if (next == 1 && childrenToo && region.Core.CurrentEntry is { Content: { } reset })
                await this.AfterUserCode(OwnerAsync(reset, saved[targets[0].Index], $"{location}entries[{targets[0].Index}]/"));

            for (; next < targets.Count; next++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (index, target) = targets[next];
                var result = await this.AfterUserCode(RequestAsync(region, NavigationOperation.Push, target, null, Options, cancellationToken));
                LastResult = result;
                if (!Committed(result, $"{location}entries[{index}]", target.Route)) return;
                if (childrenToo && result is NavigationResult<object>.Committed { Current.Content: { } content })
                    await this.AfterUserCode(OwnerAsync(content, saved[index], $"{location}entries[{index}]/"));
            }
        }

        private Task OwnerAsync(object content, NavigationEntryPath entry, string location)
        {
            if (entry.Regions.Count == 0 && entry.Selections.Count == 0) return Task.CompletedTask;
            var (regions, selections) = navigator.Children(content);
            return ChildrenAsync(regions, selections, entry.Regions, entry.Selections, location);
        }

        // Entries match by route; two entries without a route match only at the bottom, as a region's own root page.
        private static bool SameEntry(string? saved, string? live, int index) =>
            saved is null ? live is null && index == 0 : string.Equals(saved, live, StringComparison.Ordinal);

        private bool Committed(NavigationResult<object> result, string location, string? route)
        {
            if (result.IsCommitted) return true;
            cancellationToken.ThrowIfCancellationRequested();
            Issue(NavigationRestoreIssueKind.NotCommitted, location, route, result.Outcome);
            return false;
        }

        private void Issue(NavigationRestoreIssueKind kind, string location, string? route = null, NavigationOutcome? outcome = null)
        {
            _issues.Add(new NavigationRestoreIssue(kind, location, route, outcome));
            NavigationLog.NavigationRestoreSkipped(navigator.Logger, location, kind);
        }
    }
}

/// <summary>How <see cref="NavigationRegion.OpenRouteAsync"/> opens a route in a region.</summary>
/// <remarks>Later releases can add values.</remarks>
public enum NavigationRouteOpen
{
    /// <summary>Pushes the route's page; when it is the current page already, nothing changes.</summary>
    Push,

    /// <summary>Replaces the current page with the route's page.</summary>
    Replace,

    /// <summary>Makes the route's page the region's only page.</summary>
    Reset,

    /// <summary>
    /// Goes back to the page with the route when the region's history has one (<c>BackToAsync</c>), and pushes it otherwise,
    /// as a browser's address bar does with its history.
    /// </summary>
    BackToOrPush,

    /// <summary>
    /// Makes the region's history the route's prefixes that are routes too, then the route: for
    /// <c>customers/42/invoices/7</c>, <c>customers</c>, <c>customers/42</c> and <c>customers/42/invoices/7</c>. A bottom page
    /// that no route describes, such as a borrowed home page, stays below them, and the pages already in place from the
    /// bottom stay, as <see cref="RunicNavigator.RestoreAsync"/> does.
    /// </summary>
    Stack,
}
