// Shared by Runic.Navigation.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Navigation.ReactiveUI.Reactive.
#if SYSTEM_REACTIVE
using ReactiveUI.Binding.Reactive;
#else
using ReactiveUI.Binding;
#endif

#if SYSTEM_REACTIVE
namespace Runic.Navigation.ReactiveUI.Reactive;
#else
namespace Runic.Navigation.ReactiveUI;
#endif

/// <summary>Creates an <see cref="INavigationRegionGuard"/> that asks through a ReactiveUI interaction.</summary>
public static class ReactiveNavigationRegionGuard
{
    /// <summary>
    /// Creates a region guard whose question is <c>interaction.Handle(prompt(departure))</c>, for a question about a
    /// whole region rather than one page, such as unsaved work anywhere in a window: a handler that sets
    /// <see langword="true"/> lets the transition depart, <see langword="false"/> rejects it as
    /// <see cref="NavigationRejection.Guard"/>. Register the result with <see cref="NavigationRegion.AddDepartureGuard"/>
    /// or <see cref="RunicNavigator.AddDepartureGuard"/>; their <see cref="NavigationRegionGuardOptions"/> decide when it is
    /// asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is asked when <paramref name="hasUnsavedChanges"/> returns <see langword="false"/>. After a yes,
    /// <paramref name="discard"/> runs in the commit turn if the transition commits
    /// (<see cref="NavigationRegionDeparture.OnCommitted"/>), and not at all if it does not. Like any region guard, the
    /// guard is asked again when a transition reruns.
    /// </para>
    /// <para>
    /// The question is asked as <see cref="ReactiveLeaveConfirmation.WithInteraction{TPrompt}"/> asks it: outside model
    /// turns, on the thread the guard runs on; a superseded, cancelled or closed transition disposes the <c>Handle</c>
    /// subscription and the guard returns at once; and the transition fails closed when no handler answers, with
    /// <see cref="NavigationPhase.Guarding"/> and event 1060 (<c>NavigationGuardFailed</c>), as when a handler throws. A
    /// .NET handler registered on the interaction answers, latest first, as in ReactiveUI.
    /// </para>
    /// </remarks>
    /// <typeparam name="TPrompt">The interaction's input.</typeparam>
    /// <param name="interaction">The interaction, typically a get-only property of the shell ViewModel.</param>
    /// <param name="prompt">Creates the question's input for the departure. It is called inside a model turn, once per question.</param>
    /// <param name="hasUnsavedChanges">Returns whether the departure would lose changes. It is called inside a model turn.</param>
    /// <param name="discard">Discards the changes. It runs in the commit turn of a confirmed transition.</param>
    public static INavigationRegionGuard WithInteraction<TPrompt>(IInteraction<TPrompt, bool> interaction,
        Func<NavigationRegionDeparture, TPrompt> prompt, Func<NavigationRegionDeparture, bool> hasUnsavedChanges,
        Action? discard = null)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(hasUnsavedChanges);
        return new InteractionGuard<TPrompt>(interaction, prompt, hasUnsavedChanges, discard);
    }

    private sealed class InteractionGuard<TPrompt>(IInteraction<TPrompt, bool> interaction,
        Func<NavigationRegionDeparture, TPrompt> prompt, Func<NavigationRegionDeparture, bool> hasUnsavedChanges,
        Action? discard) : INavigationRegionGuard
    {
        public async ValueTask<bool> CanDepartAsync(NavigationRegionDeparture departure, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(departure);
            // One turn reads the state and creates the prompt, so the question matches the state it asks about.
            var (ask, input) = await departure.ModelContext.InvokeAsync(
                () => hasUnsavedChanges(departure) ? (true, prompt(departure)) : (false, default(TPrompt)!), cancellationToken);
            if (!ask) return true;
            if (!await ReactiveLeaveConfirmation.AskAsync(interaction, input, cancellationToken)) return false;
            if (discard is not null) departure.OnCommitted(discard);
            return true;
        }
    }
}
