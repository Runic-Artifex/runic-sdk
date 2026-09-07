using System.Reflection;
using CustomerMigration.Domain;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application;
using Runic.Application.Bridge;
#if RUNIC_CSWEBUI
using Runic.Application.CsWebUi;
#else
using Runic.Application.Desktop;
using Runic.Assets.Desktop;
using Runic.Desktop;
#endif
using Runic.Assets;

[assembly: RunicApplicationManifest("CustomerMigration", Version = "1.0.0", Provenance = "reference")]
[assembly: RunicApplicationCapability("desktop")]
[assembly: RunicApplicationArtifact("assets", "runic.assets/1:Runic.Assets.StaticFiles", "Runic.Assets.StaticFiles")]
[assembly: ApplicationBridgeContract("runic.examples.customers", 1, ContractName = "Customers")]

string? data = Environment.GetEnvironmentVariable("RUNIC_CUSTOMERS_FILE");
data ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Runic", "CustomerMigration", "customers.json");
var assets = AssetArchive.ReadEmbedded(Assembly.GetExecutingAssembly()).WithDevelopmentDocument();
#if RUNIC_CSWEBUI
var host = new CsWebUiApplicationHost(new()
{
    Assets = assets, Title = "Customers · Runic", OpenWindow = !args.Contains("--serve"),
    RequireNativeCloseConfirmation = args.Contains("--native"),
});
var builder = RunicApplication.CreateBuilder(args).UseHost(host);
#else
DesktopApplicationHost? desktop = null;
var native = args.Contains("--native");
desktop = new DesktopApplicationHost(new()
{
    Title = "Customers · Runic",
    OpenWindow = !args.Contains("--serve"),
    Surface = new DesktopSurfaceOptions { ContentHandler = assets.ToDesktopContentHandler() },
    Window = new DesktopWindowOptions
    {
        Browser = native ? BrowserKind.Embedded : BrowserKind.Any,
        ConfirmCloseAsync = native ? async cancellationToken =>
        {
            var surface = desktop?.Surface;
            if (surface is null) return false;
            // Only the presentation owns its draft. Missing/disconnected UI must not imply permission to discard it.
            return await surface.ExecuteJavaScriptAsync(
                "return await window.confirmCustomerClose?.() === true;",
                TimeSpan.FromMinutes(10), cancellationToken: cancellationToken) == "true";
        }
        : null,
    },
});
var builder = RunicApplication.CreateBuilder(args).UseHost(desktop);
#endif
builder.Services.AddSingleton(_ => new CustomerDirectory(data));
builder.Services.AddSingleton(services => new CustomerService(services.GetRequiredService<CustomerDirectory>(), TimeSpan.FromMilliseconds(300)));
await using var application = builder.Build();
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
// Headless verification can request graceful shutdown on every OS through stdin.
if (args.Contains("--serve") && Console.IsInputRedirected)
    _ = Task.Run(() => StopFromInputAsync(shutdown));
var running = application.RunAsync(shutdown.Token);
if (args.Contains("--serve"))
{
#if RUNIC_CSWEBUI
    while (host.Url is null && !running.IsCompleted) await Task.Delay(20);
    if (host.Url is not null) Console.WriteLine($"Customer editor: {host.Url}");
#else
    while (desktop.Surface is null && !running.IsCompleted) await Task.Delay(20);
    if (desktop.Surface is not null) Console.WriteLine($"Customer editor: {desktop.Surface.Url}");
#endif
}
try { await running; } catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }

static async Task StopFromInputAsync(CancellationTokenSource shutdown)
{
    try
    {
        if (await Console.In.ReadLineAsync(shutdown.Token) == "stop") shutdown.Cancel();
    }
    catch (OperationCanceledException) { }
    catch (ObjectDisposedException) { }
}
