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

/// <summary>Creates a <see cref="LeaveConfirmation"/> that asks through a ReactiveUI interaction.</summary>
public static class ReactiveLeaveConfirmation
{
    /// <summary>
    /// Creates a <see cref="LeaveConfirmation"/> whose question is <c>interaction.Handle(prompt())</c>: a handler that sets
    /// <see langword="true"/> lets the entry depart, <see langword="false"/> keeps it. The presentation answers it natively:
    /// a WPF View with <c>RegisterHandler</c> in <c>WhenActivated</c>, a Runic web View with the generated client's
    /// <c>interactions.&lt;name&gt;.handle</c>, a test with <c>RegisterHandler(context =&gt; context.SetOutput(true))</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every <see cref="LeaveConfirmation"/> rule applies: a retaining departure is allowed without asking unless
    /// <paramref name="askOnRetain"/> is set; a yes stands while the departure it allowed is unsettled; nothing is asked
    /// without unsaved changes; <paramref name="discard"/> runs at most once per yes, in the commit turn.
    /// </para>
    /// <para>
    /// When the departure is superseded, cancelled or its navigator closes, the guard's token disposes the <c>Handle</c>
    /// subscription and the guard returns at once. ReactiveUI lets a running .NET handler finish; a Runic web View's
    /// handler receives an aborted <c>signal</c>, because a browser request made by a navigation hook ends with the hook's
    /// token (<see cref="RunicNavigator.HookCancellationToken"/>).
    /// </para>
    /// <para>
    /// The departure fails closed: when no handler answers, ReactiveUI's <c>UnhandledInteractionException</c> fails the
    /// navigation with <see cref="NavigationPhase.Guarding"/>, and the navigator logs <c>NavigationGuardFailed</c> (event 1060)
    /// with that exception type as <c>ErrorType</c>. A handler that throws, or a browser that disconnects while asked, fails
    /// it the same way. The entry stays current.
    /// </para>
    /// <para>
    /// The question is asked in the guard, outside model turns, on the thread the guard runs on: the dispatcher for a WPF
    /// navigator, the thread pool otherwise.
    /// </para>
    /// <para>
    /// A Runic web View's handler answers a departure that its own window starts: a bridged command of any View in that
    /// window, such as the page's Close or the shell's Back, asks the presentation mounted by the same browser
    /// connection; a guarded window close asks the window's one presentation with a mounted handler. A departure that
    /// .NET code starts outside both, such as a timer's navigation, reaches only .NET handlers. Handlers run latest
    /// first, as in ReactiveUI: one registered after the View attached, such as a WPF View's <c>WhenActivated</c>
    /// handler, answers before the web View, and a fallback registered in the ViewModel's constructor answers only
    /// when no View does.
    /// </para>
    /// </remarks>
    /// <typeparam name="TPrompt">The interaction's input, which a web View receives as typed data.</typeparam>
    /// <param name="interaction">The interaction, typically a get-only property of the ViewModel.</param>
    /// <param name="prompt">Creates the question's input. It is called inside a model turn, once per question.</param>
    /// <param name="hasUnsavedChanges">Returns whether leaving would lose changes. It is called inside a model turn.</param>
    /// <param name="discard">Discards the changes. It runs in the commit turn of a confirmed departure, at most once per yes.</param>
    /// <param name="askOnRetain">Whether to ask when the entry is only retained, as in <see cref="LeaveConfirmation"/>.</param>
    public static LeaveConfirmation WithInteraction<TPrompt>(IInteraction<TPrompt, bool> interaction, Func<TPrompt> prompt,
        Func<bool> hasUnsavedChanges, Action? discard = null, bool askOnRetain = false)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ArgumentNullException.ThrowIfNull(prompt);
        return new LeaveConfirmation(hasUnsavedChanges, async (departure, cancellationToken) =>
            await AskAsync(interaction, await departure.ModelContext.InvokeAsync(prompt, cancellationToken), cancellationToken),
            discard, askOnRetain);
    }

    private static async Task<bool> AskAsync<TPrompt>(IInteraction<TPrompt, bool> interaction, TPrompt input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var answer = new Answer();
        var subscription = interaction.Handle(input).Subscribe(answer);
        // Disposing drops the answer; the guard returns without waiting for the handler.
        using var registration = cancellationToken.Register(static state =>
        {
            var (subscription, answer, token) = ((IDisposable, Answer, CancellationToken))state!;
            subscription.Dispose();
            answer.Cancel(token);
        }, (subscription, answer, cancellationToken));
        try { return await answer.Task; }
        finally { subscription.Dispose(); }
    }

    // The first answer, or the failure of the question.
    private sealed class Answer : IObserver<bool>
    {
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> Task => _completion.Task;

        public void Cancel(CancellationToken token) => _completion.TrySetCanceled(token);

        public void OnNext(bool value) => _completion.TrySetResult(value);

        public void OnError(Exception error) => _completion.TrySetException(error);

        public void OnCompleted() => _completion.TrySetException(
            new InvalidOperationException("The interaction completed without an answer."));
    }
}
