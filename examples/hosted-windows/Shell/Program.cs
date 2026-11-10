using HostedShell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Desktop;
using Runic.Platform;

var services = new ServiceCollection();
// --probe also reads the log for a counter window that did not open (ShellProbe.cs).
var failures = args is ["--probe"] ? new HostedWindowFailures() : null;
// Warnings reach the console, such as a counter window that did not open (event 2019).
services.AddLogging(logging =>
{
    logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);
    if (failures is not null) logging.AddProvider(failures);
});
services.AddRunicReactiveModelContext();
services.AddScoped<ShellViewModel>();
services.AddSingleton<ShellTheme>();
services.AddRunicViews();
// IRunicApplicationLocale: the shell's language, which every guest follows.
services.AddRunicWindowService();
// IRunicHostedApplications, which ShellViewModel starts the counter with.
services.AddRunicHostedApplications();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

// A guest's windows show only in an embedded web view, never in an installed browser: on Linux the shell selects GTK 3
// and WebKitGTK 4.1 (DesktopHostOptions.WithGtk4() from Runic.Desktop.Gtk4 selects GTK 4 and WebKitGTK 6).
var options = new DesktopHostOptions { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } };
return RunicDesktopHost.Run(provider, options, async host =>
{
    // The language: the first of the user's that the shell has. Guests start in it and follow each switch.
    var language = DesktopLanguages.Preferred.Select(tag => tag.Split('-')[0]).FirstOrDefault(ShellViewModel.Languages.Contains) ?? "en";
    await provider.GetRequiredService<IRunicApplicationLocale>().SetLocaleAsync(language);
    // The theme applies to every window of the shell, and so to the counter's: their pages' prefers-color-scheme follows.
    var theme = provider.GetRequiredService<ShellTheme>();
    theme.Changed += (_, _) => _ = host.Desktop.SetColorSchemeAsync(theme.ColorScheme).AsTask();

    // CI's check of the example on a real desktop (the native job's Linux legs, under Xvfb): ShellProbe.cs.
    if (failures is not null) return await ShellProbe.RunAsync(host, failures);

    await using var window = await host.OpenWindowAsync<ShellWindow>(new RunicWindowOptions { Width = 560, Height = 360 });
    await window.WaitForCloseAsync();
    // Returning stops the counter if it still runs.
    return 0;
});
