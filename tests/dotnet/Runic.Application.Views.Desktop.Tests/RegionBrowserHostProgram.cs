using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;

// This suite's executable as a browser (serve-only) host (host-protocol.md, Browser hosts), for
// hosted-region-browser-smoke.mjs: it serves one Window whose page, from the folder it is given, holds
// <runic-hosted-region name="check">, and shows the window of a guest (HostedGuestProgram's region-page mode) there. It
// prints the page's address as any serve-only application does, and stops after a line on standard input.
internal static class RegionBrowserHostProgram
{
    internal const string Argument = "--region-browser-host";

    private sealed class EmbedPolicy : IRunicHostedWindowPolicy
    {
        public ValueTask<RunicHostedWindowDecision> OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(request is { CanEmbed: true, CanOpenWindow: false }
                ? RunicHostedWindowDecision.Embed("check")
                : RunicHostedWindowDecision.Refuse("The browser host shows windows only in its region."));
    }

    public static int Run(string folder)
    {
        var services = new ServiceCollection();
        // The host Window's ViewModel and its Bridge, as the guest program's.
        services.AddScoped<OrderedCollectionModel>();
        services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
            _ => (transport, model) => new OrderedCollectionBridge(transport, model));
        services.AddRunicWindowService();
        services.AddRunicHostedApplications();
        var provider = services.BuildServiceProvider();
        using var disposal = new AsyncDisposal(provider);
        return RunicDesktopHost.Run(provider, new DesktopHostOptions { WaitForConnection = false },
            new RunicDesktopHostOptions
            {
                ServeOnly = true,
                SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Directory(folder, "index.html") },
            },
            async host =>
            {
                await using var window = await host.OpenWindowAsync<ConformanceMainWindow>();
                await using var guest = await host.HostedApplications.StartAsync(HostedGuestProgram.Guest("region-page"),
                    new RunicHostedApplicationOptions { Policy = new EmbedPolicy() });
                Console.Out.WriteLine("RUNIC_TEST_GUEST_STARTED");
                Console.Out.Flush();
                await window.WaitForCloseAsync();
                return 0;
            });
    }
}
