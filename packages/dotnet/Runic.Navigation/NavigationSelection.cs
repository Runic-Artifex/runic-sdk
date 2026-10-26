using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Runic.Navigation;

/// <summary>Configures a selection created by <see cref="RunicNavigator.CreateSelection"/>.</summary>
public sealed record NavigationSelectionOptions
{
    /// <summary>Gets the index of the initially selected region; 0 by default. It is ignored when there are no regions.</summary>
    public int SelectedIndex { get; init; }

    /// <summary>
    /// Gets how the selection admits a request while an earlier one runs, as <see cref="NavigationRegionOptions.Admission"/>
    /// does for a region. The default, <see cref="NavigationAdmission.Supersede"/>, lets a later request supersede earlier
    /// ones that have not committed.
    /// </summary>
    public NavigationAdmission Admission { get; init; }
}

/// <summary>
/// Which one of an owner's sibling regions is presented, such as the tab of a page whose tabs each keep their own stack.
/// Changing it is a navigation transition: the current entries of the region left, and of the regions below them, depart
/// as <see cref="NavigationDepartureKind.Retain"/>, and a veto keeps the selection.
/// </summary>
/// <remarks>
/// <para>
/// A selection change is admitted, guarded, superseded, cancelled and committed like a request of a region, with
/// <see cref="NavigationOperation.Select"/>, and its outcome is returned, never thrown. It changes no stack and no
/// entry's state: the region left keeps its entries, which stay <see cref="NavigationEntryState.Active"/>, and selecting
/// it again presents the same entry. The regions stay the owner's: when the owner's entry retires, every entry of every
/// region is asked as <see cref="NavigationDepartureKind.Retire"/>, whichever is selected, and the regions and the
/// selection close.
/// </para>
/// <para>
/// Only <see cref="RunicNavigator.CreateSelection"/> creates selections, and members can be added to the class in a
/// minor release.
/// </para>
/// </remarks>
public sealed class NavigationSelection : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs RegionsChanged = new(nameof(Regions));
    private static readonly PropertyChangedEventArgs SelectedChanged = new(nameof(Selected));
    private static readonly PropertyChangedEventArgs SelectedIndexChanged = new(nameof(SelectedIndex));
    private static readonly PropertyChangedEventArgs IsTransitioningChanged = new(nameof(IsTransitioning));
    private static readonly PropertyChangedEventArgs IsBusyChanged = new(nameof(IsBusy));

    internal NavigationSelection(NavigationSelectionCore core)
    {
        Core = core;
        core.Unit.Raise = Raise;
    }

    internal NavigationSelectionCore Core { get; }

    /// <summary>Gets the navigator that owns the selection and its regions.</summary>
    public RunicNavigator Navigator => Core.Unit.Navigator;

    /// <summary>
    /// Gets the regions to select from, in presentation order. The list is an immutable snapshot, replaced by
    /// <see cref="Add"/> and <see cref="RemoveAsync"/>.
    /// </summary>
    public IReadOnlyList<NavigationRegion> Regions => Core.State.Regions;

    /// <summary>Gets the presented region, or <see langword="null"/> only when <see cref="Regions"/> is empty.</summary>
    public NavigationRegion? Selected => Core.State.Selected;

    /// <summary>Gets the index of <see cref="Selected"/> in <see cref="Regions"/>, or -1 when <see cref="Regions"/> is empty.</summary>
    public int SelectedIndex => Core.State.SelectedIndex;

    /// <summary>Gets how the selection admits a request while an earlier one runs (<see cref="NavigationSelectionOptions.Admission"/>).</summary>
    public NavigationAdmission Admission => Core.Unit.Admission;

    /// <summary>
    /// Gets whether a selection change is in flight: from a request's admission until its commit decision, like
    /// <see cref="NavigationRegion.IsTransitioning"/>. A selection change doesn't make the region it leaves transition.
    /// </summary>
    public bool IsTransitioning => Core.Unit.IsTransitioning;

    /// <summary>
    /// Gets whether a request of this selection has not completed: from its admission until its task completes, like
    /// <see cref="NavigationRegion.IsBusy"/>. A selection change doesn't make the region it leaves busy.
    /// </summary>
    public bool IsBusy => Core.Unit.IsBusy;

    /// <summary>
    /// Completes when <see cref="IsBusy"/> is <see langword="false"/>: when no request of this selection is running. It
    /// doesn't wait for the requests of its regions.
    /// </summary>
    /// <remarks>A hook of a transition of this selection that awaits it deadlocks: the selection is busy while the hook runs.</remarks>
    /// <param name="cancellationToken">Stops waiting.</param>
    public ValueTask WhenIdleAsync(CancellationToken cancellationToken = default) =>
        Core.Unit.Navigator.WhenRegionIdleAsync(Core.Unit, cancellationToken);

    /// <summary>
    /// Presents <paramref name="region"/>. The current entry of the selected region, and the current entry of every region
    /// its content owns, deepest first, are asked as <see cref="NavigationDepartureKind.Retain"/> with
    /// <see cref="NavigationOperation.Select"/>, then the region guards that ask on retain and the navigator's. A veto keeps
    /// the selection. The commit changes only <see cref="Selected"/> and <see cref="SelectedIndex"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Selecting the selected region while no request of the selection is pending commits at once. The entering region's
    /// entries are not asked or prepared. A later <see cref="SelectAsync"/> or <see cref="RemoveAsync"/> supersedes this
    /// request until it starts committing, and a commit in the region left, or in a region below it, while it asks
    /// supersedes it too; requests of the region left are neither superseded nor delayed.
    /// </para>
    /// <para>
    /// <see cref="NavigationRequestOptions.ExpectedCurrent"/> names the current entry of the region left, at admission and at
    /// commit; <see cref="NavigationRequestOptions.RetainView"/> is ignored. <see cref="NavigationResult{TContent}.Committed.Current"/>
    /// is the current entry of the region selected after the commit, and <see cref="NavigationResult{TContent}.Committed.Retired"/>
    /// is empty. Compare the entry with the region's entries by <see cref="NavigationEntry.Id"/>.
    /// </para>
    /// </remarks>
    /// <param name="region">The region to present, one of <see cref="Regions"/>.</param>
    /// <param name="options">The request's options.</param>
    /// <param name="cancellationToken">Cancels the request before it commits.</param>
    /// <exception cref="ArgumentException"><paramref name="region"/> is not one of <see cref="Regions"/>.</exception>
    public ValueTask<NavigationResult<object>> SelectAsync(NavigationRegion region, NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        RequireMember(region);
        return Run(NavigationOperation.Select, region, options, cancellationToken);
    }

    /// <summary>
    /// Appends <paramref name="region"/>, a live region of this selection's navigator that the selection's owner owns and
    /// that is in no selection. When the selection was empty, the region becomes selected; nothing departs, and no guard
    /// is asked.
    /// </summary>
    /// <remarks>
    /// It supersedes no pending request of the selection. <see cref="PropertyChanged"/> is raised on the caller's thread
    /// before the method returns, so call it where the selection's state may change, for example in a command handler.
    /// </remarks>
    /// <param name="region">The region to append.</param>
    /// <exception cref="ArgumentException">
    /// The region belongs to another navigator, is closed, has another owner, or is already in a selection.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The selection or its navigator is closed.</exception>
    public void Add(NavigationRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        Core.Unit.Navigator.AddToSelection(this, region);
    }

    /// <summary>
    /// Removes <paramref name="region"/> and closes it, as a document tab closes. Every entry of the region, and of the
    /// regions its owned content owns, is asked as <see cref="NavigationDepartureKind.Retire"/>, deepest first, with
    /// <see cref="NavigationOperation.Clear"/>, then the region and navigator guards. A veto changes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It supersedes the pending requests of the region, as a parent's request does for a child region it closes, and the
    /// earlier pending requests of the selection. When the region was selected, the next region becomes selected, or the
    /// previous one when it was the last, or none when it was the only one; the newly selected region is not asked.
    /// </para>
    /// <para>
    /// The commit closes the region, so later requests to it are <see cref="NavigationRejection.Closed"/>, replaces
    /// <see cref="Regions"/>, and retires the region's entries after the turn, as <see cref="NavigationRegion.ClearAsync"/>
    /// does. <see cref="NavigationRequestOptions.ExpectedCurrent"/> names the region's current entry.
    /// <see cref="NavigationResult{TContent}.Committed.Current"/> is the current entry of the region selected after the
    /// commit, and <see cref="NavigationResult{TContent}.Committed.Retired"/> holds the entries the removal retired.
    /// </para>
    /// </remarks>
    /// <param name="region">The region to remove, one of <see cref="Regions"/>.</param>
    /// <param name="options">The request's options.</param>
    /// <param name="cancellationToken">Cancels the request before it commits.</param>
    /// <exception cref="ArgumentException"><paramref name="region"/> is not one of <see cref="Regions"/>.</exception>
    public ValueTask<NavigationResult<object>> RemoveAsync(NavigationRegion region, NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        RequireMember(region);
        return Run(NavigationOperation.Clear, region, options, cancellationToken);
    }

    /// <summary>
    /// Raised inside a model turn for <see cref="Regions"/>, <see cref="Selected"/>, <see cref="SelectedIndex"/>,
    /// <see cref="IsTransitioning"/> and <see cref="IsBusy"/>; by <see cref="Add"/>, on the caller's thread. Each handler is
    /// called separately; one that throws is logged and does not skip the others.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public override string ToString() => $"NavigationSelection #{Core.Unit.Id}";

    private ValueTask<NavigationResult<object>> Run(NavigationOperation operation, NavigationRegion region,
        NavigationRequestOptions? options, CancellationToken cancellationToken) =>
        NavigationResults.MapAsync<object>(Core.Unit.Navigator.Start(Core.Unit, operation, null, null, options?.ExpectedCurrent,
            cancellationToken, admission: options?.Admission, requester: options?.Requester, member: region.Core));

    private void RequireMember(NavigationRegion region)
    {
        if (!Core.State.Regions.Contains(region))
            throw new ArgumentException($"{region} is not one of the regions of {this}.", nameof(region));
    }

    private void Raise(NavigationRegionChanges changes)
    {
        if ((changes & NavigationRegionChanges.Regions) != 0) Raise(RegionsChanged);
        if ((changes & NavigationRegionChanges.Selected) != 0) Raise(SelectedChanged);
        if ((changes & NavigationRegionChanges.SelectedIndex) != 0) Raise(SelectedIndexChanged);
        if ((changes & NavigationRegionChanges.Transitioning) != 0) Raise(IsTransitioningChanged);
        if ((changes & NavigationRegionChanges.Busy) != 0) Raise(IsBusyChanged);
    }

    // D-13: one throwing handler must not skip the others or undo the commit.
    private void Raise(PropertyChangedEventArgs args)
    {
        var handlers = PropertyChanged;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((PropertyChangedEventHandler)handler)(this, args); }
            catch (Exception error)
            {
                NavigationLog.NavigationNotificationFailed(Core.Unit.Navigator.Logger, error, Core.Unit.ContentTypeName, Core.Unit.Id,
                    args.PropertyName ?? string.Empty, NavigationTelemetry.ErrorType(error));
            }
        }
    }
}

// The regions of a selection and the index of the selected one: one immutable record, replaced on change, so a reader
// never pairs the regions with another reader's index.
internal sealed record NavigationSelectionState(ReadOnlyCollection<NavigationRegion> Regions, int SelectedIndex)
{
    public NavigationRegion? Selected => SelectedIndex < 0 ? null : Regions[SelectedIndex];
}

// The engine side of a selection. Its unit is the selection's admission unit: a region core without entries whose
// transitions are the selection's changes, admitted, superseded, made busy and logged like a region's ("NavigationSelection"
// with the unit's id). The unit is owned by the selection's owner, so it is one of the owner's regions (it closes with
// them) and sits in the region graph between the owner's region and the selection's regions (AncestorsLocked). The state
// changes only under the navigator's gate: in the commit turn of a selection change, or in Add.
internal sealed class NavigationSelectionCore
{
    private volatile NavigationSelectionState _state;

    public NavigationSelectionCore(RunicNavigator navigator, int id, object owner, NavigationAdmission admission,
        NavigationRegion[] regions, int selectedIndex)
    {
        Unit = new NavigationRegionCore(navigator, id, nameof(NavigationSelection), owner, NavigationChildRetention.Keep, admission)
        {
            SelectionUnit = this,
        };
        _state = new(Array.AsReadOnly(regions), regions.Length == 0 ? -1 : selectedIndex);
    }

    public NavigationRegionCore Unit { get; }

    public NavigationSelectionState State => _state;

    // The selected region's core, or null when the selection is empty.
    public NavigationRegionCore? Selected => _state.Selected?.Core;

    // Presents a region of the selection. Call under the gate.
    public NavigationRegionChanges SelectLocked(NavigationRegionCore region)
    {
        var state = _state;
        var index = IndexOf(state, region);
        if (index < 0 || index == state.SelectedIndex) return NavigationRegionChanges.None;
        _state = state with { SelectedIndex = index };
        return NavigationRegionChanges.Selected | NavigationRegionChanges.SelectedIndex;
    }

    // Appends a region; the first region of an empty selection becomes selected. Call under the gate.
    public NavigationRegionChanges AddLocked(NavigationRegion region)
    {
        var state = _state;
        NavigationRegion[] regions = [.. state.Regions, region];
        if (state.SelectedIndex >= 0)
        {
            _state = new(Array.AsReadOnly(regions), state.SelectedIndex);
            return NavigationRegionChanges.Regions;
        }
        _state = new(Array.AsReadOnly(regions), 0);
        return NavigationRegionChanges.Regions | NavigationRegionChanges.Selected | NavigationRegionChanges.SelectedIndex;
    }

    // Drops a region. When it was selected, the next region becomes selected, or the previous one when it was the last.
    // Call under the gate.
    public NavigationRegionChanges RemoveLocked(NavigationRegionCore region)
    {
        var state = _state;
        var index = IndexOf(state, region);
        if (index < 0) return NavigationRegionChanges.None;
        NavigationRegion[] regions = [.. state.Regions.Where((_, position) => position != index)];
        var selected = state.SelectedIndex;
        var changes = NavigationRegionChanges.Regions;
        if (selected == index)
        {
            changes |= NavigationRegionChanges.Selected;
            selected = Math.Min(index, regions.Length - 1);
            if (selected != index) changes |= NavigationRegionChanges.SelectedIndex;
        }
        else if (selected > index)
        {
            selected--;
            changes |= NavigationRegionChanges.SelectedIndex;
        }
        _state = new(Array.AsReadOnly(regions), selected);
        return changes;
    }

    private static int IndexOf(NavigationSelectionState state, NavigationRegionCore region)
    {
        for (var index = 0; index < state.Regions.Count; index++)
            if (ReferenceEquals(state.Regions[index].Core, region)) return index;
        return -1;
    }
}
