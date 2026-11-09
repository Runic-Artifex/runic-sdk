using Runic.Desktop;
using Runic.Desktop.Gtk4;

// runic-host/1 window mode (S11) in the built-in web views; see HostedSmoke. With no argument the platform's web view
// (WebView2, WKWebView, WebKitGTK 4.1); with gtk4 WebKitGTK 6 through Runic.Desktop.Gtk4.

// A guest application: it shows no window of its own, so it needs no web view and no event loop.
if (Array.IndexOf(args, HostedSmoke.GuestArgument) is var guest and >= 0 && guest + 2 < args.Length)
    return HostedSmoke.RunGuest(args[guest + 1], args[guest + 2]);

// A stalled native event loop otherwise occupies a runner until the job timeout.
using var watchdog = new Timer(static _ =>
{
    Console.Error.WriteLine("The hosted windows smoke exceeded its 4-minute deadline; see the last logged phase.");
    Environment.Exit(1);
}, null, TimeSpan.FromMinutes(4), Timeout.InfiniteTimeSpan);

var gtk4 = args.Contains("gtk4", StringComparer.Ordinal);
var toolkit = gtk4 ? "gtk4" : OperatingSystem.IsWindows() ? "webview2" : OperatingSystem.IsMacOS() ? "wkwebview" : "gtk3";
var options = (gtk4 ? new DesktopHostOptions().WithGtk4() : new DesktopHostOptions { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } }) with
{
    // Only lets a window connect on a slow runner; the smoke expects every page to connect.
    ConnectionTimeout = TestTimeouts.Scale(TimeSpan.FromSeconds(45)),
    DiagnosticSink = static diagnostic => Console.Error.WriteLine($"Desktop diagnostic: {diagnostic.Category}/{diagnostic.Code}: {diagnostic.Message}"),
};
try
{
    // Before any await: on macOS the AppKit loop owns the main thread.
    var result = HostedSmoke.Run(options, toolkit);
    if (result == 0) Console.WriteLine($"Runic hosted windows smoke passed ({toolkit}).");
    return result;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
