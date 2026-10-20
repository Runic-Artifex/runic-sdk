namespace Runic.Navigation;

/// <summary>
/// Describes an exception that a model context reports through <see cref="IRunicModelContext.UnhandledTurnException"/>:
/// a posted turn that threw, a posted turn that the context dropped when it closed, or a close callback that threw.
/// </summary>
public sealed class RunicModelTurnExceptionEventArgs : EventArgs
{
    /// <summary>Creates the event data.</summary>
    /// <param name="exception">The exception.</param>
    /// <param name="dropped">Whether the context dropped a posted turn instead of running it.</param>
    public RunicModelTurnExceptionEventArgs(Exception exception, bool dropped)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception = exception;
        Dropped = dropped;
    }

    /// <summary>
    /// Gets the exception: the one the turn or callback threw, or an <see cref="ObjectDisposedException"/> for a dropped turn.
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// Gets whether the context dropped a posted turn because it closed before the turn ran. The turn's code never ran.
    /// <see langword="false"/> for a turn or close callback that threw.
    /// </summary>
    public bool Dropped { get; }
}
