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
    public static DesktopAvailabilityResult GetAvailability(string? browserFolder = null) => GetAvailability(browserFolder, new LinuxDesktopOptions());

    /// <summary>Inspects presentations for an explicit Linux backend.</summary>
    public static DesktopAvailabilityResult GetAvailability(string? browserFolder, LinuxDesktopOptions linux)
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

        var embeddedDiagnostics = GetEmbeddedDiagnostics(linux);
        var embeddedAvailable = embeddedDiagnostics.Count == 0;
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
            embeddedAvailable ? null : embeddedDiagnostics[0])
        {
            Diagnostics = embeddedDiagnostics,
        });
        return new DesktopAvailabilityResult(GetPlatformName(), presentations);
    }

    /// <summary>Inspects both Linux runtime candidates without choosing or loading either toolkit.</summary>
    public static IReadOnlyList<LinuxEmbeddedBackendAvailability> GetLinuxEmbeddedBackends() =>
    [
        Inspect(LinuxEmbeddedBackend.Gtk3WebKit41, "libgtk-3.so.0", "libwebkit2gtk-4.1.so.0"),
        Inspect(LinuxEmbeddedBackend.Gtk4WebKit6, "libgtk-4.so.1", "libwebkitgtk-6.0.so.4"),
    ];

    private static LinuxEmbeddedBackendAvailability Inspect(LinuxEmbeddedBackend backend, string gtk, string webkit)
    {
        bool discovered = LinuxDesktopRuntime.IsLibraryAvailable(gtk) && LinuxDesktopRuntime.IsLibraryAvailable(webkit);
        return new(backend, discovered, discovered ? null : Missing(
            backend == LinuxEmbeddedBackend.Gtk3WebKit41 ? "webkitgtk-runtime-missing" : "webkitgtk6-runtime-missing",
            $"The runtime libraries for {backend} were not discovered.",
            backend == LinuxEmbeddedBackend.Gtk3WebKit41
                ? $"Install {gtk} and {webkit} (for example libgtk-3-0 and libwebkit2gtk-4.1-0) in the native loader search path."
                : $"Install GTK 4.12 or newer and WebKitGTK 6.0 ({gtk} and {webkit}, for example libgtk-4-1 and libwebkitgtk-6.0-4), and reference the Runic.Desktop.Gtk4 provider."));
    }

    /// <summary>Opens a URL through the operating system's default URL handler.</summary>
    public static void OpenUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // Every missing embedded prerequisite, in the order a user can act on them. Linux reports each one
    // (selection, provider, libraries, session); other platforms have a single prerequisite chain.
    internal static IReadOnlyList<DesktopDiagnostic> GetEmbeddedDiagnostics(LinuxDesktopOptions? linux = null)
    {
        if (OperatingSystem.IsLinux()) return GetLinuxEmbeddedDiagnostics(linux ?? new LinuxDesktopOptions());
        return GetEmbeddedDiagnostic(linux) is { } diagnostic ? [diagnostic] : [];
    }

    private static List<DesktopDiagnostic> GetLinuxEmbeddedDiagnostics(LinuxDesktopOptions linux)
    {
        var diagnostics = GetLinuxSelectionDiagnostics(linux, null);
        if (linux.EmbeddedBackend is not { } backend || !Enum.IsDefined(backend)) return diagnostics;
        if (backend == LinuxEmbeddedBackend.Gtk3WebKit41 && !LinuxWebKitGtkHost.IsSupported)
            diagnostics.Add(Missing("webkitgtk-runtime-missing", "GTK 3 and WebKitGTK 4.1 are unavailable.",
                "Install GTK 3 and WebKitGTK 4.1 (for example libgtk-3-0 and libwebkit2gtk-4.1-0), or select an installed browser. dotnet runic doctor --rid <rid> checks a deployment target."));
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            diagnostics.Add(Missing(
                "graphical-session-missing",
                "No graphical Linux session is available to host a WebView.",
                "Run inside an X11 or Wayland session, or select an installed browser in a graphical session."));
        }
        return diagnostics;
    }

    internal static DesktopDiagnostic? GetEmbeddedDiagnostic(LinuxDesktopOptions? linux = null)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (WindowsWebView2Interop.IsAvailable()) return null;
            }
            catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                return Missing("webview2-loader-unavailable",
                    "The WebView2 native loader is missing, incompatible, or could not be loaded.",
                    "For JIT builds, deploy the matching WebView2Loader.dll native asset. For NativeAOT, publish with Runic.Desktop's transitive build targets to link the loader into the executable.");
            }
            catch (System.Runtime.InteropServices.COMException) { }
            return Missing(
                "webview2-runtime-missing",
                "The Microsoft Edge WebView2 Runtime is unavailable.",
                "Install the evergreen Microsoft Edge WebView2 Runtime or select an installed browser.");
        }
        if (OperatingSystem.IsLinux())
        {
            var diagnostics = GetLinuxEmbeddedDiagnostics(linux ?? new LinuxDesktopOptions());
            return diagnostics.Count == 0 ? null : diagnostics[0];
        }
        if (OperatingSystem.IsMacOS())
        {
            return GetMacOsEmbeddedDiagnostic(
                WebUiEmbeddedHostFactory.Instance.IsSupported,
                MacOsWkWebViewHost.IsMainThread,
                DesktopEventLoop.IsRunning);
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

    internal static DesktopDiagnostic? GetLinuxSelectionDiagnostic(LinuxDesktopOptions linux, IDesktopWindowHostFactory? factory) =>
        GetLinuxSelectionDiagnostics(linux, factory) is [var first, ..] ? first : null;

    // Reports every selection problem at once. An unselected or undefined backend stops the list: nothing
    // after it can be checked. A selected GTK4 backend without its provider also reports its missing libraries,
    // because the provider that would check them is absent.
    internal static List<DesktopDiagnostic> GetLinuxSelectionDiagnostics(LinuxDesktopOptions linux, IDesktopWindowHostFactory? factory)
    {
        if (linux.EmbeddedBackend is not { } backend)
            return [Missing("linux-embedded-backend-not-selected", "No Linux embedded backend is selected.",
                "Set DesktopHostOptions.Linux.EmbeddedBackend to Gtk3WebKit41 or Gtk4WebKit6 (for GTK4, call DesktopHostOptions.WithGtk4() from Runic.Desktop.Gtk4), " +
                "or keep an installed browser with DesktopWindowOptions.Browser = BrowserKind.Any or PresentationPolicy = EmbeddedThenBrowser.")];
        if (!Enum.IsDefined(backend))
            return [new DesktopDiagnostic(DesktopErrorCategory.InvalidArgument, "linux-embedded-backend-invalid",
                $"Linux.EmbeddedBackend has the undefined value {(int)backend}.", Retryable: false, CorrelationId: string.Empty,
                Remediation: "Set DesktopHostOptions.Linux.EmbeddedBackend to Gtk3WebKit41 or Gtk4WebKit6.")
            {
                Option = "DesktopHostOptions.Linux.EmbeddedBackend",
            }];
        List<DesktopDiagnostic> diagnostics = [];
        if (!LinuxDesktopRuntime.CanUse(backend))
            diagnostics.Add(Missing("linux-embedded-backend-conflict", "Another Linux toolkit is already initialized.", "Start a new process to select a different toolkit."));
        if (backend == LinuxEmbeddedBackend.Gtk4WebKit6 && factory is not ILinuxDesktopWindowHostFactory { Backend: LinuxEmbeddedBackend.Gtk4WebKit6 })
        {
            diagnostics.Add(Missing("gtk4-provider-missing", "The optional GTK4 window provider is not configured.",
                "Reference Runic.Desktop.Gtk4 and call DesktopHostOptions.WithGtk4(), which selects Gtk4WebKit6 and sets WindowHostFactory to Gtk4WindowHostFactory."));
            diagnostics.AddRange(GetGtk4LibraryDiagnostics());
        }
        return diagnostics;
    }

    /// <summary>Reports each GTK 4 or WebKitGTK 6 library the native loader cannot find.</summary>
    internal static IEnumerable<DesktopDiagnostic> GetGtk4LibraryDiagnostics()
    {
        if (!LinuxDesktopRuntime.IsLibraryAvailable("libgtk-4.so.1"))
            yield return Missing("gtk4-runtime-missing", "The GTK 4 library libgtk-4.so.1 was not discovered.",
                "Install GTK 4.12 or newer (for example libgtk-4-1) in the native loader search path.");
        if (!LinuxDesktopRuntime.IsLibraryAvailable("libwebkitgtk-6.0.so.4") && !LinuxDesktopRuntime.IsLibraryAvailable("libwebkitgtk-6.0.so.0"))
            yield return Missing("webkitgtk6-runtime-missing", "The WebKitGTK 6.0 library libwebkitgtk-6.0.so.4 was not discovered.",
                "Install WebKitGTK 6.0 (for example libwebkitgtk-6.0-4) in the native loader search path.");
    }

    // Availability must accept the same worker-to-main dispatch path as
    // MacOsWkWebViewHost.ShowAsync. An active loop owns the process main thread;
    // merely calling from a worker without that loop is still unsupported.
    internal static DesktopDiagnostic? GetMacOsEmbeddedDiagnostic(
        bool frameworkAvailable, bool isMainThread, bool eventLoopRunning)
    {
        if (!frameworkAvailable)
            return Missing(
                "webkit-runtime-missing",
                "The macOS WebKit framework is unavailable.",
                "Use a supported macOS installation or select an installed browser.");
        if (!isMainThread && !eventLoopRunning)
            return Missing(
                "macos-main-thread-required",
                "Opening a macOS embedded window requires the process main thread or an active Desktop event loop.",
                "Call DesktopEventLoop.Run() from the process main thread, or open the first Desktop window before awaiting other work.");
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
