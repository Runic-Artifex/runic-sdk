using System.Runtime.CompilerServices;

namespace Runic.Navigation.Wpf;

/// <summary>
/// Creates region guards that ask once per window close, for a question about a whole window such as "Discard the
/// unsaved changes in this window?", with <see cref="NavigationHost.GuardWindowClose"/>.
/// </summary>
/// <remarks>
/// <para>
/// A window whose hosts set <see cref="NavigationHost.GuardWindowClose"/> clears its regions one after another when it
/// closes, so a guard registered with <see cref="RunicNavigator.AddDepartureGuard"/> is asked once for each region that
/// has entries. The guards these methods return are asked for the first of those clears only: after a yes, the other
/// clears of the same close depart without asking it, and a no keeps the window open as before. The next close asks
/// again, also after a close that did not complete.
/// </para>
/// <para>
/// Register the guard with <see cref="RunicNavigator.AddDepartureGuard"/>, or with
/// <see cref="NavigationRegion.AddDepartureGuard"/> on the regions the window guards. A close is recognized by the region
/// the window clears: a region guard of a child region that the clear closes is not part of it and is asked as any
/// guard. Its <see cref="NavigationRegionGuardOptions"/> still decide when it is asked; a clear asks guards with the
/// default options.
/// </para>
/// <para>
/// An action registered with <see cref="NavigationRegionDeparture.OnCommitted"/> runs when the clear that asked commits,
/// even when a later region's clear keeps the window open.
/// </para>
/// </remarks>
public static class WindowCloseConfirmation
{
    /// <summary>
    /// Creates a region guard that asks <paramref name="confirm"/> when a window that guards the departing region closes,
    /// once per close, and lets every other departure pass without asking.
    /// </summary>
    /// <param name="confirm">
    /// Asks whether the window may close: <see langword="true"/> lets it close, <see langword="false"/> keeps it open.
    /// It runs as a region guard runs: outside model turns, on the model's thread when the model context schedules hooks;
    /// read model state through <see cref="NavigationRegionDeparture.ModelContext"/>.
    /// </param>
    /// <returns>The guard.</returns>
    public static INavigationRegionGuard Create(Func<NavigationRegionDeparture, CancellationToken, ValueTask<bool>> confirm)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        return new OnceGuard(new Delegated(confirm), askOutsideClose: false);
    }

    /// <summary>
    /// Wraps a region guard, such as one from <c>ReactiveNavigationRegionGuard.WithInteraction</c>, so that a window close
    /// asks it once rather than once per region. Every other departure asks it as before.
    /// </summary>
    /// <param name="guard">The guard.</param>
    /// <returns>The guard that asks <paramref name="guard"/> once per window close.</returns>
    public static INavigationRegionGuard OncePerClose(INavigationRegionGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        return new OnceGuard(guard, askOutsideClose: true);
    }

    private sealed class Delegated(Func<NavigationRegionDeparture, CancellationToken, ValueTask<bool>> confirm) : INavigationRegionGuard
    {
        public ValueTask<bool> CanDepartAsync(NavigationRegionDeparture departure, CancellationToken cancellationToken) =>
            confirm(departure, cancellationToken);
    }

    private sealed class OnceGuard(INavigationRegionGuard guard, bool askOutsideClose) : INavigationRegionGuard
    {
        // The close checks this guard said yes to. Weak: a check ends with its close.
        private readonly ConditionalWeakTable<CloseCheck, object> _confirmed = new();

        public async ValueTask<bool> CanDepartAsync(NavigationRegionDeparture departure, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(departure);
            if (WindowCloseGuard.ClearingCheck(departure.Region) is not { } check)
                return !askOutsideClose || await guard.CanDepartAsync(departure, cancellationToken).ConfigureAwait(false);
            if (_confirmed.TryGetValue(check, out _)) return true;
            if (!await guard.CanDepartAsync(departure, cancellationToken).ConfigureAwait(false)) return false;
            _confirmed.AddOrUpdate(check, check);
            return true;
        }
    }
}
