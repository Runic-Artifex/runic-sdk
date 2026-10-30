namespace Runic.Desktop.Internal;

// Reports, without opening anything, which DesktopWindowOptions a presentation rejects or ignores. The
// built-in tables mirror what WebUiBrowserHost and the built-in embedded hosts apply; an
// IDesktopWindowHostFactory reports its own options through ValidateOptions.
internal static class DesktopWindowOptionValidation
{
    internal const string OptionInvalidCode = "window-option-invalid";
    internal const string OptionUnsupportedCode = "window-option-unsupported";
    internal const string OptionIncompleteCode = "window-option-incomplete";
    internal const string GrantUnsupportedCode = "permission-grant-unsupported";
    internal const string GrantWithheldCode = "permission-grant-withheld";
    internal const string BrowserUnsupportedCode = "browser-unsupported";
    internal const string CloseConfirmationUnavailableCode = "close-confirmation-unavailable";
    internal const string ColorSchemeUnsupportedCode = "color-scheme-unsupported";

    private const string Prefix = "DesktopWindowOptions.";

    internal static bool IsLaunchable(BrowserKind browser) => browser is not (BrowserKind.Safari or BrowserKind.Opera);

    internal static DesktopDiagnostic BrowserUnsupported(BrowserKind browser) => new(
        DesktopErrorCategory.Unavailable,
        BrowserUnsupportedCode,
        $"Runic Desktop cannot launch {browser} as a presentation.",
        Retryable: false,
        Remediation: "Select BrowserKind.Embedded, Any, ChromiumBased, or a Chromium-based browser or Firefox.")
    {
        Option = Prefix + nameof(DesktopWindowOptions.Browser),
    };

    internal static void AddPairChecks(DesktopWindowOptions options, List<DesktopDiagnostic> diagnostics)
    {
        if (options.X is null != options.Y is null)
        {
            diagnostics.Add(Incomplete(options.X is null ? nameof(DesktopWindowOptions.Y) : nameof(DesktopWindowOptions.X),
                "X and Y apply only together; the window ignores one without the other.",
                "Set both X and Y, or neither."));
        }
        if (options.MinimumWidth is null != options.MinimumHeight is null)
        {
            diagnostics.Add(Incomplete(
                options.MinimumWidth is null ? nameof(DesktopWindowOptions.MinimumHeight) : nameof(DesktopWindowOptions.MinimumWidth),
                "MinimumWidth and MinimumHeight apply only together; the window ignores one without the other.",
                "Set both MinimumWidth and MinimumHeight, or neither."));
        }
    }

    internal static void AddEmbeddedChecks(
        DesktopWindowOptions options,
        IDesktopWindowHostFactory? customFactory,
        LinuxEmbeddedBackend? linuxBackend,
        List<DesktopDiagnostic> diagnostics)
    {
        const string presentation = "The embedded window";
        const string remediation = "Remove the option, or select an installed browser that supports it.";
        if (!string.IsNullOrWhiteSpace(options.ProfileName))
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.ProfileName), remediation));
        }
        if (!string.IsNullOrWhiteSpace(options.ProxyServer))
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.ProxyServer),
                "Embedded windows use the system proxy. Remove the option, or select an installed browser."));
        }

        if (customFactory is not null)
        {
            if (options.ConfirmCloseAsync is not null &&
                !customFactory.Capabilities.HasFlag(DesktopWindowCapabilities.CloseConfirmation))
            {
                diagnostics.Add(Unsupported(
                    "The configured embedded-window host does not support close confirmation.",
                    nameof(DesktopWindowOptions.ConfirmCloseAsync),
                    "Remove ConfirmCloseAsync, or use a window host whose Capabilities include CloseConfirmation."));
            }
            if (options.MonitorId is not null && !customFactory.Capabilities.HasFlag(DesktopWindowCapabilities.MonitorPlacement))
            {
                diagnostics.Add(Ignored("The configured embedded-window host", nameof(DesktopWindowOptions.MonitorId),
                    "Remove the option, or use a window host whose Capabilities include MonitorPlacement."));
            }
            diagnostics.AddRange(customFactory.ValidateOptions(ToHostOptions(options)));
            return;
        }

        // Without a built-in Linux toolkit, availability reports the missing selection or GTK4 provider instead.
        if (OperatingSystem.IsWindows() ||
            (OperatingSystem.IsLinux() && linuxBackend != LinuxEmbeddedBackend.Gtk3WebKit41))
        {
            return;
        }
        var platform = OperatingSystem.IsMacOS() ? "The macOS WKWebView window" : "The GTK 3 WebKitGTK 4.1 window";
        // WebKitGTK follows the GTK theme, which is the application's, not a window's.
        if (!OperatingSystem.IsMacOS() && options.ColorScheme != Runic.Platform.DesktopColorScheme.NoPreference)
        {
            diagnostics.Add(ColorSchemeUnsupported(platform,
                "WebKitGTK follows the GTK theme for every window. Remove the option on Linux, or style the page with its own light and dark themes."));
        }
        // AppKit lays the window out, and WKWebView picks its language, from the application's localizations.
        if (OperatingSystem.IsMacOS() && options.LayoutDirection != Runic.Platform.DesktopLayoutDirection.Default)
        {
            diagnostics.Add(Ignored(platform, nameof(DesktopWindowOptions.LayoutDirection),
                "macOS lays windows out for the application bundle's localization. Remove the option on macOS."));
        }
        if (OperatingSystem.IsMacOS() && options.Language is not null)
        {
            diagnostics.Add(Ignored(platform, nameof(DesktopWindowOptions.Language),
                "WKWebView uses the application's preferred languages. Remove the option on macOS."));
        }
        if (OperatingSystem.IsMacOS())
        {
            AddMacOSTitleBarChecks(options, platform, diagnostics);
        }
        if (!string.IsNullOrWhiteSpace(options.ProfilePath))
        {
            diagnostics.Add(Ignored(platform, nameof(DesktopWindowOptions.ProfilePath), remediation));
        }
        if (!string.IsNullOrWhiteSpace(options.BrowserArguments))
        {
            diagnostics.Add(Ignored(platform, nameof(DesktopWindowOptions.BrowserArguments), remediation));
        }
    }

    private const string MacOSTitleBarOption = nameof(DesktopWindowOptions.MacOS) + "." + nameof(MacOSWindowOptions.TitleBar);
    private const string MacOSTrafficLightOption = nameof(DesktopWindowOptions.MacOS) + "." + nameof(MacOSWindowOptions.TrafficLightPosition);

    // A frameless or kiosk window has no title bar, and the traffic lights keep AppKit's place in a standard one.
    internal static void AddMacOSTitleBarChecks(DesktopWindowOptions options, string presentation, List<DesktopDiagnostic> diagnostics)
    {
        var noTitleBar = options.Frameless || options.Kiosk;
        if (noTitleBar && options.MacOS.TitleBar != MacOSTitleBarStyle.Default)
        {
            diagnostics.Add(Ignored(presentation, MacOSTitleBarOption,
                "A frameless or kiosk window has no title bar. Remove MacOS.TitleBar, or Frameless and Kiosk."));
        }
        if (options.MacOS.TrafficLightPosition is not null && (noTitleBar || options.MacOS.TitleBar == MacOSTitleBarStyle.Default))
        {
            diagnostics.Add(Ignored(presentation, MacOSTrafficLightOption,
                "The traffic lights move only under a Hidden or Transparent MacOS.TitleBar of a window with a frame."));
        }
    }

    // A null browser is an Any or ChromiumBased request that no installed browser satisfies; only the
    // options every browser ignores are reported then.
    internal static void AddBrowserChecks(
        DesktopWindowOptions options,
        BrowserKind? browser,
        bool isFallback,
        List<DesktopDiagnostic> diagnostics)
    {
        var presentation = isFallback
            ? browser is { } fallbackBrowser ? $"The {fallbackBrowser} browser fallback" : "The browser fallback"
            : browser is { } selected ? $"The {selected} browser" : "An installed browser";
        var remediation = isFallback
            ? "Remove the option, or remove the browser fallback (DesktopPresentationPolicy.RequestedOnly, no Fallbacks)."
            : "Remove the option, or select BrowserKind.Embedded.";
        if (options.MinimumWidth is not null && options.MinimumHeight is not null)
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.MinimumWidth), remediation));
        }
        if (options.Centered)
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.Centered), remediation));
        }
        if (options.Placement is { Maximized: true })
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.Placement),
                "A browser opens with the saved size and position but not maximized. " + remediation)
                with { Message = $"{presentation} ignores {Prefix}{nameof(DesktopWindowOptions.Placement)}.{nameof(DesktopWindowPlacement.Maximized)}." });
        }
        if (options.Placement is { FullScreen: true })
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.Placement),
                "A browser opens with the saved size and position but not full-screen. " + remediation)
                with { Message = $"{presentation} ignores {Prefix}{nameof(DesktopWindowOptions.Placement)}.{nameof(DesktopWindowPlacement.FullScreen)}." });
        }
        if (options.MonitorId is not null)
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.MonitorId), remediation));
        }
        if (options.LayoutDirection != Runic.Platform.DesktopLayoutDirection.Default)
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.LayoutDirection), remediation));
        }
        if (options.Language is not null)
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.Language), remediation));
        }
        if (!options.SpellChecking)
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.SpellChecking),
                "A browser checks spelling with its own settings; set spellcheck=\"false\" in the page instead. " + remediation));
        }
        if (!options.Resizable)
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.Resizable), remediation));
        }
        if (options.Frameless)
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.Frameless), remediation));
        }
        if (options.Transparent)
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.Transparent), remediation));
        }
        if (!string.IsNullOrWhiteSpace(options.IconFile))
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.IconFile), remediation));
        }
        if (options.ColorScheme != Runic.Platform.DesktopColorScheme.NoPreference)
        {
            diagnostics.Add(ColorSchemeUnsupported(presentation, "A browser follows its own and the system's theme. " + remediation));
        }
        // Only the macOS window has these; elsewhere every presentation ignores them without a report.
        if (OperatingSystem.IsMacOS() && options.MacOS.TitleBar != MacOSTitleBarStyle.Default)
        {
            diagnostics.Add(Ignored(presentation, MacOSTitleBarOption, remediation));
        }

        if (options.ConfirmCloseAsync is not null)
        {
            diagnostics.Add(CloseConfirmationUnavailable(presentation, isFallback));
        }

        var firefox = browser == BrowserKind.Firefox;
        if (browser is not null && !firefox && !string.IsNullOrWhiteSpace(options.ProfileName))
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.ProfileName),
                "Chromium browsers use ProfilePath. " + remediation));
        }
        if (firefox && options.X is not null && options.Y is not null)
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.X), remediation));
        }
        // A browser window position is unsigned, so WebUiBrowserHost skips a negative one.
        else if (options.X is int x && options.Y is int y && (x < 0 || y < 0))
        {
            diagnostics.Add(Ignored(presentation,
                x < 0 ? nameof(DesktopWindowOptions.X) : nameof(DesktopWindowOptions.Y),
                "Use a non-negative position. " + remediation));
        }
        if (firefox && !string.IsNullOrWhiteSpace(options.ProxyServer))
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.ProxyServer), remediation));
        }

        if (!options.AllowedPermissions.HasFlag(DesktopPermissionGrant.MediaCapture))
        {
            return;
        }
        // A browser cannot limit the grant to the presented origin, so a fallback never inherits it.
        if (isFallback)
        {
            diagnostics.Add(GrantWithheld(presentation));
        }
        // Nor does an explicitly selected browser apply it: Chromium-based browsers ask the user too (SEC-11), naming
        // the requesting origin.
        else if (browser is not null)
        {
            diagnostics.Add(new DesktopDiagnostic(
                DesktopErrorCategory.CapabilityDenied,
                GrantUnsupportedCode,
                firefox
                    ? "Firefox does not apply the MediaCapture grant; it asks the user instead."
                    : $"{presentation} does not apply the MediaCapture grant; it asks the user instead.",
                Retryable: false,
                Remediation: "Remove the grant, or select BrowserKind.Embedded to grant camera and microphone to the presented origin only.")
            {
                Severity = DesktopDiagnosticSeverity.Warning,
                Option = Prefix + nameof(DesktopWindowOptions.AllowedPermissions),
            });
        }
    }

    // A browser cannot veto a user close, so ConfirmCloseAsync is never asked there. An explicit browser fails;
    // a browser fallback opens without confirmation, because the preferred window honours it.
    internal static DesktopDiagnostic CloseConfirmationUnavailable(string presentation, bool isFallback) => new(
        DesktopErrorCategory.CapabilityDenied,
        CloseConfirmationUnavailableCode,
        isFallback
            ? $"{presentation} opens without close confirmation: a browser cannot keep its window open, so ConfirmCloseAsync is not called."
            : $"{presentation} cannot confirm a user close request: a browser cannot keep its window open.",
        Retryable: false,
        Remediation: isFallback
            ? "Require the embedded window (DesktopPresentationPolicy.RequestedOnly, no browser in Fallbacks), or remove ConfirmCloseAsync."
            : "Select BrowserKind.Embedded, or remove ConfirmCloseAsync.")
    {
        Severity = isFallback ? DesktopDiagnosticSeverity.Warning : DesktopDiagnosticSeverity.Error,
        Option = Prefix + nameof(DesktopWindowOptions.ConfirmCloseAsync),
    };

    // DesktopWindowOptions.ColorScheme on a presentation that cannot set a window's light or dark appearance: the window
    // follows the system instead.
    internal static DesktopDiagnostic ColorSchemeUnsupported(string presentation, string remediation) => new(
        DesktopErrorCategory.CapabilityDenied,
        ColorSchemeUnsupportedCode,
        $"{presentation} ignores {Prefix}{nameof(DesktopWindowOptions.ColorScheme)} and follows the system's light or dark appearance.",
        Retryable: false,
        Remediation: remediation)
    {
        Severity = DesktopDiagnosticSeverity.Warning,
        Option = Prefix + nameof(DesktopWindowOptions.ColorScheme),
    };

    // DesktopHostOptions.SpellCheckingLanguages on Windows: WebView2 checks in its own language (DesktopWindowOptions.Language).
    internal static DesktopDiagnostic SpellCheckingLanguagesIgnored() => new(
        DesktopErrorCategory.CapabilityDenied,
        OptionUnsupportedCode,
        "The WebView2 window ignores DesktopHostOptions.SpellCheckingLanguages: WebView2 has no setting for its spelling languages.",
        Retryable: false,
        Remediation: "WebView2 checks spelling in its own language, which DesktopWindowOptions.Language sets. Keep the option for Linux and macOS, or remove it on Windows.")
    {
        Severity = DesktopDiagnosticSeverity.Warning,
        Option = "DesktopHostOptions.SpellCheckingLanguages",
    };

    internal static DesktopDiagnostic GrantWithheld(string presentation) => new(
        DesktopErrorCategory.CapabilityDenied,
        GrantWithheldCode,
        $"{presentation} opens without the MediaCapture grant: Chromium browsers deny camera and microphone, and Firefox asks the user.",
        Retryable: false,
        Remediation: "Require the embedded window (DesktopPresentationPolicy.RequestedOnly, no browser in Fallbacks), or remove the grant.")
    {
        Severity = DesktopDiagnosticSeverity.Warning,
        Option = Prefix + nameof(DesktopWindowOptions.AllowedPermissions),
    };

    internal static DesktopWindowHostOptions ToHostOptions(DesktopWindowOptions options) => new()
    {
        CloseRequested = options.ConfirmCloseAsync is null ? null : static () => { },
        Width = options.Width,
        Height = options.Height,
        MinimumWidth = options.MinimumWidth,
        MinimumHeight = options.MinimumHeight,
        // As when opening: a position applies only with both coordinates, and Centered replaces it.
        X = options.X is not null && options.Y is not null && !options.Centered ? options.X : null,
        Y = options.X is not null && options.Y is not null && !options.Centered ? options.Y : null,
        Centered = options.Centered,
        Placement = options.Placement,
        Resizable = options.Resizable,
        Frameless = options.Frameless,
        Transparent = options.Transparent,
        Hidden = options.Hidden,
        Kiosk = options.Kiosk,
        // The window follows the system setting when the option is unset, as it does when opening.
        HighContrast = options.HighContrast ?? WebUiSystemTheme.IsHighContrast,
        IconFile = options.IconFile,
        ProfilePath = options.ProfilePath,
        BrowserArguments = options.BrowserArguments,
        AllowedPermissions = options.AllowedPermissions,
        WebContent = options.WebContent is { } webContent
            ? WebContentRules.Resolve(webContent, Environment.GetEnvironmentVariable)
            : DesktopWindowHostOptions.SafeWebContent,
        MenuBarPresentation = options.MenuBarPresentation,
        ColorScheme = options.ColorScheme,
        MonitorId = options.MonitorId,
        LayoutDirection = options.LayoutDirection,
        Language = options.Language,
        SpellChecking = options.SpellChecking,
    };

    internal static DesktopDiagnostic Invalid(string message) => new(
        DesktopErrorCategory.InvalidArgument,
        OptionInvalidCode,
        message,
        Retryable: false,
        Remediation: "Correct the DesktopWindowOptions value.");

    private static DesktopDiagnostic Ignored(string presentation, string option, string remediation) => new(
        DesktopErrorCategory.CapabilityDenied,
        OptionUnsupportedCode,
        $"{presentation} ignores {Prefix}{option}.",
        Retryable: false,
        Remediation: remediation)
    {
        Severity = DesktopDiagnosticSeverity.Warning,
        Option = Prefix + option,
    };

    private static DesktopDiagnostic Unsupported(string message, string option, string remediation) => new(
        DesktopErrorCategory.CapabilityDenied,
        OptionUnsupportedCode,
        message,
        Retryable: false,
        Remediation: remediation)
    {
        Option = Prefix + option,
    };

    private static DesktopDiagnostic Incomplete(string option, string message, string remediation) => new(
        DesktopErrorCategory.InvalidArgument,
        OptionIncompleteCode,
        message,
        Retryable: false,
        Remediation: remediation)
    {
        Severity = DesktopDiagnosticSeverity.Warning,
        Option = Prefix + option,
    };
}
