namespace Runic.Navigation;

/// <summary>
/// Can veto a transition that departs from a whole region, independent of the entries' content: for example a guard
/// that asks once whether to discard unsaved work anywhere in a window. Register it with
/// <see cref="NavigationRegion{TContent}.AddDepartureGuard"/> or <see cref="RunicNavigator.AddDepartureGuard"/>.
/// </summary>
/// <remarks>
/// Region guards run after the departure guards of the entries (<see cref="INavigationDepartureGuard"/>), outside model
/// turns, like them: one at a time, on the model's thread when the model context schedules hooks, and again when a
/// transition reruns. A request from a region guard into its region, an ancestor or a descendant is
/// <see cref="NavigationRejection.Reentrant"/>.
/// </remarks>
public interface INavigationRegionGuard
{
    /// <summary>Returns whether the transition may depart. <see langword="false"/> rejects it as <see cref="NavigationRejection.Guard"/>.</summary>
    /// <param name="departure">The departing region, its departing entries, whether one of them retires, and the operation.</param>
    /// <param name="cancellationToken">Cancelled when the transition is superseded, cancelled or closed.</param>
    ValueTask<bool> CanLeaveAsync(NavigationRegionDeparture departure, CancellationToken cancellationToken);
}

/// <summary>When a region guard is asked.</summary>
public sealed record NavigationRegionGuardOptions
{
    /// <summary>
    /// Gets whether the guard is asked when the transition only retains the region's entries, as a push over its current
    /// entry or a tab switch does. The default is <see langword="false"/>, as for <see cref="LeaveConfirmation"/>.
    /// </summary>
    public bool AskOnRetain { get; init; }

    /// <summary>
    /// Gets whether the guard is asked for a <see cref="NavigationOperation.ClearHistory"/> request, including the child
    /// regions that request closes. The default is <see langword="false"/>: history clean-up never asks. The entries' own
    /// departure guards are asked either way.
    /// </summary>
    public bool AskOnClearHistory { get; init; }
}

/// <summary>
/// Describes a departure from a region that a <see cref="INavigationRegionGuard"/> can veto. The navigator creates one for
/// each guard invocation; it belongs to that invocation and to the transition that runs it.
/// </summary>
public sealed class NavigationRegionDeparture
{
    private readonly NavigationTransition _transition;
    private readonly string _guardType;
    // Set under the navigator's gate when the guard's task has completed.
    private bool _closed;

    internal NavigationRegionDeparture(NavigationTransition transition, INavigationRegion region, NavigationDepartureKind kind,
        IReadOnlyList<NavigationEntryId> entries, string guardType)
    {
        _transition = transition;
        _guardType = guardType;
        Region = region;
        Kind = kind;
        Entries = entries;
    }

    /// <summary>
    /// Gets the departing region. For a guard of <see cref="RunicNavigator.AddDepartureGuard"/>, it is the region the request
    /// was made to.
    /// </summary>
    public INavigationRegion Region { get; }

    /// <summary>Gets the requested operation.</summary>
    public NavigationOperation Operation => _transition.Operation;

    /// <summary>
    /// Gets <see cref="NavigationDepartureKind.Retire"/> when the transition retires a departing entry; otherwise
    /// <see cref="NavigationDepartureKind.Retain"/>.
    /// </summary>
    public NavigationDepartureKind Kind { get; }

    /// <summary>
    /// Gets the departing entries of <see cref="Region"/>, current first. For a guard of
    /// <see cref="RunicNavigator.AddDepartureGuard"/>, the departing entries of every region of the transition, deepest first.
    /// </summary>
    public IReadOnlyList<NavigationEntryId> Entries { get; }

    /// <summary>
    /// Gets the model context whose turns commit the navigator's state. Guards run outside model turns, so they read and
    /// change shared model state through <see cref="IRunicModelContext.InvokeAsync{T}(Func{T}, CancellationToken)"/>.
    /// </summary>
    public IRunicModelContext ModelContext => _transition.Region.Navigator.ModelContext;

    /// <summary>
    /// Runs <paramref name="action"/> if, and only if, this transition commits, like <see cref="NavigationDeparture.OnCommitted"/>:
    /// in its commit turn, after the actions of the entries' guards, in the order the guards were asked.
    /// </summary>
    /// <param name="action">The action to run in the commit turn.</param>
    /// <exception cref="InvalidOperationException">The guard's task has already completed.</exception>
    public void OnCommitted(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_transition.Region.Navigator.Gate)
        {
            if (_closed)
                throw new InvalidOperationException(
                    "OnCommitted can be called only while the region guard runs, before the task it returned completes.");
            _transition.CommitActions.Add((_guardType, action));
        }
    }

    // Called by the navigator once the guard's task has completed.
    internal void Close()
    {
        lock (_transition.Region.Navigator.Gate) _closed = true;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"NavigationRegionDeparture {{ Region = {Region}, Kind = {Kind}, Operation = {Operation}, Entries = {Entries.Count} }}";
}

// One registration of a region or navigator guard. The list it belongs to is guarded by the navigator's gate.
internal sealed class NavigationRegionGuardRegistration(RunicNavigator navigator, List<NavigationRegionGuardRegistration> list,
    INavigationRegionGuard guard, NavigationRegionGuardOptions options) : IDisposable
{
    public INavigationRegionGuard Guard { get; } = guard;
    public NavigationRegionGuardOptions Options { get; } = options;
    // Read and set under the navigator's gate.
    public bool Removed { get; private set; }

    public void Dispose()
    {
        lock (navigator.Gate)
        {
            if (Removed) return;
            Removed = true;
            list.Remove(this);
        }
    }
}
