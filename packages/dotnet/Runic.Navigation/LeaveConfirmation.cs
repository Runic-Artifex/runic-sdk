using System.Diagnostics.CodeAnalysis;

namespace Runic.Navigation;

/// <summary>
/// A departure guard that asks before unsaved changes are lost, and discards them only when the departure
/// commits. Compose it into a ViewModel that implements <see cref="INavigationDepartureGuard"/> and forward
/// <see cref="INavigationDepartureGuard.CanDepartAsync"/> to <see cref="CanDepartAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// A yes stands while the transition it allowed is unsettled: a guard that runs again for the same entry, for
/// example when a parent transition asks a child's guard that a child transition already asked, does not ask
/// again. When a later request supersedes that transition, the yes moves to the later request. When the
/// transition commits, is rejected, is cancelled or fails, the yes ends and the next departure asks again.
/// When a change in another region (rather than a later request) supersedes it, the yes also ends, so the user
/// may be asked again.
/// </para>
/// <para>
/// <c>discard</c> runs at most once per yes, inside the commit turn of the departure, after the new navigation
/// state is applied and before the regions raise <c>PropertyChanged</c>. It must not pump or await navigation.
/// </para>
/// <para>
/// <see cref="CanDepartAsync"/> continues on the caller's synchronization context, so a <c>confirm</c> delegate
/// that a UI-thread guard calls (a message box or a modal dialog) runs on that thread.
/// </para>
/// </remarks>
[Experimental(RunicNavigator.DiagnosticId)]
public sealed class LeaveConfirmation
{
    private readonly Func<bool> _hasUnsavedChanges;
    private readonly Func<NavigationDeparture, CancellationToken, ValueTask<bool>> _confirm;
    private readonly Action? _discard;
    private readonly bool _askOnRetain;
    // The standing yes: one immutable record, replaced atomically.
    private StandingYes? _standing;

    /// <summary>Creates a leave confirmation.</summary>
    /// <param name="hasUnsavedChanges">Returns whether leaving would lose changes. It is called inside a model turn.</param>
    /// <param name="confirm">
    /// Asks the user and returns whether to leave. It runs in the guard, outside model turns, with the guard's token,
    /// which is cancelled when the departure is superseded, cancelled or the navigator closes.
    /// </param>
    /// <param name="discard">Discards the changes. It runs in the commit turn of a confirmed departure, at most once per yes.</param>
    /// <param name="askOnRetain">
    /// Whether to ask when the entry is only retained, for example when a push covers it. By default only a departure
    /// that retires the entry asks, because a retained entry keeps its state. With it, a confirmed push over the entry
    /// runs <paramref name="discard"/> when the push commits, although the entry stays in the history.
    /// </param>
    public LeaveConfirmation(Func<bool> hasUnsavedChanges, Func<NavigationDeparture, CancellationToken, ValueTask<bool>> confirm,
        Action? discard = null, bool askOnRetain = false)
    {
        ArgumentNullException.ThrowIfNull(hasUnsavedChanges);
        ArgumentNullException.ThrowIfNull(confirm);
        _hasUnsavedChanges = hasUnsavedChanges;
        _confirm = confirm;
        _discard = discard;
        _askOnRetain = askOnRetain;
    }

    /// <summary>
    /// Creates a leave confirmation that asks with a dialog pushed for a <see cref="bool"/> result into
    /// <paramref name="dialogs"/>. Only <see cref="NavigationCompletion{TResult}.Completed"/> with <see langword="true"/>
    /// confirms; a dismissal, a rejected push or <see langword="false"/> keeps the entry. The guard's token is passed to
    /// the push, so a superseded departure and a closing navigator dismiss the dialog.
    /// </summary>
    /// <remarks>
    /// The dialog region must not be the guarded region, one of its ancestors or one of its descendants: the push would be
    /// <see cref="NavigationRejection.Reentrant"/>, its completion <see cref="NavigationCompletion{TResult}.Dismissed"/>,
    /// and the guard would veto. The dialog completes with <see cref="NavigationEntryContext.CompleteAsync{TResult}"/>
    /// and cancels with <see cref="NavigationEntryContext.DismissAsync"/>.
    /// </remarks>
    /// <typeparam name="TDialog">The dialog region's content type.</typeparam>
    /// <param name="dialogs">The region that presents the dialog.</param>
    /// <param name="dialog">Creates the dialog's target, once per question.</param>
    /// <param name="hasUnsavedChanges">Returns whether leaving would lose changes. It is called inside a model turn.</param>
    /// <param name="discard">Discards the changes in the commit turn of a confirmed departure.</param>
    public static LeaveConfirmation InDialog<TDialog>(NavigationRegion<TDialog> dialogs, Func<INavigationTarget<TDialog>> dialog,
        Func<bool> hasUnsavedChanges, Action? discard = null) where TDialog : class
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(dialog);
        return new LeaveConfirmation(hasUnsavedChanges, async (_, cancellationToken) =>
            await dialogs.PushForResult<bool>(dialog(), cancellationToken: cancellationToken).Completion
                is NavigationCompletion<bool>.Completed { Value: true }, discard);
    }

    /// <summary>
    /// Decides a departure: allows a retaining departure unless <c>askOnRetain</c> is set, allows a departure that a
    /// standing yes covers, allows when there are no unsaved changes, and otherwise asks. A yes registers
    /// <c>discard</c> with <see cref="NavigationDeparture.OnCommitted"/>.
    /// </summary>
    /// <param name="departure">The departure the guard received.</param>
    /// <param name="cancellationToken">The guard's token.</param>
    public async ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(departure);
        // 1. A retained entry keeps its state, so nothing is lost.
        if (departure.Kind == NavigationDepartureKind.Retain && !_askOnRetain) return true;

        // 2. A yes that still stands for this entry answers again without asking.
        if (Current(departure.Entry) is { } standing)
        {
            RegisterDiscard(departure, standing.Discarded);
            return true;
        }

        // 3. Read the state in a model turn: guards run outside turns.
        if (!await departure.ModelContext.InvokeAsync(_hasUnsavedChanges, cancellationToken)) return true;

        // 4. Ask.
        if (!await _confirm(departure, cancellationToken)) return false;

        // 5. Record the yes for this entry and transition, and discard only if the departure commits.
        var yes = new StandingYes(departure.Entry, departure.Transition, departure.Settled, new DiscardOnce());
        Interlocked.Exchange(ref _standing, yes);
        RegisterDiscard(departure, yes.Discarded);
        return true;
    }

    private void RegisterDiscard(NavigationDeparture departure, DiscardOnce discarded)
    {
        if (_discard is not { } discard) return;
        departure.OnCommitted(() =>
        {
            if (discarded.TryClaim()) discard();
        });
    }

    // The standing yes for this entry, following supersessions to the transition that now carries it,
    // or null when it has ended. A yes stands while its transition is unsettled.
    private StandingYes? Current(NavigationEntryId entry)
    {
        while (true)
        {
            var standing = Volatile.Read(ref _standing);
            if (standing is null || standing.Entry != entry) return null;
            if (!standing.Settled.IsCompleted) return standing;
            var settlement = standing.Settled.Result;
            StandingYes? next = settlement is { Outcome: NavigationDepartureOutcome.Superseded, SupersededBy: { } by, Superseder: { } superseder }
                ? standing with { Transition = by, Settled = superseder }
                : null;
            // Another thread may have replaced the record; then read it again.
            if (ReferenceEquals(Interlocked.CompareExchange(ref _standing, next, standing), standing) && next is null) return null;
        }
    }

    private sealed record StandingYes(NavigationEntryId Entry, NavigationTransitionId Transition,
        Task<NavigationDepartureSettlement> Settled, DiscardOnce Discarded);

    // Shared by every departure a yes covers, also after the yes moved to a superseding transition.
    private sealed class DiscardOnce
    {
        private int _claimed;
        public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
    }
}
