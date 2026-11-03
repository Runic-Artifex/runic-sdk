using Microsoft.Extensions.DependencyInjection;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

// Whether the application serves its Windows to a browser instead of showing them (RunicDesktopHostOptions.ServeOnly or
// RUNIC_APPLICATION_SERVE_ONLY=1). AddRunicPlatformServices registers one; each RunicDesktopHost built on that provider
// records its options here. Services that must not act for a headless server (recent documents, power events) decide
// on first use, after the host exists, instead of when the container is built.
internal sealed class DesktopServeOnlyState
{
    private volatile bool _serveOnly;

    internal bool IsServeOnly => _serveOnly || RunicDesktopHost.ServeOnlyRequested;

    internal void Record(bool serveOnly)
    {
        if (serveOnly) _serveOnly = true;
    }

    // The host's record on the provider it opens Windows from, when AddRunicPlatformServices registered one.
    internal static void RecordFor(IServiceProvider services, bool serveOnly) => services.GetService<DesktopServeOnlyState>()?.Record(serveOnly);

    // The service once first used: unavailable for a serve-only application, else the real one.
    internal T Choose<T>(Func<T> create, Func<PlatformUnavailableReason, T> unavailable) =>
        IsServeOnly ? unavailable(PlatformUnavailableReason.BackendUnavailable) : create();
}
