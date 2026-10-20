namespace Runic.Desktop;

// The experimental API families of Runic.Desktop (docs/experimental.md). The IDs number from 100 because the
// Runic.Desktop build targets already report RUNICDESK001 and RUNICDESK002.
internal static class DesktopExperimental
{
    // Custom window host providers: the window-host SPI, its options, and the process-wide Linux toolkit claim.
    internal const string WindowHostId = "RUNICDESK100";

    // The low-level presentation transport: capabilities, sessions and scripts on a DesktopSurface.
    internal const string PresentationTransportId = "RUNICDESK101";
}
