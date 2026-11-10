using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;

// B5 review: AddRunicPlatformServices' recent documents decide on first use, so a host whose options alone make it
// serve-only never writes the desktop's recent documents.
internal sealed class ServeOnlyRecentDocumentsChecks
{
    [Test, NotInParallel(Order = 47)]
    public async Task OptionsOnlyServeOnlyMakesRecentDocumentsUnavailable()
    {
        if (RunicDesktopHost.ServeOnlyRequested) throw new InvalidOperationException("Run the case without RUNIC_APPLICATION_SERVE_ONLY.");
        foreach (var serveOnly in new[] { true, false })
        {
            await using var provider = new ServiceCollection().AddRunicPlatformServices().BuildServiceProvider();
            await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false });
            _ = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions { ServeOnly = serveOnly });
            var recent = provider.GetRequiredService<IDesktopRecentDocuments>();
            var status = DesktopServiceStatus.Get(recent);
            if (serveOnly)
            {
                Require(status is CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable), $"A serve-only host's recent documents report {status}.");
                var added = await recent.AddAsync(Path.GetFullPath("/tmp/runic-serve-only.txt"));
                Require(added is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable), $"A serve-only host noted a document: {added}.");
            }
            else
            {
                Require(status is CapabilityStatus.Available || !(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()),
                    $"A host that shows its Windows reports {status}.");
            }
        }
    }

    // Hosted mode (runic-host/1, D055.8): recent documents are unavailable, as in serve-only mode. RunicDesktopHost.Run
    // records hosted mode; the record is checked directly here.
    [Test, NotInParallel(Order = 47)]
    public async Task HostedModeMakesRecentDocumentsUnavailable()
    {
        if (RunicDesktopHost.ServeOnlyRequested) throw new InvalidOperationException("Run the case without RUNIC_APPLICATION_SERVE_ONLY.");
        await using var provider = new ServiceCollection().AddRunicPlatformServices().BuildServiceProvider();
        provider.GetRequiredService<DesktopServeOnlyState>().Record(serveOnly: false, hosted: true);
        var recent = provider.GetRequiredService<IDesktopRecentDocuments>();
        var status = DesktopServiceStatus.Get(recent);
        Require(status is CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable), $"A hosted application's recent documents report {status}.");
        var added = await recent.AddAsync(Path.GetFullPath("/tmp/runic-hosted.txt"));
        Require(added is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable), $"A hosted application noted a document: {added}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
