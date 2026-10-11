global using Xunit;
using TUnit.Core;

// The cases share process-wide state: Trace.Listeners, WebUiApplication's static connection timeout,
// environment variables (browser and GTK probes), launched browser processes, signal handlers and the
// single native event loop. Run them one at a time, as xUnit did with parallelization disabled.
[assembly: NotInParallel]

// Replaces the former two-minute VSTest hang limit (--blame-hang-timeout 2m): a case that runs longer fails.
// TUnit observes the limit only while the case awaits; a case blocked synchronously runs on until the
// session limit (--timeout) ends the run.
[assembly: Timeout(120_000)]
