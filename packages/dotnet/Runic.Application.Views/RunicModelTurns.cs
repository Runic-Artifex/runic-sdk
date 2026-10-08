using Microsoft.Extensions.Logging;
using Runic.Navigation;

namespace Runic.Application.Views;

// Synchronous entry points for transport routes. They follow the blocking
// contract documented on IRunicModelContext.
internal static class RunicModelTurns
{
    public static void Run(IRunicModelContext context, Action work)
    {
        if (context.IsExecuting) work();
        else context.InvokeAsync(work).AsTask().GetAwaiter().GetResult();
    }

    public static T Run<T>(IRunicModelContext context, Func<T> work) =>
        context.IsExecuting ? work() : context.InvokeAsync(work).AsTask().GetAwaiter().GetResult();

    // Teardown must still release subscriptions and lifetimes when an
    // application-owned context was disposed first. If the context rejected
    // the turn before it started, run it inline under the supplied gate.
    public static void RunForTeardown(IRunicModelContext? context, Action work, object? gate = null)
    {
        if (context is not null)
        {
            var started = 0;
            try
            {
                Run(context, () =>
                {
                    Volatile.Write(ref started, 1);
                    work();
                });
                return;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref started) == 0) { }
        }
        if (gate is null) work();
        else lock (gate) work();
    }
}

// An IDisposable host can be closed by a view callback, which itself runs in a
// model turn. Waiting for that same turn to leave the queue would deadlock.
// Keep the normal synchronous disposal contract for external callers, but let
// the owner turn initiate shutdown and finish naturally when it returns.
internal static class RunicModelContextDisposal
{
    // A shutdown that fails later in the background is logged (1033) to the caller's logger.
    public static void DisposeSynchronously(IRunicModelContext context, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        var shutdown = context.DisposeAsync();
        if (shutdown.IsCompletedSuccessfully) return;
        if (context.IsExecuting)
        {
            _ = ObserveAsync(shutdown, logger);
            return;
        }
        shutdown.AsTask().GetAwaiter().GetResult();
    }

    private static async Task ObserveAsync(ValueTask shutdown, ILogger logger)
    {
        try { await shutdown.ConfigureAwait(false); }
        catch (Exception error)
        { ViewsLog.ModelContextReleaseFailed(logger, error, BridgeTelemetry.ErrorType(error)); }
    }
}
