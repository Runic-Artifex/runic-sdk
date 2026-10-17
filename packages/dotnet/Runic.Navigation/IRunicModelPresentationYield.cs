namespace Runic.Navigation;

/// <summary>
/// Optionally implemented by an <see cref="IRunicModelContext"/> whose presentation renders after a turn, such as a UI
/// dispatcher context. Before a navigator runs the activation hooks (<see cref="INavigationActivated"/>) of a commit, it
/// awaits <see cref="YieldToPresentationAsync"/>, so the entry is presented, for example with its loading state, before the
/// hook's synchronous part runs.
/// </summary>
/// <remarks>
/// <c>Runic.Navigation.Wpf</c>'s <c>DispatcherModelContext</c> implements it. A decorator that wraps such a context must
/// implement and forward this interface too; otherwise activation hooks may run before the first render.
/// </remarks>
public interface IRunicModelPresentationYield
{
    /// <summary>Completes once the changes made in earlier turns have been presented (laid out and rendered).</summary>
    /// <param name="cancellationToken">Cancelled when the navigator closes.</param>
    ValueTask YieldToPresentationAsync(CancellationToken cancellationToken);
}
