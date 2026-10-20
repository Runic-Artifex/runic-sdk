namespace Runic.Platform.Administration.Windows.Internal;

/// <summary>Runs blocking native calls (SCM, NetAPI, DC locator) off the caller's thread.</summary>
internal static class NativeWork
{
    // Cancellation before the call starts prevents it. Once it starts, its real outcome is returned:
    // a native call cannot be interrupted, and a cancellation request never means rollback.
    internal static Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        NativeError.Windows();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(operation, cancellationToken);
    }

    internal static Task RunAsync(Action operation, CancellationToken cancellationToken)
    {
        NativeError.Windows();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(operation, cancellationToken);
    }
}
