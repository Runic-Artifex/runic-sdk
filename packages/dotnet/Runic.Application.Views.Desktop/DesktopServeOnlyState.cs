using Microsoft.Extensions.DependencyInjection;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

// Whether the application serves its Windows to a browser instead of showing them (RunicDesktopHostOptions.ServeOnly or
// RUNIC_APPLICATION_SERVE_ONLY=1). AddRunicPlatformServices registers one; each RunicDesktopHost built on that provider
// records its options here. Services that must not act for a headless server (recent documents, power events) decide
// on first use, after the host exists, instead of when the container is built. A hosted application (runic-host/1) runs
// on its host's desktop, so its power events stay live, but its recent documents are unavailable (D055.8).
internal sealed class DesktopServeOnlyState
{
    private volatile bool _serveOnly;
    private volatile bool _hosted;

    internal bool IsServeOnly => _serveOnly || RunicDesktopHost.ServeOnlyRequested;

    internal bool IsHosted => _hosted;

    internal void Record(bool serveOnly, bool hosted = false)
    {
        if (serveOnly) _serveOnly = true;
        if (hosted) _hosted = true;
    }

    // The host's record on the provider it opens Windows from, when AddRunicPlatformServices registered one.
    internal static void RecordFor(IServiceProvider services, bool serveOnly, bool hosted = false) =>
        services.GetService<DesktopServeOnlyState>()?.Record(serveOnly, hosted);

    // The service once first used: unavailable for a serve-only application, and with unavailableWhenHosted for a hosted
    // one too, else the real one.
    internal T Choose<T>(Func<T> create, Func<PlatformUnavailableReason, T> unavailable, bool unavailableWhenHosted = false) =>
        IsServeOnly || (unavailableWhenHosted && IsHosted) ? unavailable(PlatformUnavailableReason.BackendUnavailable) : create();
}
