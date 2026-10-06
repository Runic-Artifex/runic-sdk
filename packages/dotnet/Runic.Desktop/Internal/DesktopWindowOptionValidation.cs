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
        if (!string.IsNullOrWhiteSpace(options.ProfilePath))
        {
            diagnostics.Add(Ignored(platform, nameof(DesktopWindowOptions.ProfilePath), remediation));
        }
        if (!string.IsNullOrWhiteSpace(options.BrowserArguments))
        {
            diagnostics.Add(Ignored(platform, nameof(DesktopWindowOptions.BrowserArguments), remediation));
        }
        if (OperatingSystem.IsMacOS() && options.AllowedPermissions.HasFlag(DesktopPermissionGrant.MediaCapture))
        {
            diagnostics.Add(new DesktopDiagnostic(
                DesktopErrorCategory.CapabilityDenied,
                GrantUnsupportedCode,
                "The macOS WKWebView window does not apply the MediaCapture grant; WebKit asks the user instead.",
                Retryable: false,
                Remediation: "Remove the grant, or expect the system camera and microphone prompt.")
            {
                Severity = DesktopDiagnosticSeverity.Warning,
                Option = Prefix + nameof(DesktopWindowOptions.AllowedPermissions),
            });
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
            ? "Remove the option, or use DesktopPresentationPolicy.RequestedOnly."
            : "Remove the option, or select BrowserKind.Embedded.";
        if (options.MinimumWidth is not null && options.MinimumHeight is not null)
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.MinimumWidth), remediation));
        }
        if (options.Centered)
        {
            diagnostics.Add(Ignored(presentation, nameof(DesktopWindowOptions.Centered), remediation));
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
        else if (firefox)
        {
            diagnostics.Add(new DesktopDiagnostic(
                DesktopErrorCategory.CapabilityDenied,
                GrantUnsupportedCode,
                "Firefox does not apply the MediaCapture grant; it asks the user instead.",
                Retryable: false,
                Remediation: "Remove the grant, select a Chromium-based browser, or select BrowserKind.Embedded.")
            {
                Severity = DesktopDiagnosticSeverity.Warning,
                Option = Prefix + nameof(DesktopWindowOptions.AllowedPermissions),
            });
        }
    }

    internal static DesktopDiagnostic GrantWithheld(string presentation) => new(
        DesktopErrorCategory.CapabilityDenied,
        GrantWithheldCode,
        $"{presentation} opens without the MediaCapture grant: Chromium browsers deny camera and microphone, and Firefox asks the user.",
        Retryable: false,
        Remediation: "Use DesktopPresentationPolicy.RequestedOnly to require the embedded window, or remove the grant.")
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
        Resizable = options.Resizable,
        Frameless = options.Frameless,
        Transparent = options.Transparent,
        Hidden = options.Hidden,
        Kiosk = options.Kiosk,
        // The window follows the system setting when the option is unset, as it does when opening.
        HighContrast = options.HighContrast ?? WebUiSystemTheme.IsHighContrast,
        IconFile = options.IconFile,
        ProfilePath = options.ProfilePath,
        CustomArguments = options.BrowserArguments,
        AllowedPermissions = options.AllowedPermissions,
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
