using HostedDashboard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Desktop;

// --elevated: the elevated pair. --probe: CI's check on a real desktop (DashboardProbe.cs).
var elevated = args.Contains("--elevated");
var failures = args.Contains("--probe") ? new ProbeLog() : null;

var services = new ServiceCollection();
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
    // Only an end through the runtime raises it: a process that something else ends shows no such line.
    AppDomain.CurrentDomain.ProcessExit += (_, _) => ProbeLog.Write($"process exit, exit code {Environment.ExitCode}");
}
services.AddRunicReactiveModelContext();
services.AddSingleton(new DashboardOptions(elevated));
services.AddScoped<DashboardViewModel>();
services.AddRunicViews();
services.AddRunicHostedApplications();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

var options = new DesktopHostOptions { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } };
var hostOptions = new RunicDesktopHostOptions
{
    // The page policy of a host that shows components. A component's module and its connection come from the widget's
    // origin on 127.0.0.1, whose port changes with each run: script-src and connect-src admit 127.0.0.1 on any port. That
    // admits any local server's scripts, should the page ever run injected script: keep such a page free of untrusted
    // content. frame-src http: covers a widget that the policy shows in a frame instead, which comes from [::1], an
    // address a CSP host source cannot name. The page's styles are a file of its own ('self').
    ContentSecurityPolicy = "default-src 'self'; script-src 'self' http://127.0.0.1:*; connect-src 'self' ws://127.0.0.1:*; "
        + "frame-src http:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'",
};
return RunicDesktopHost.Run(provider, options, hostOptions, async host =>
{
    var windowOptions = new RunicWindowOptions { Width = 760, Height = 420 };
    if (failures is not null) return await DashboardProbe.RunAsync(host, windowOptions, failures, elevated);

    await using var window = await host.OpenWindowAsync<DashboardWindow>(windowOptions);
    await window.ViewModel.StartWidgetsAsync();
    await window.WaitForCloseAsync();
    // Returning stops the widgets.
    return 0;
});
