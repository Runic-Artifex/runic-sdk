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

    /// <summary>Gets whether the operating system currently uses a high-contrast theme.</summary>
    /// <remarks>
    /// A synchronous probe of the current setting, which decides a window's initial theme when
    /// <see cref="DesktopWindowOptions.HighContrast"/> is not set. To follow changes while the application runs,
    /// observe <c>IDesktopSettings</c> from Runic.Platform (<c>ReadAsync</c> and <c>WatchAsync</c>, whose
    /// <c>DesktopAppearance.HighContrast</c> reports the same setting).
    /// </remarks>
    public static bool IsHighContrast => WebUiSystemTheme.IsHighContrast;

    /// <summary>
    /// Inspects the installed browsers and the embedded presentation of a host configuration without starting a host
    /// or opening a window.
    /// </summary>
    /// <param name="options">
    /// The options the host will start with; <see langword="null"/> for <c>new DesktopHostOptions()</c>. Their
    /// <see cref="DesktopHostOptions.BrowserFolder"/>, <see cref="DesktopHostOptions.Linux"/> and
    /// <see cref="DesktopHostOptions.WindowHostFactory"/> are read, so a configuration such as
    /// <c>new DesktopHostOptions().WithGtk4()</c> or a WPF <c>RunicWebView</c> factory reports its own window host.
    /// </param>
    /// <returns>
    /// One entry per discoverable browser, then <see cref="DesktopAvailabilityResult.Embedded"/>, and on Linux the
    /// toolkit libraries in <see cref="DesktopAvailabilityResult.LinuxBackends"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <see cref="DesktopHost.GetPresentationPreflight"/> and <see cref="DesktopHost.Validate"/> evaluate a window request
    /// with this result for the host's own options, so a check before <see cref="DesktopEventLoop"/> chooses its loop
    /// gives the same answer as the host. Opening a window remains the final check: a window host may still fail, for
    /// example when the GTK 4 loop finds no display.
    /// </para>
    /// <para>
    /// Without <see cref="DesktopHostOptions.WindowHostFactory"/>, the embedded entry describes the built-in WebView2,
    /// WKWebView or WebKitGTK host, and its <see cref="DesktopPresentationAvailability.Capabilities"/> leave out what
    /// is known only once the native toolkit has loaded; an open window's <see cref="DesktopWindow.Capabilities"/> is
    /// authoritative. With a factory, the entry has the factory's <see cref="IDesktopWindowHostFactory.IsSupported"/>
    /// and <see cref="IDesktopWindowHostFactory.Capabilities"/>, and when it is not supported, the Linux selection
    /// problems and <see cref="IDesktopWindowHostFactory.GetAvailabilityDiagnostics"/>, or
    /// <c>custom-window-host-unavailable</c> when neither reports one.
    /// </para>
    /// <para>
    /// The call is synchronous: it checks files, asks the WebView2 loader and, on Linux, reads the loader cache once
    /// per process, which can take up to one second the first time. On macOS the embedded entry reports
    /// <c>macos-main-thread-required</c> when called off the main thread before a Desktop event loop runs.
    /// Diagnostic codes are an open set: treat an unknown code on an unavailable presentation as unavailable, and
    /// match entries by <see cref="DesktopPresentationAvailability.Browser"/>, since the list can grow.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <see cref="DesktopHostOptions.Linux"/> is <see langword="null"/>, or a Linux window host factory's backend differs
    /// from <see cref="LinuxDesktopOptions.EmbeddedBackend"/>, which <see cref="DesktopHost.StartAsync"/> rejects too.
    /// </exception>
    public static DesktopAvailabilityResult GetAvailability(DesktopHostOptions? options = null)
    {
        options ??= new DesktopHostOptions();
        var linux = options.Linux ?? throw new ArgumentException("DesktopHostOptions.Linux cannot be null.", nameof(options));
        var factory = options.WindowHostFactory;
        if (factory is ILinuxDesktopWindowHostFactory linuxFactory && linux.EmbeddedBackend != linuxFactory.Backend)
            throw new ArgumentException("Linux.EmbeddedBackend must match the configured Linux window host factory.", nameof(options));

        var presentations = new List<DesktopPresentationAvailability>();
        foreach (var browser in DiscoverableBrowsers)
        {
            var installation = WebUiBrowserDiscovery.Find(ToCompatibilityBrowser(browser), options.BrowserFolder);
            presentations.Add(new DesktopPresentationAvailability(
                browser,
                installation is not null,
                installation?.ExecutablePath,
                DesktopWindowCapabilities.None,
                installation is null ? [MissingBrowser(browser)] : []));
        }
        presentations.Add(factory is null ? GetBuiltInEmbeddedAvailability(linux) : GetCustomEmbeddedAvailability(linux, factory));
        return new DesktopAvailabilityResult(GetPlatformName(), presentations)
        {
            LinuxBackends = OperatingSystem.IsLinux() ? GetLinuxEmbeddedBackends() : [],
        };
    }

    // The operations the built-in host advertises before opening: each host's own KnownCapabilities, without a flag it
    // decides only after loading its toolkit (State and FileDrop of GTK 3, FileDrop of WKWebView).
    internal static DesktopWindowCapabilities BuiltInEmbeddedCapabilities =>
        OperatingSystem.IsWindows() ? WindowsWebView2Host.KnownCapabilities :
        OperatingSystem.IsMacOS() ? MacOsWkWebViewHost.KnownCapabilities :
        OperatingSystem.IsLinux() ? LinuxWebKitGtkHost.KnownCapabilities :
        DesktopWindowCapabilities.None;

    private static DesktopPresentationAvailability GetBuiltInEmbeddedAvailability(LinuxDesktopOptions linux)
    {
        var diagnostics = GetEmbeddedDiagnostics(linux);
        var available = diagnostics.Count == 0;
        return new DesktopPresentationAvailability(
            BrowserKind.Embedded,
            available,
            ExecutablePath: null,
            available ? BuiltInEmbeddedCapabilities : DesktopWindowCapabilities.None,
            diagnostics);
    }

    private static DesktopPresentationAvailability GetCustomEmbeddedAvailability(LinuxDesktopOptions linux, IDesktopWindowHostFactory factory)
    {
        List<DesktopDiagnostic> diagnostics = OperatingSystem.IsLinux() && factory is ILinuxDesktopWindowHostFactory
            ? GetLinuxSelectionDiagnostics(linux, factory)
            : [];
        var available = diagnostics.Count == 0 && factory.IsSupported;
        if (!available)
        {
            // The selection and the factory can both report a prerequisite, such as a toolkit conflict; list each code once.
            if (!factory.IsSupported)
            {
                foreach (var diagnostic in factory.GetAvailabilityDiagnostics())
                {
                    if (!diagnostics.Exists(existing => existing.Code == diagnostic.Code)) diagnostics.Add(diagnostic);
                }
            }
            if (diagnostics.Count == 0)
                diagnostics.Add(new DesktopDiagnostic(
                    DesktopErrorCategory.Unavailable,
                    "custom-window-host-unavailable",
                    "The configured embedded-window host is unavailable.",
                    Retryable: false,
                    Remediation: "Install its platform prerequisites or select an installed browser."));
        }
        return new DesktopPresentationAvailability(
            BrowserKind.Embedded,
            available,
            ExecutablePath: null,
            available ? factory.Capabilities : DesktopWindowCapabilities.None,
            available ? [] : diagnostics);
    }

    // Both Linux toolkit candidates, without choosing or loading either.
    private static IReadOnlyList<LinuxEmbeddedBackendAvailability> GetLinuxEmbeddedBackends() =>
    [
        Inspect(LinuxEmbeddedBackend.Gtk3WebKit41, Gtk3NativeLibraries.Gtk, Gtk3NativeLibraries.WebKit),
        Inspect(LinuxEmbeddedBackend.Gtk4WebKit6, Gtk4NativeLibraries.Gtk, Gtk4NativeLibraries.WebKit[0]),
    ];

    private static LinuxEmbeddedBackendAvailability Inspect(LinuxEmbeddedBackend backend, string gtk, string webkit)
    {
        bool discovered = LinuxDesktopRuntime.IsLibraryAvailable(gtk) && LinuxDesktopRuntime.IsLibraryAvailable(webkit);
        return new(backend, discovered, discovered ? [] : [Missing(
            backend == LinuxEmbeddedBackend.Gtk3WebKit41 ? "webkitgtk-runtime-missing" : "webkitgtk6-runtime-missing",
            $"The runtime libraries for {backend} were not discovered.",
            backend == LinuxEmbeddedBackend.Gtk3WebKit41
                ? $"Install {gtk} and {webkit} (for example {Gtk3NativeLibraries.ExamplePackages}) in the native loader search path."
                : $"Install GTK 4.12 or newer and WebKitGTK 6.0 ({gtk} and {webkit}, for example libgtk-4-1 and libwebkitgtk-6.0-4), and reference the Runic.Desktop.Gtk4 provider.")]);
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
                $"Install GTK 3 and WebKitGTK 4.1 (for example {Gtk3NativeLibraries.ExamplePackages}), or select an installed browser. dotnet runic doctor checks this machine, and doctor --rid <rid> a deployment target."));
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
        if (!LinuxDesktopRuntime.IsLibraryAvailable(Gtk4NativeLibraries.Gtk))
            yield return Missing("gtk4-runtime-missing", "The GTK 4 library libgtk-4.so.1 was not discovered.",
                "Install GTK 4.12 or newer (for example libgtk-4-1) in the native loader search path.");
        if (!Gtk4NativeLibraries.WebKit.Any(LinuxDesktopRuntime.IsLibraryAvailable))
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
