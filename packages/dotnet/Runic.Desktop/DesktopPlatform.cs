using System.Diagnostics;
using Runic.Desktop.Internal;

namespace Runic.Desktop;

/// <summary>Provides read-only platform presentation discovery.</summary>
public static class DesktopPlatform
{
    /// <summary>Gets whether the built-in embedded WebView runtime is available.</summary>
    public static bool IsEmbeddedWindowAvailable => WebUiEmbeddedHostFactory.Instance.IsSupported;

    /// <summary>Gets whether a requested installed or embedded browser is available.</summary>
    public static bool IsBrowserAvailable(BrowserKind browser, string? browserFolder = null) =>
        browser == BrowserKind.Embedded
            ? IsEmbeddedWindowAvailable
            : WebUiBrowserDiscovery.Find(ToCompatibilityBrowser(browser), browserFolder) is not null;

    /// <summary>Gets whether the operating system currently uses a high-contrast theme.</summary>
    public static bool IsHighContrast => WebUiSystemTheme.IsHighContrast;

    /// <summary>Opens a URL through the operating system's default URL handler.</summary>
    public static void OpenUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

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
