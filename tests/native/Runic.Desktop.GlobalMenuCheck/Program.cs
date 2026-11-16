using GlobalMenuCheck;
using Microsoft.Extensions.Logging;
using Runic.Desktop;
using Runic.Desktop.Gtk4;

// A manual check of the Linux global menu (KDE Plasma's Global Menu applet or the title bar's application menu
// button). See README.md for the options and the checklist.
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        Usage: dotnet run -- [--gtk4] [--no-global-menu] [--frameless] [--windows N]

          --gtk4             GTK 4 / WebKitGTK 6 instead of GTK 3 / WebKitGTK 4.1
          --no-global-menu   LinuxDesktopOptions.GlobalMenu = false (the bar must stay in the window)
          --frameless        frameless windows (no in-window bar; the global menu should still work)
          --windows N        open N windows at start (default 1)
        """);
    return 0;
}

var settings = new CheckSettings(
    Gtk4: args.Contains("--gtk4"),
    GlobalMenu: !args.Contains("--no-global-menu"),
    Frameless: args.Contains("--frameless"),
    InitialWindows: ReadWindows(args));

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(console => { console.SingleLine = true; console.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));

var app = new MenuCheckApp(settings);
var options = new DesktopHostOptions
{
    Linux = new LinuxDesktopOptions { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41, GlobalMenu = settings.GlobalMenu },
    MenuBar = app.BuildMenuBar(),
    LoggerFactory = loggerFactory,
    DiagnosticSink = app.OnDiagnostic,
    WaitForConnection = false,
};
if (settings.Gtk4) options = options.WithGtk4();

return DesktopEventLoop.Run(options, app.RunAsync);

static int ReadWindows(string[] args)
{
    var index = Array.IndexOf(args, "--windows");
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var count) ? Math.Clamp(count, 1, 8) : 1;
}
