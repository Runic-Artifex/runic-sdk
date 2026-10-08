using System.Diagnostics.CodeAnalysis;

namespace Runic.Navigation;

/// <summary>
/// Optionally implemented by an <see cref="IRunicModelContext"/> that reports when it closes, for example on disposal
/// or when its UI thread shuts down. A navigator whose <see cref="RunicNavigator.ModelContext"/> implements this
/// interface starts closing as soon as the context closes (amendment A6).
/// </summary>
/// <remarks>
/// <para>
/// Without it, a navigator learns that its context closed only when a turn or hook it starts fails with
/// <see cref="ObjectDisposedException"/>. Until then, a request can wait behind an in-flight transition whose hook ignores
/// its cancellation. With it, requests made after the context closed end <see cref="NavigationRejection.Closed"/> at
/// once, and in-flight transitions are cancelled as closed.
/// </para>
/// <para>
/// The navigator detects the interface with a type test on the context object it was given. A decorator that wraps
/// such a context must implement and forward this interface too. The navigator keeps its registration on
/// <see cref="Closed"/> until it is disposed.
/// </para>
/// </remarks>
[Experimental(RunicNavigator.DiagnosticId)]
public interface IRunicModelContextLifetime
{
    /// <summary>
    /// Gets a token that is cancelled when the context closes, after it stopped accepting work and before it rejects the
    /// work it still had queued. The token is never cancelled for any other reason.
    /// </summary>
    /// <remarks>
    /// Callbacks registered on the token run synchronously on the thread that closes the context, which may be the
    /// model's thread while it shuts down. Keep them short and don't block.
    /// </remarks>
    CancellationToken Closed { get; }
}
