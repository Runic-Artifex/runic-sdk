using HostedInstaller;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Desktop;
using Runic.Navigation;

// --elevated: the elevated pair (README, "Elevated"). --probe: CI's check on a real desktop (InstallerProbe.cs).
var elevated = args.Contains("--elevated");
var failures = args.Contains("--probe") ? new ProbeLog() : null;

var services = new ServiceCollection();
// Warnings reach the console, such as a check that refused its region (event 2026).
services.AddLogging(logging =>
{
    logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);
    if (failures is null) return;
    // The probe writes the host's log, and the guests' output lines, to standard error at once (ProbeLog).
    logging.AddProvider(failures);
    logging.AddFilter<ProbeLog>(null, LogLevel.Debug);
});
if (failures is not null)
{
    ProbeLog.Write($"start: {string.Join(' ', args)}; elevated: {Environment.IsPrivilegedProcess}; {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
    AppDomain.CurrentDomain.UnhandledException += (_, e) => ProbeLog.Write($"unhandled exception: {e.ExceptionObject}");
    TaskScheduler.UnobservedTaskException += (_, e) => ProbeLog.Write($"unobserved task exception: {e.Exception}");
}
services.AddRunicReactiveModelContext();
// The wizard's steps: one navigator per window, whose departure guards ask the check (InstallerViewModel).
services.AddRunicNavigation();
services.AddSingleton(new InstallerOptions(elevated));
services.AddScoped<InstallerViewModel>();
services.AddRunicViews();
// IRunicHostedApplications, which the wizard starts the check with.
services.AddRunicHostedApplications();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

// A guest shows only in an embedded web view, or in a region of a page in one: on Linux the installer selects GTK 3 and
// WebKitGTK 4.1 (DesktopHostOptions.WithGtk4() from Runic.Desktop.Gtk4 selects GTK 4, whose window host routes no
// drops into a region's frame).
var options = new DesktopHostOptions { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } };
// The installer's page policy. The check's frame comes from [::1] on a port that changes with each run, and a CSP host
// source cannot name an IPv6 address, so frame-src allows http: (without IPv6 the frame comes from 127.0.0.1).
var hostOptions = new RunicDesktopHostOptions
{
    ContentSecurityPolicy = "default-src 'self'; script-src 'self'; frame-src http:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'",
};
return RunicDesktopHost.Run(provider, options, hostOptions, async host =>
{
    // The host-wide events, for every guest and region: here only logged.
    var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("HostedInstaller");
    host.HostedApplications.RegionAttached += (_, e) => logger.LogInformation("{Guest} attached to the region {Region}.", e.Region.Window.Application.Guest.Name, e.Region.Name);
    host.HostedApplications.RegionDetached += (_, e) => logger.LogInformation("{Guest} detached from the region {Region} ({Reason}).", e.Region.Window.Application.Guest.Name, e.Region.Name, e.Reason);

    var windowOptions = new RunicWindowOptions { Width = 760, Height = 560 };
    if (failures is not null) return await InstallerProbe.RunAsync(host, windowOptions, failures, elevated);

    await using var window = await host.OpenWindowAsync<InstallerWindow>(windowOptions);
    await window.WaitForCloseAsync();
    // Returning stops the check if it still runs.
    return 0;
});
