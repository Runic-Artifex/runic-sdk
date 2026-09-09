namespace Runic.Desktop.Internal;

internal sealed class WebUiEmbeddedHostFactory(LinuxEmbeddedBackend? linuxBackend = null) : IWebUiEmbeddedHostFactory
{
    internal static WebUiEmbeddedHostFactory Instance { get; } = new();

    public bool IsSupported => OperatingSystem.IsWindows()
        ? WindowsWebView2Host.IsSupported
        : OperatingSystem.IsLinux()
            ? linuxBackend == LinuxEmbeddedBackend.Gtk3WebKit41 && LinuxDesktopRuntime.CanUse(linuxBackend.Value) && LinuxWebKitGtkHost.IsSupported
            : OperatingSystem.IsMacOS() && MacOsWkWebViewHost.IsSupported;

    public IWebUiEmbeddedHost Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsWebView2Host();
        }
        if (OperatingSystem.IsLinux())
        {
            if (linuxBackend != LinuxEmbeddedBackend.Gtk3WebKit41)
                throw new PlatformNotSupportedException("Select Linux.EmbeddedBackend explicitly and register its optional factory for GTK4.");
            return new LinuxWebKitGtkHost();
        }
        if (OperatingSystem.IsMacOS())
        {
            return new MacOsWkWebViewHost();
        }

        throw new PlatformNotSupportedException("Embedded WebViews are supported on Windows, Linux, and macOS.");
    }
}

internal interface IWebUiMainThreadHost
{
    void ProcessEvents();
}
