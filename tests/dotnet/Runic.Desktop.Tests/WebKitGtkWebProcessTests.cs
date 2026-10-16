using TUnit.Core;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// What an embedded WebKitGTK window does when its web process ends (#313): the decision the GTK 3 host and the
// GTK 4 provider share. Their signal handlers only reload the view and report the diagnostic; a real crash is not
// reproduced here.
public sealed class WebKitGtkWebProcessTests
{
    private static readonly long Minute = (long)WebKitGtkWebProcess.ReloadWindow.TotalMilliseconds;

    [Test]
    public void ACrashReloadsAFewTimesAMinuteAndThenStops()
    {
        var process = new WebKitGtkWebProcess();
        for (var reload = 1; reload <= WebKitGtkWebProcess.MaximumReloads; reload++)
        {
            var (reloads, diagnostic) = process.Terminated(WebKitGtkWebProcess.Crashed, 1_000 + reload);
            Assert.True(reloads);
            Assert.Equal(WebKitGtkWebProcess.TerminatedCode, diagnostic.Code);
            Assert.Equal(DesktopErrorCategory.OperationFailed, diagnostic.Category);
            Assert.Equal(DesktopDiagnosticSeverity.Warning, diagnostic.Severity);
            Assert.True(diagnostic.Retryable);
            Assert.Contains("crashed; the page was reloaded", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains($"reload {reload} of at most {WebKitGtkWebProcess.MaximumReloads}", diagnostic.Message, StringComparison.Ordinal);
        }

        var (again, stopped) = process.Terminated(WebKitGtkWebProcess.Crashed, 1_000 + Minute - 1);
        Assert.False(again);
        Assert.Equal(DesktopDiagnosticSeverity.Error, stopped.Severity);
        Assert.False(stopped.Retryable);
        Assert.Contains("was not reloaded", stopped.Message, StringComparison.Ordinal);

        // A minute after the first reload, it has room for one more.
        var (later, _) = process.Terminated(WebKitGtkWebProcess.Crashed, 1_001 + Minute);
        Assert.True(later);
        var (fourth, _) = process.Terminated(WebKitGtkWebProcess.Crashed, 1_001 + Minute);
        Assert.False(fourth);
    }

    [Test]
    public void TheReasonNamesTheTermination()
    {
        var (reloads, memory) = new WebKitGtkWebProcess().Terminated(WebKitGtkWebProcess.ExceededMemoryLimit, 0);
        Assert.True(reloads);
        Assert.Equal(DesktopErrorCategory.LimitExceeded, memory.Category);
        Assert.Contains("exceeded its memory limit", memory.Message, StringComparison.Ordinal);

        // The application ended the process itself and reloads when it wants to.
        var (byApplication, requested) = new WebKitGtkWebProcess().Terminated(WebKitGtkWebProcess.TerminatedByApi, 0);
        Assert.False(byApplication);
        Assert.Equal(DesktopDiagnosticSeverity.Warning, requested.Severity);
        Assert.Contains("terminated by the application", requested.Message, StringComparison.Ordinal);

        var (unknown, other) = new WebKitGtkWebProcess().Terminated(7, 0);
        Assert.True(unknown);
        Assert.Contains("ended for reason 7", other.Message, StringComparison.Ordinal);
    }
}
