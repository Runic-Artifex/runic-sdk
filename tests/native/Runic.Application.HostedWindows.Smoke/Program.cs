using Runic.Desktop;
using Runic.Desktop.Gtk4;

// runic-host/1 window mode (S11) in the built-in web views; see HostedSmoke. With no argument the platform's web view
// (WebView2, WKWebView, WebKitGTK 4.1); with gtk4 WebKitGTK 6 through Runic.Desktop.Gtk4. With --region-appearance,
// region mode with an appearance override instead (RegionAppearanceSmoke).

// A guest application: it shows no window of its own, so it needs no web view and no event loop.
if (Array.IndexOf(args, HostedSmoke.GuestArgument) is var guest and >= 0 && guest + 2 < args.Length)
    return args[guest + 2] == RegionAppearanceSmoke.GuestBehaviour ? RegionAppearanceSmoke.RunGuest() : HostedSmoke.RunGuest(args[guest + 1], args[guest + 2]);

// --run-unelevated <command> [arguments]: runs a command, such as the host cases, as the runner's user without its
// elevation (Unelevated); an elevated guest refuses hosted mode.
if (args is ["--run-unelevated", .. var command])
    return OperatingSystem.IsWindows() ? Unelevated.Run(command) : throw new PlatformNotSupportedException("--run-unelevated is for Windows.");

// --direct-services (sdk#896): the per-window services of a window the smoke opens itself; see DirectServicesSmoke. It
// needs no unelevated run. Before any await: on macOS the AppKit loop owns the main thread.
if (args.Contains("--direct-services", StringComparer.Ordinal))
{
    using var directWatchdog = new Timer(static _ =>
    {
        Console.Error.WriteLine("The direct services smoke exceeded its 3-minute deadline.");
        Environment.Exit(1);
    }, null, TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);
    try { return DirectServicesSmoke.Run(); }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        return 1;
    }
}

// --elevated-pair: the smoke runs as it is, an elevated host of elevated guests that both allow it
// (RunicGuestStartInfo.AllowElevatedGuest, RunicGuestOptions.AllowElevatedHost); it must run elevated.
if (args.Contains("--elevated-pair", StringComparer.Ordinal))
{
    if (!Environment.IsPrivilegedProcess)
    {
        Console.Error.WriteLine("--elevated-pair needs an elevated process.");
        return 1;
    }
    HostedSmoke.ElevatedPair = true;
}
// Otherwise the smoke itself runs unelevated on an elevated Windows runner.
else if (OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
{
    var self = new List<string> { Environment.ProcessPath ?? throw new InvalidOperationException("The smoke has no process path.") };
    if (string.Equals(Path.GetFileNameWithoutExtension(self[0]), "dotnet", StringComparison.OrdinalIgnoreCase))
        self.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
    self.AddRange(args);
    Console.WriteLine("The smoke runs elevated; it starts itself again without the elevation.");
    return Unelevated.Run(self);
}

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
    // --region-appearance (#917): a guest's Window in a region of the host's page with an appearance override; see
    // RegionAppearanceSmoke. Before any await: on macOS the AppKit loop owns the main thread.
    if (args.Contains(RegionAppearanceSmoke.Argument, StringComparer.Ordinal)) return RegionAppearanceSmoke.Run(options, toolkit);
    var result = HostedSmoke.Run(options, toolkit);
    if (result == 0) Console.WriteLine($"Runic hosted windows smoke passed ({toolkit}{(HostedSmoke.ElevatedPair ? ", elevated pair" : "")}).");
    return result;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
