using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Desktop;
using Runic.Platform;
using Runic.Platform.Runtime;
using StatusIconSample;

const string ApplicationId = "dev.runic.examples.StatusIcon";

// A second launch hands its arguments to the running instance and ends.
var claim = await ApplicationInstance.ClaimAsync(ApplicationId, ApplicationLaunch.FromProcess(args));
if (claim is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded }) return 0;
// Without a per-user runtime directory the sample still runs, but every launch starts its own instance.
await using var instance = (claim as PlatformResult<ApplicationInstanceClaim>.Success)?.Value is ApplicationInstanceClaim.Primary primary
    ? primary.Instance : null;

var services = new ServiceCollection();
services.AddRunicReactiveModelContext();
services.AddScoped<CounterViewModel>();
services.AddRunicBridges();
// Registers the application's IDesktopStatusIcon and IDesktopLoginStartup, among the other platform services.
services.AddRunicPlatformServices(ApplicationId);
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

// The application starts without a window: only the icon in the desktop's status area.
return RunicDesktopHost.Run(provider, new DesktopHostOptions(), async host =>
{
    var icon = provider.GetRequiredService<IDesktopStatusIcon>();
    var startup = provider.GetRequiredService<IDesktopLoginStartup>();
    // Activations and launches arrive on worker threads; hand them to this loop, which owns the windows.
    var events = Channel.CreateUnbounded<object>();
    icon.Activated += (_, activation) => events.Writer.TryWrite(activation);
    if (instance is not null) instance.Launched += (_, launch) => events.Writer.TryWrite(launch);

    // Null while the desktop cannot say, for example without a login startup provider.
    bool? startsAtLogin = await startup.IsEnabledAsync() is PlatformResult<bool>.Success { Value: var enabled } ? enabled : null;
    DesktopStatusIconState State() => new(new DesktopIcon { ThemeName = "applications-system" }, "Status icon sample")
    {
        Tooltip = "Click to open the counter",
        Menu =
        [
            new DesktopMenuCommand("open", "Open window"),
            new DesktopMenuCommand("login", "Start at login") { Checked = startsAtLogin ?? false, Enabled = startsAtLogin is not null },
            new DesktopMenuSeparator(),
            new DesktopMenuCommand("quit", "Quit"),
        ],
    };
    if (await icon.ShowAsync(State()) is not PlatformResult<PlatformUnit>.Success)
        Console.Error.WriteLine("No status area is showing the icon yet; it appears once one starts.");

    IRunicWindow? window = null;
    Task? closed = null;
    await foreach (var item in events.Reader.ReadAllAsync())
    {
        if (item is DesktopStatusIconActivation { MenuItemId: "quit" }) break;
        if (item is DesktopStatusIconActivation { MenuItemId: "login" })
        {
            var changed = startsAtLogin == true
                ? await startup.DisableAsync()
                : await startup.EnableAsync(new LoginStartupRequest([]) { Reason = "Show the status icon after you log in." });
            if (changed is PlatformResult<PlatformUnit>.Success) startsAtLogin = startsAtLogin != true;
            else Console.Error.WriteLine($"Start at login was not changed: {changed}");
            await icon.ShowAsync(State());
            continue;
        }
        if (closed is { IsCompleted: true }) { await window!.DisposeAsync(); window = null; }
        // The primary click, "Open window" and a second launch open the counter, or bring the open one forward.
        var context = (item as ApplicationLaunch)?.PlatformContext;
        if (item is ApplicationLaunch || item is DesktopStatusIconActivation { Kind: DesktopStatusIconActivationKind.Primary } or DesktopStatusIconActivation { MenuItemId: "open" })
        {
            if (window is null)
            {
                window = await host.OpenWindowAsync<CounterWindow>(new RunicWindowOptions { Width = 480, Height = 320 });
                closed = window.WaitForCloseAsync().AsTask();
            }
            else if (window.Host is DesktopBridgeWindow<CounterViewModel> open)
                await PlatformServices.PresentAsync(open.NativeOwner, context);
        }
    }
    if (window is not null) await window.DisposeAsync();
    await icon.HideAsync();
    return 0;
});
