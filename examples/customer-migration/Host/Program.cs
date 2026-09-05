using System.Reflection;
using CustomerMigration.Domain;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application;
using Runic.Application.Bridge;
using Runic.Application.Desktop;
using Runic.Assets;
using Runic.Assets.Desktop;
using Runic.Desktop;

[assembly: RunicApplicationManifest("CustomerMigration", Version = "1.0.0", Provenance = "reference")]
[assembly: RunicApplicationCapability("desktop")]
[assembly: RunicApplicationArtifact("assets", "runic.assets/1:Runic.Assets.StaticFiles", "Runic.Assets.StaticFiles")]
[assembly: ApplicationBridgeContract("runic.examples.customers", 1, ContractName = "Customers")]

string? data = Environment.GetEnvironmentVariable("RUNIC_CUSTOMERS_FILE");
data ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Runic", "CustomerMigration", "customers.json");
var assets = AssetArchive.ReadEmbedded(Assembly.GetExecutingAssembly());
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
builder.Services.AddSingleton(_ => new CustomerDirectory(data));
builder.Services.AddSingleton(services => new CustomerService(services.GetRequiredService<CustomerDirectory>(), TimeSpan.FromMilliseconds(300)));
await using var application = builder.Build();
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
var running = application.RunAsync(shutdown.Token);
if (args.Contains("--serve"))
{
    while (desktop.Surface is null && !running.IsCompleted) await Task.Delay(20);
    if (desktop.Surface is not null) Console.WriteLine($"Customer editor: {desktop.Surface.Url}");
}
try { await running; } catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
