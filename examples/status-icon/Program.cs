using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Desktop;
using Runic.Platform;
using StatusIconSample;

var services = new ServiceCollection();
services.AddRunicReactiveModelContext();
services.AddScoped<CounterViewModel>();
services.AddRunicBridges();
// Registers the application's IDesktopStatusIcon, among the other platform services.
services.AddRunicPlatformServices();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

// The application starts without a window: only the icon in the desktop's status area.
return RunicDesktopHost.Run(provider, new DesktopHostOptions(), async host =>
{
    var icon = provider.GetRequiredService<IDesktopStatusIcon>();
    // Activation arrives on a worker thread; hand it to this loop, which owns the windows.
    var activations = Channel.CreateUnbounded<DesktopStatusIconActivation>();
    icon.Activated += (_, activation) => activations.Writer.TryWrite(activation);
    var state = new DesktopStatusIconState(new DesktopIcon { ThemeName = "applications-system" }, "Status icon sample")
    {
        Tooltip = "Click to open the counter",
        Menu = [new DesktopMenuCommand("open", "Open window"), new DesktopMenuSeparator(), new DesktopMenuCommand("quit", "Quit")],
    };
    if (await icon.ShowAsync(state) is not PlatformResult<PlatformUnit>.Success)
        Console.Error.WriteLine("No status area is showing the icon yet; it appears once one starts.");

    IRunicWindow? window = null;
    Task? closed = null;
    await foreach (var activation in activations.Reader.ReadAllAsync())
    {
        if (activation.MenuItemId == "quit") break;
        if (closed is { IsCompleted: true }) { await window!.DisposeAsync(); window = null; }
        // The primary click and "Open window" open the counter, or keep the one already open.
        if (window is null && (activation.Kind == DesktopStatusIconActivationKind.Primary || activation.MenuItemId == "open"))
        {
            window = await host.OpenWindowAsync<CounterWindow>(new RunicWindowOptions { Width = 480, Height = 320 });
            closed = window.WaitForCloseAsync().AsTask();
        }
    }
    if (window is not null) await window.DisposeAsync();
    await icon.HideAsync();
    return 0;
});
