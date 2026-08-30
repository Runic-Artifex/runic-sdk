using System.Diagnostics;
using Runic.Desktop.Internal;

namespace Runic.Desktop;

/// <summary>Provides read-only platform presentation discovery.</summary>
public static class DesktopPlatform
{
    private static readonly BrowserKind[] DiscoverableBrowsers =
    [
        BrowserKind.Chrome,
        BrowserKind.Firefox,
        BrowserKind.Edge,
        BrowserKind.Chromium,
        BrowserKind.Brave,
        BrowserKind.Vivaldi,
        BrowserKind.Epic,
        BrowserKind.Yandex,
    ];

    /// <summary>Gets whether the built-in embedded WebView presentation is available to this process.</summary>
    public static bool IsEmbeddedWindowAvailable => GetEmbeddedDiagnostic() is null;

    /// <summary>Gets whether a requested installed or embedded browser is available.</summary>
    public static bool IsBrowserAvailable(BrowserKind browser, string? browserFolder = null) =>
        browser == BrowserKind.Embedded
            ? IsEmbeddedWindowAvailable
            : WebUiBrowserDiscovery.Find(ToCompatibilityBrowser(browser), browserFolder) is not null;

    /// <summary>Gets whether the operating system currently uses a high-contrast theme.</summary>
    public static bool IsHighContrast => WebUiSystemTheme.IsHighContrast;

    /// <summary>Inspects installed browsers and embedded-WebView prerequisites without opening a window.</summary>
    public static DesktopAvailabilityResult GetAvailability(string? browserFolder = null)
    {
        var presentations = new List<DesktopPresentationAvailability>();
        foreach (var browser in DiscoverableBrowsers)
        {
            var installation = WebUiBrowserDiscovery.Find(ToCompatibilityBrowser(browser), browserFolder);
            presentations.Add(new DesktopPresentationAvailability(
                browser,
                installation is not null,
                installation?.ExecutablePath,
                DesktopWindowCapabilities.None,
                installation is null ? MissingBrowser(browser) : null));
        }

        var embeddedDiagnostic = GetEmbeddedDiagnostic();
        var embeddedAvailable = embeddedDiagnostic is null;
        presentations.Add(new DesktopPresentationAvailability(
            BrowserKind.Embedded,
            embeddedAvailable,
            ExecutablePath: null,
            embeddedAvailable
                ? DesktopWindowCapabilities.NativeHandle |
                  DesktopWindowCapabilities.Focus |
                  DesktopWindowCapabilities.Minimize |
                  DesktopWindowCapabilities.Maximize |
                  DesktopWindowCapabilities.Resize |
                  DesktopWindowCapabilities.Move
                : DesktopWindowCapabilities.None,
            embeddedDiagnostic));
        return new DesktopAvailabilityResult(GetPlatformName(), presentations);
    }

    /// <summary>Opens a URL through the operating system's default URL handler.</summary>
    public static void OpenUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    internal static DesktopDiagnostic? GetEmbeddedDiagnostic()
    {
        if (OperatingSystem.IsWindows() && !WebUiEmbeddedHostFactory.Instance.IsSupported)
        {
            return Missing(
                "webview2-runtime-missing",
                "The Microsoft Edge WebView2 Runtime is unavailable.",
                "Install the evergreen Microsoft Edge WebView2 Runtime or select an installed browser.");
        }
        if (OperatingSystem.IsLinux())
        {
            if (!WebUiEmbeddedHostFactory.Instance.IsSupported)
            {
                return Missing(
                    "webkitgtk-runtime-missing",
                    "GTK 3 and WebKitGTK 4.1 or 4.0 are unavailable.",
                    "Install GTK 3 and WebKitGTK 4.1 (or 4.0), or select an installed browser.");
            }
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) &&
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            {
                return Missing(
                    "graphical-session-missing",
                    "No graphical Linux session is available to host a WebView.",
                    "Run inside an X11 or Wayland session, or select an installed browser in a graphical session.");
            }
        }
        if (OperatingSystem.IsMacOS())
        {
            if (!WebUiEmbeddedHostFactory.Instance.IsSupported)
            {
                return Missing(
                    "webkit-runtime-missing",
                    "The macOS WebKit framework is unavailable.",
                    "Use a supported macOS installation or select an installed browser.");
            }
            if (!MacOsWkWebViewHost.IsMainThread)
            {
                return Missing(
                    "macos-main-thread-required",
                    "The first macOS embedded window must be opened from the process main thread.",
                    "Open the first Desktop window before awaiting other work, or select an installed browser.");
            }
        }
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return Missing(
                "embedded-platform-unsupported",
                "The current platform has no built-in embedded WebView host.",
                "Provide an IDesktopWindowHostFactory or select an installed browser.");
        }
        return null;
    }

    private static DesktopDiagnostic MissingBrowser(BrowserKind browser) => Missing(
        "browser-not-found",
        $"No supported {browser} installation was discovered.",
        $"Install {browser}, configure DesktopHostOptions.BrowserFolder, or select another presentation.");

    private static DesktopDiagnostic Missing(string code, string message, string remediation) => new(
        DesktopErrorCategory.Unavailable,
        code,
        message,
        Retryable: false,
        CorrelationId: string.Empty,
        Remediation: remediation);

    private static string GetPlatformName() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsLinux() ? "linux" :
        "unsupported";

    private static WebUiBrowser ToCompatibilityBrowser(BrowserKind browser) => browser switch
    {
        BrowserKind.Any => WebUiBrowser.AnyBrowser,
        BrowserKind.Chrome => WebUiBrowser.Chrome,
        BrowserKind.Firefox => WebUiBrowser.Firefox,
        BrowserKind.Edge => WebUiBrowser.Edge,
        BrowserKind.Safari => WebUiBrowser.Safari,
        BrowserKind.Chromium => WebUiBrowser.Chromium,
        BrowserKind.Opera => WebUiBrowser.Opera,
        BrowserKind.Brave => WebUiBrowser.Brave,
        BrowserKind.Vivaldi => WebUiBrowser.Vivaldi,
        BrowserKind.Epic => WebUiBrowser.Epic,
        BrowserKind.Yandex => WebUiBrowser.Yandex,
        BrowserKind.ChromiumBased => WebUiBrowser.ChromiumBased,
        BrowserKind.Embedded => WebUiBrowser.WebView,
        _ => throw new ArgumentOutOfRangeException(nameof(browser)),
    };
}
