namespace Runic.Desktop;

// The experimental API family of Runic.Desktop (docs/experimental.md). The IDs number from 100 because the
// Runic.Desktop build targets already report RUNICDESK001 and RUNICDESK002. RUNICDESK100, the custom window
// hosts, graduated in 0.10.0 and is retired.
internal static class DesktopExperimental
{
    // The low-level presentation transport: capabilities, sessions and scripts on a DesktopSurface.
    internal const string PresentationTransportId = "RUNICDESK101";
}
