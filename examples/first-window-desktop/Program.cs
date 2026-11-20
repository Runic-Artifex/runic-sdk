using FirstWindowDesktop;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Desktop;

var services = new ServiceCollection();
services.AddRunicReactiveModelContext();
services.AddScoped<CounterViewModel>();
services.AddRunicBridges();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
var probeOwner = args.Contains("--probe-owner", StringComparer.Ordinal);
// --probe-presented (CI, runic-sdk#1193): the page in the native window (WebKitGTK on Linux) connects and presents the
// counter, and nothing logs an error. Run on the trimmed publish, which leaves Bridge blobs out: this example's Bridges
// use none. Without an ILoggerFactory, Runic logs its errors to Trace, which the probe writes to standard error.
var probePresented = args.Contains("--probe-presented", StringComparer.Ordinal);
if (probePresented) System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener(useErrorStream: true));
var hostOptions = new DesktopHostOptions { WaitForConnection = !probeOwner };

// RUNIC_APPLICATION_SERVE_ONLY=1 makes the host serve the authenticated surface without opening a window,
// for an external browser test (browser-smoke.mjs).
return RunicDesktopHost.Run(provider, hostOptions, async host =>
{
    await using var window = await host.OpenWindowAsync<CounterWindow>(new RunicWindowOptions { Width = 800, Height = 600 });
    if (probeOwner)
    {
        var close = await window.CloseAsync(TimeSpan.FromSeconds(2));
        await close.Completion;
        if (!close.Drained || close.RemainingOperations != 0 || CounterViewModel.Disposals != 1)
            throw new InvalidOperationException("The Desktop Window did not release its ViewModel scope cleanly.");
        Console.WriteLine("DESKTOP_VIEWS_OWNER_OK");
        return 0;
    }
    if (probePresented)
    {
        // The page's Views client reports presented/1 after its hello, the route's open and the first paint.
        if (!await RunicPresentation.WaitForPresentedAsync(window.DataContext!, TimeSpan.FromSeconds(60)))
            throw new InvalidOperationException("The page in the Desktop window did not present the counter within 60 s.");
        Console.WriteLine("DESKTOP_VIEWS_PRESENTED_OK");
        var close = await window.CloseAsync(TimeSpan.FromSeconds(2));
        await close.Completion;
        return 0;
    }
    // The Desktop adapter behind the host-neutral Window exposes the surface and native presentation. A string:
    // writing the Uri object formats it with the current culture, which loads ICU.
    Console.WriteLine(((DesktopBridgeWindow<CounterViewModel>)window.Host).Surface.Url.AbsoluteUri);
    await window.WaitForCloseAsync();
    return 0;
});
