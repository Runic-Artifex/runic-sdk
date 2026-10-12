namespace Runic.Application.Views;

/// <summary>
/// The Window whose DI scope resolved the service, for a ViewModel that closes its own Window. Take it in a
/// constructor and close from a command:
/// <code>
/// [RelayCommand]
/// private async Task QuitAsync() =&gt; await _window.RequestCloseAsync();
/// </code>
/// </summary>
/// <remarks>
/// <see cref="RunicWindowServiceCollectionExtensions.AddRunicWindowService"/> registers it per scope.
/// <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/> binds it to the Window it opens before it resolves
/// the Window's ViewModel, on every host, so the ViewModel and the services it shares that scope with need no holder
/// of the Window. <see cref="IRunicWindowService"/> opens other Windows; this service acts on the caller's own. In
/// tests, register a fake that records the request and returns the answer the test needs.
/// </remarks>
public interface IRunicWindowContext
{
    /// <summary>
    /// Asks to close this Window as its close button does, and returns whether it closed, as
    /// <see cref="RunicWindow{TViewModel}.RequestCloseAsync"/> does: with <see cref="RunicWindow{TViewModel}.GuardClose"/>
    /// it first asks <c>CanCloseAsync</c> and the navigation guards, and returns <see langword="false"/> with the
    /// Window open when they keep it.
    /// </summary>
    /// <param name="cancellationToken">Stops this caller's wait; the decision continues for other requests.</param>
    /// <returns><see langword="true"/> when the Window closed; <see langword="false"/> when it stays open.</returns>
    /// <exception cref="InvalidOperationException">
    /// The service was resolved outside the scope of a Window that <c>OpenWindowAsync</c> opened, or the Window has
    /// not opened yet, for example when the ViewModel's constructor calls it.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    ValueTask<bool> RequestCloseAsync(CancellationToken cancellationToken = default);
}

internal sealed class RunicWindowContext : IRunicWindowContext
{
    private IRunicWindow? _window;

    // Called by OpenWindowAsync in the Window's new scope, before it resolves the ViewModel.
    internal void Bind(IRunicWindow window)
    {
        if (Interlocked.CompareExchange(ref _window, window, null) is { } bound && !ReferenceEquals(bound, window))
            throw new InvalidOperationException("The window context is already bound to another Window.");
    }

    public ValueTask<bool> RequestCloseAsync(CancellationToken cancellationToken = default)
    {
        var window = Volatile.Read(ref _window) ?? throw new InvalidOperationException(
            "IRunicWindowContext was resolved outside a Window's scope, so it has no Window to close. Take it in the " +
            "constructor of a ViewModel that OpenWindowAsync resolves, or of a scoped service that ViewModel uses.");
        return window.RequestCloseAsync(cancellationToken);
    }
}
