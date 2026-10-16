namespace Runic.Desktop.Internal;

// What an embedded WebKitGTK window does when its web process ends (#313, #318). A crashed web process
// leaves a blank or frozen view that nothing reports, so the window reports the termination and reloads its
// page; a page that keeps crashing it, for example when a screen reader asks for something WebKit aborts
// on, stays down after a few reloads instead of looping. Shared by the GTK 3 host and the GTK 4 provider,
// which links this file.
internal sealed class WebKitGtkWebProcess
{
    internal const string TerminatedCode = "web-process-terminated";

    // WebKitWebProcessTerminationReason, the same in webkit2gtk-4.1 and webkitgtk-6.0.
    internal const int Crashed = 0;
    internal const int ExceededMemoryLimit = 1;
    internal const int TerminatedByApi = 2;

    internal const int MaximumReloads = 3;
    internal static readonly TimeSpan ReloadWindow = TimeSpan.FromMinutes(1);

    private readonly Queue<long> _reloads = new();

    // Called on the window's GTK thread. Returns whether to reload, and the diagnostic to report.
    internal (bool Reload, DesktopDiagnostic Diagnostic) Terminated(int reason, long timestampMilliseconds)
    {
        while (_reloads.TryPeek(out var reload) && timestampMilliseconds - reload >= (long)ReloadWindow.TotalMilliseconds)
        {
            _reloads.Dequeue();
        }

        var what = reason switch
        {
            Crashed => "crashed",
            ExceededMemoryLimit => "exceeded its memory limit",
            TerminatedByApi => "was terminated by the application",
            _ => $"ended for reason {reason}",
        };
        var category = reason == ExceededMemoryLimit ? DesktopErrorCategory.LimitExceeded : DesktopErrorCategory.OperationFailed;
        // A termination the application asked for is its own; it reloads the view when it wants to.
        if (reason == TerminatedByApi)
        {
            return (false, Diagnostic(category, $"The window's WebKitGTK web process {what}; the page was not reloaded.",
                retryable: false, DesktopDiagnosticSeverity.Warning));
        }

        if (_reloads.Count >= MaximumReloads)
        {
            return (false, Diagnostic(category,
                $"The window's WebKitGTK web process {what} again, after {MaximumReloads} reloads within a minute; the page " +
                "was not reloaded and stays blank until the window is reopened.",
                retryable: false, DesktopDiagnosticSeverity.Error));
        }

        _reloads.Enqueue(timestampMilliseconds);
        return (true, Diagnostic(category,
            $"The window's WebKitGTK web process {what}; the page was reloaded (reload {_reloads.Count} of at most " +
            $"{MaximumReloads} within a minute).",
            retryable: true, DesktopDiagnosticSeverity.Warning));
    }

    private static DesktopDiagnostic Diagnostic(DesktopErrorCategory category, string message, bool retryable,
        DesktopDiagnosticSeverity severity) =>
        new(category, TerminatedCode, message, retryable,
            Remediation: "Look for the WebKitWebProcess crash in the system journal, for example with coredumpctl. " +
                "The Accessibility section of the Runic.Desktop README describes a known WebKitGTK crash and its workaround.")
        {
            Severity = severity,
        };
}
