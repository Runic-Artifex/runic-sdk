using Runic.Sandbox.Probe;

// The sandboxed Runic Desktop application CI runs from its signed bundle (README.md). Each mode opens a real window
// in the App Sandbox and exits 0 when its checks pass, 1 when one fails and 2 for a wrong command line. Later batches
// add their own mode here, such as --document-probe for document replacement (M2).
using var watchdog = new Timer(static _ =>
{
    Console.Error.WriteLine("FAIL the sandbox probe exceeded its 120-second deadline.");
    Environment.Exit(1);
}, null, TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);

if (!OperatingSystem.IsMacOS())
{
    Console.Error.WriteLine("The sandbox probe runs on macOS, in its App Sandbox bundle.");
    return 2;
}
// DesktopEventLoop.Run must start before the first await: the AppKit loop owns the main thread.
return args switch
{
    ["--sandbox-probe", var outside] => SandboxProbe.Run(outside),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Usage: Runic.Sandbox.Probe --sandbox-probe <file outside the container>");
    return 2;
}
