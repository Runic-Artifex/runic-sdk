namespace Runic.Application.Views;

/// <summary>
/// Reports the progress of the operation whose command runs on the current asynchronous flow
/// (Views protocol 4, "Progress").
/// </summary>
/// <remarks>
/// A command started as an operation (<c>start{Command}()</c> in the generated client) runs in the operation's flow,
/// so it can report how far it got without changing its signature. Pages that watch the operation receive the
/// progress as <c>progress</c> on its <c>running</c> status, at most every 100 ms. Outside an operation, including a
/// command invoked through its ordinary awaited call, reporting does nothing.
/// </remarks>
public static class RunicOperationProgress
{
    /// <summary>Reports the fraction of the operation's work that is done.</summary>
    /// <param name="fraction">
    /// A number from 0 to 1. Values outside the range are clamped to it; <see cref="double.NaN"/> is ignored.
    /// </param>
    public static void Report(double fraction) => BridgeOperationProgress.Report(fraction);
}

// The operation whose work runs on the current flow (BridgeOperationRegistry.Run), for RunicOperationProgress.
internal sealed class BridgeOperationProgress(Action<double> report)
{
    private static readonly AsyncLocal<BridgeOperationProgress?> Flow = new();

    // Runs the operation's work with this sink; null outside an operation, so a nested flow does not report to the
    // operation that started it.
    internal static void Enter(BridgeOperationProgress? progress) => Flow.Value = progress;

    internal static void Report(double fraction)
    {
        if (double.IsNaN(fraction) || Flow.Value is not { } progress) return;
        progress._report(Math.Clamp(fraction, 0, 1));
    }

    private readonly Action<double> _report = report;
}
