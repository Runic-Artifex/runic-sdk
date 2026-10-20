namespace Runic.Application.Views;

/// <summary>The admission result of a graceful Window or content-session close.</summary>
/// <param name="Drained">Whether every accepted operation reached a terminal result within the timeout.</param>
/// <param name="RemainingOperations">Operations still running at the timeout. They were asked to cancel.</param>
/// <param name="Completion">
/// Completes when the remaining operations have finished and the closer has released
/// what it owns. A Window host faults it with any release failures.
/// </param>
public sealed record RunicWindowCloseResult(bool Drained, int RemainingOperations, Task Completion);

/// <summary>
/// The lifetime contract shared by every Window host adapter, such as
/// <c>CsWebUiBridgeWindow&lt;TViewModel&gt;</c> and <c>DesktopBridgeWindow&lt;TViewModel&gt;</c>.
/// <see cref="RunicWindow{TViewModel}.Host"/> returns the adapter of an open Window.
/// </summary>
/// <remarks>
/// Hosts release asynchronously: accepted operations may still be running when the
/// visible window closes, and the Window's DI scope stays alive until they finish.
/// <see cref="IAsyncDisposable.DisposeAsync"/> closes immediately and returns after
/// every resource has been released.
/// </remarks>
public interface IBridgeWindow : IAsyncDisposable
{
    /// <summary>
    /// Stops new operation admission and waits up to <paramref name="timeout"/> for
    /// accepted work. At the timeout the visible window and its browser routes close
    /// promptly, while the host keeps its scope until the remaining operations finish.
    /// The first call selects the timeout; later calls return the same result.
    /// </summary>
    /// <param name="timeout">How long accepted operations may run before the window closes anyway.</param>
    /// <param name="cancellationToken">
    /// Stops this caller's wait. The close itself continues, and other callers still receive its result.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative and not infinite.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    ValueTask<RunicWindowCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes when the window closes: the user closed it, or <see cref="CloseAsync"/> or
    /// <see cref="IAsyncDisposable.DisposeAsync"/> started closing it. It does not release the window; dispose it afterwards.
    /// </summary>
    /// <param name="cancellationToken">Stops the wait without closing the window.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default);
}
