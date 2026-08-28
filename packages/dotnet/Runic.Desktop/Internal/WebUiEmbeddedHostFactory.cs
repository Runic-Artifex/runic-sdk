namespace Runic.Desktop.Internal;

internal sealed class WebUiEmbeddedHostFactory : IWebUiEmbeddedHostFactory
{
    internal static WebUiEmbeddedHostFactory Instance { get; } = new();

    public bool IsSupported => OperatingSystem.IsWindows()
        ? WindowsWebView2Host.IsSupported
        : OperatingSystem.IsLinux()
            ? LinuxWebKitGtkHost.IsSupported
            : OperatingSystem.IsMacOS() && MacOsWkWebViewHost.IsSupported;

    public IWebUiEmbeddedHost Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsWebView2Host();
        }
        if (OperatingSystem.IsLinux())
        {
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
