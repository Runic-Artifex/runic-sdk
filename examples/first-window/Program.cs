using FirstWindow;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

// RUNIC_SMOKE_TRACE=1 (browser-smoke.mjs): without an ILoggerFactory, Runic logs its errors to Trace, which this writes
// to standard error so that the smoke sees them.
if (Environment.GetEnvironmentVariable("RUNIC_SMOKE_TRACE") == "1")
    System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener(useErrorStream: true));

var services = new ServiceCollection();
services.AddScoped<CounterViewModel>();
services.AddRunicViews();

using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true
});

// The main thread waits for WebUI while the application runs; Run cleans up WebUI's native state at the end.
return RunicCsWebUiHost.Run(provider, async host =>
{
    await using var window = await host.OpenWindowAsync<CounterWindow>();
    await window.WaitForCloseAsync();
    return 0;
});
