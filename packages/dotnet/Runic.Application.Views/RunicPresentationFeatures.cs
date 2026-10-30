using System.Runtime.CompilerServices;
using Runic.Application.Views.Generated;

namespace Runic.Application.Views;

/// <summary>
/// Renews and observes the presentations of a ViewModel in the pages of every window that presents it
/// (Views protocol 4, features <c>presentation-epoch/1</c> and <c>presented/1</c>).
/// </summary>
/// <remarks>
/// A ViewModel calls these with itself, from any thread, and needs no reference to its window. Pages and hosts of
/// older releases do not use the features: <see cref="Renew(object)"/> then only publishes the state again, and
/// <see cref="WaitForPresentedAsync(object, TimeSpan, CancellationToken)"/> returns <see langword="false"/> at its
/// timeout.
/// </remarks>
public static class RunicPresentation
{
    /// <summary>
    /// Begins a new epoch of every route that presents <paramref name="viewModel"/>, so the calls of its
    /// <see cref="RunicPresentationScopedAttribute"/> commands that a page sent for the previous state are superseded:
    /// they do not run and resolve with the current state.
    /// </summary>
    /// <remarks>
    /// Call it when the page shows something new without the ViewModel being presented again, such as a wizard's Next
    /// and Back, or when a cancelled dialog returns to its page. Scoped calls that arrive from now on are checked
    /// against the new epoch; the route publishes it as a full state with its next change, or the reply of the call
    /// that renewed it. A ViewModel that nothing presents is not affected: its next presentation begins an epoch anyway.
    /// </remarks>
    /// <param name="viewModel">The presented ViewModel.</param>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is <see langword="null"/>.</exception>
    public static void Renew(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        foreach (var route in BridgePresentationRegistry.Routes(viewModel)) route.Renew();
    }

    /// <summary>
    /// Waits until a page painted the state of <paramref name="viewModel"/> that includes everything changed before
    /// this call, such as a result page that the application presents before it closes a window or exports a picture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pending changes of every route that presents the ViewModel are published first, as before a call's reply.
    /// The wait completes with <see langword="true"/> once a page reports that it rendered one of those routes at that
    /// state or a later one. A ViewModel that no route presents yet counts with the first state of the first route that
    /// presents it, so <c>Page = result; await RunicPresentation.WaitForPresentedAsync(result, …)</c> works before the
    /// parent published <c>Page</c>.
    /// </para>
    /// <para>
    /// A page that is hidden or minimized still reports, after its next timer. The timeout runs on the window's
    /// <see cref="WindowContentSessionOptions.TimeProvider"/>.
    /// </para>
    /// </remarks>
    /// <param name="viewModel">The ViewModel that a page presents or is about to present.</param>
    /// <param name="timeout">How long to wait, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>
    /// <see langword="true"/> when a page painted the state; <see langword="false"/> at the timeout, also when no page
    /// that uses <c>presented/1</c> presents the ViewModel.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative and not infinite.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async ValueTask<bool> WaitForPresentedAsync(object viewModel, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout is negative.");
        cancellationToken.ThrowIfCancellationRequested();
        var wait = new BridgePresentedWait();
        var routes = BridgePresentationRegistry.BeginWait(viewModel, wait);
        try
        {
            foreach (var route in routes) route.AddPresentedWait(wait);
            var clock = routes.Count > 0 ? routes[0].TimeProvider : TimeProvider.System;
            try { return await wait.Task.WaitAsync(timeout, clock, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { return false; }
        }
        finally
        {
            wait.Complete(false);
            BridgePresentationRegistry.EndWait(viewModel, wait);
        }
    }
}

// One WaitForPresentedAsync: the first route that a page reports painted completes it with true; the caller completes it
// with false when it ends otherwise, which tells the routes to drop it.
internal sealed class BridgePresentedWait
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<bool> Task => _result.Task;

    internal bool Complete(bool presented) => _result.TrySetResult(presented);
}

// A Bridge route, as RunicPresentation reaches it.
internal interface IBridgePresentationRoute
{
    TimeProvider TimeProvider { get; }

    void Renew();

    void AddPresentedWait(BridgePresentedWait wait);

    void AddFirstStateWait(BridgePresentedWait wait);
}

// A Bridge, or what wraps it, whose route's open presentations resumed on it after a suspension.
internal interface IBridgeResumedPresentations
{
    void PresentationsResumed();
}

// The live Bridges of each ViewModel, in every window, and the presented waits for a ViewModel that no route presents
// yet. Keyed weakly by the model, as the model gates are (BridgeModelGates).
internal static class BridgePresentationRegistry
{
    private static readonly ConditionalWeakTable<object, Entry> Entries = new();

    // Registers a Bridge of the model when it starts serving its route. It takes the waits for a model that no route
    // presented before.
    internal static void Add(object model, IBridgePresentationRoute route)
    {
        var entry = Entries.GetValue(model, static _ => new());
        BridgePresentedWait[] waits;
        lock (entry)
        {
            entry.Routes.Add(route);
            waits = [.. entry.Waits];
            entry.Waits.Clear();
        }
        foreach (var wait in waits)
            if (!wait.Task.IsCompleted) route.AddFirstStateWait(wait);
    }

    internal static void Remove(object model, IBridgePresentationRoute route)
    {
        if (!Entries.TryGetValue(model, out var entry)) return;
        lock (entry) entry.Routes.Remove(route);
    }

    internal static IReadOnlyList<IBridgePresentationRoute> Routes(object model)
    {
        if (!Entries.TryGetValue(model, out var entry)) return [];
        lock (entry) return [.. entry.Routes];
    }

    // The routes that present the model now; without one, the wait goes to the first route that will.
    internal static IReadOnlyList<IBridgePresentationRoute> BeginWait(object model, BridgePresentedWait wait)
    {
        var entry = Entries.GetValue(model, static _ => new());
        lock (entry)
        {
            if (entry.Routes.Count == 0) entry.Waits.Add(wait);
            return [.. entry.Routes];
        }
    }

    internal static void EndWait(object model, BridgePresentedWait wait)
    {
        if (!Entries.TryGetValue(model, out var entry)) return;
        lock (entry) entry.Waits.Remove(wait);
    }

    private sealed class Entry
    {
        internal List<IBridgePresentationRoute> Routes { get; } = [];
        internal List<BridgePresentedWait> Waits { get; } = [];
    }
}
