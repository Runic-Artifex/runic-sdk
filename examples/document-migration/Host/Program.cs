using System.Reflection;
using DocumentMigration.Domain;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application;
using Runic.Application.Platform;
#if !RUNIC_CSWEBUI
using Runic.Application.Platform.Desktop;
#endif
#if RUNIC_PLATFORM_Linux
using SelectedProvider = Runic.Platform.Linux.LinuxPlatformProvider;
#elif RUNIC_PLATFORM_Windows
using SelectedProvider = Runic.Platform.Windows.WindowsPlatformProvider;
#elif RUNIC_PLATFORM_MacOS
using SelectedProvider = Runic.Platform.MacOS.MacOSPlatformProvider;
#endif
using Runic.Application.Bridge;
#if RUNIC_CSWEBUI
using Runic.Application.CsWebUi;
#else
using Runic.Application.Desktop;
using Runic.Assets.Desktop;
using Runic.Desktop;
#endif
using Runic.Assets;

[assembly: RunicApplicationManifest("DocumentMigration", Version = "1.0.0", Provenance = "reference")]
[assembly: RunicApplicationCapability("desktop")]
[assembly: RunicApplicationArtifact("assets", "runic.assets/1:Runic.Assets.StaticFiles", "Runic.Assets.StaticFiles")]
[assembly: ApplicationBridgeContract("runic.examples.documents", 1, ContractName = "Documents")]

var assets = AssetArchive.ReadEmbedded(Assembly.GetExecutingAssembly()).WithDevelopmentDocument();
#if RUNIC_CSWEBUI
var host = new CsWebUiApplicationHost(new()
{
    Assets = assets, Title = "Documents · Runic", OpenWindow = !args.Contains("--serve"),
    RequireNativeCloseConfirmation = args.Contains("--native"),
});
var builder = RunicApplication.CreateBuilder(args).UseHost(host);
#else
DesktopApplicationHost? desktop = null;
var native = args.Contains("--native");
desktop = new DesktopApplicationHost(new()
{
    Host = new DesktopHostOptions { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } },
    Title = "Documents · Runic",
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
                "return await window.confirmDocumentClose?.() === true;",
                TimeSpan.FromMinutes(10), cancellationToken: cancellationToken) == "true";
        }
        : null,
    },
});
var builder = RunicApplication.CreateBuilder(args).UseHost(desktop);
#endif
#if RUNIC_PLATFORM_Linux || RUNIC_PLATFORM_Windows || RUNIC_PLATFORM_MacOS
if (native)
    builder.Services.AddRunicDesktopPlatform(() => desktop?.Window, owner => new PlatformProvider
    {
#if RUNIC_PLATFORM_Linux
        // These migration examples require atomic sibling replacement on save.
        // Portal grants cover only the chosen file, so keep the explicit native
        // compatibility path until a suitable directory-access policy is available.
        Files = SelectedProvider.CreateGtkNativeFileDialogs(owner),
#else
        Files = SelectedProvider.CreateFileDialogs(owner),
#endif
    });
else builder.Services.AddRunicPlatform();
#else
builder.Services.AddRunicPlatform();
#endif
builder.Services.AddScoped<DocumentService>();
await using var application = builder.Build();
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
// Headless verification can request graceful shutdown on every OS through stdin.
if (args.Contains("--serve") && Console.IsInputRedirected)
    _ = Task.Run(() => StopFromInputAsync(shutdown));
#if !RUNIC_CSWEBUI
if (native && !args.Contains("--serve"))
{
    // Embedded AppKit must enter the host's main-thread pump before the first async suspension.
    try { application.Run(shutdown.Token); }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    return;
}
#endif
var running = application.RunAsync(shutdown.Token);
if (args.Contains("--serve"))
{
#if RUNIC_CSWEBUI
    while (host.Url is null && !running.IsCompleted) await Task.Delay(20);
    if (host.Url is not null) Console.WriteLine($"Document editor: {host.Url}");
#else
    while (desktop.Surface is null && !running.IsCompleted) await Task.Delay(20);
    if (desktop.Surface is not null) Console.WriteLine($"Document editor: {desktop.Surface.Url}");
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
