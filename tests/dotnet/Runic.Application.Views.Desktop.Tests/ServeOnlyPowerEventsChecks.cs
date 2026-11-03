using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;

// B4 review: AddRunicPlatformServices' power events decide on first use, so a host whose options alone make it serve-only
// opens no system bus or window watches.
internal sealed class ServeOnlyPowerEventsChecks
{
    [Test, NotInParallel(Order = 48)]
    public async Task OptionsOnlyServeOnlyMakesPowerEventsUnavailable()
    {
        if (RunicDesktopHost.ServeOnlyRequested) throw new InvalidOperationException("Run the case without RUNIC_APPLICATION_SERVE_ONLY.");
        await using var provider = new ServiceCollection().AddRunicPlatformServices().BuildServiceProvider();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false });
        _ = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions { ServeOnly = true });
        var events = provider.GetRequiredService<IDesktopPowerEvents>();
        var status = DesktopServiceStatus.Get(events);
        Require(status is CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable), $"A serve-only host's power events report {status}.");
        Require(await events.ReadAsync() is PlatformResult<DesktopPowerState>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "A serve-only host read the power state.");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var watch = events.WatchAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
        Require(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopPowerEvent>.Unavailable, "A serve-only host watched power events.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
