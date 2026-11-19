namespace Runic.Application.Views;

/// <summary>
/// Reports the progress of the operation whose command runs on the current asynchronous flow
/// (Views protocol 4, "Progress").
/// </summary>
/// <remarks>
/// <para>
/// A command started as an operation (<c>start{Command}()</c> in the generated client) runs in the operation's flow,
/// so it can report how far it got without changing its signature. Pages that watch the operation receive the
/// progress as <c>progress</c> on its <c>running</c> status, at most every 100 ms. Outside an operation, including a
/// command invoked through its ordinary awaited call, reporting does nothing.
/// </para>
/// <para>
/// An asynchronous command can instead take the progress as its parameter, the .NET pattern: a CommunityToolkit
/// <c>[RelayCommand]</c> method <c>Task ImportAsync(IProgress&lt;double&gt; progress, CancellationToken token)</c>, or a
/// ReactiveUI <c>ReactiveCommand&lt;IProgress&lt;double&gt;, TResult&gt;</c>. The Bridge passes it <see cref="Current"/>,
/// so the page sends no input for the parameter, and its generated client calls the command without one.
/// </para>
/// </remarks>
public static class RunicOperationProgress
{
    /// <summary>
    /// Gets a progress reporter bound to the operation whose command runs on the current asynchronous flow, as
    /// <see cref="Report"/> reports. Outside an operation it is a reporter that does nothing.
    /// </summary>
    /// <remarks>
    /// The reporter keeps reporting to that operation from any thread or flow, such as a library method that takes an
    /// <see cref="IProgress{T}"/> and reports from a worker thread, until the operation ends; later reports are
    /// ignored. It reports synchronously, without <see cref="Progress{T}"/>'s posting to a synchronization context.
    /// </remarks>
    public static IProgress<double> Current => BridgeOperationProgress.Capture();

    /// <summary>Reports the fraction of the operation's work that is done.</summary>
    /// <param name="fraction">
    /// A number from 0 to 1. Values outside the range are clamped to it; <see cref="double.NaN"/> is ignored.
    /// </param>
    public static void Report(double fraction) => BridgeOperationProgress.ReportCurrent(fraction);
}

// The operation whose work runs on the current flow (BridgeOperationRegistry.Run), for RunicOperationProgress. It is
// the IProgress<double> that RunicOperationProgress.Current captures.
internal sealed class BridgeOperationProgress(Action<double> report) : IProgress<double>
{
    private static readonly AsyncLocal<BridgeOperationProgress?> Flow = new();
    private static readonly IProgress<double> None = new NoProgress();

    // Runs the operation's work with this sink; null outside an operation, so a nested flow does not report to the
    // operation that started it.
    internal static void Enter(BridgeOperationProgress? progress) => Flow.Value = progress;

    internal static void ReportCurrent(double fraction) => Flow.Value?.Report(fraction);

    // The current operation's progress, or one that does nothing outside an operation.
    internal static IProgress<double> Capture() => Flow.Value ?? None;

    public void Report(double value)
    {
        if (!double.IsNaN(value)) _report(Math.Clamp(value, 0, 1));
    }

    private sealed class NoProgress : IProgress<double>
    {
        public void Report(double value) { }
    }

    private readonly Action<double> _report = report;
}
