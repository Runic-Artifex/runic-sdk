using TUnit.Core;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Runic.Desktop.Gtk4;

namespace Runic.Desktop.Tests;

public sealed class ConfigurationValidationTests
{
    [Test]
    public void WindowsDefaultToTheEmbeddedPresentation()
    {
        Assert.Equal(BrowserKind.Embedded, new DesktopWindowOptions().Browser);
        Assert.Equal(DesktopPresentationPolicy.RequestedOnly, new DesktopWindowOptions().PresentationPolicy);
    }

    // Each row creates its options when the case runs, as xUnit's member data did.
    public static IEnumerable<Func<(string, DesktopWindowOptions)>> Gtk4UnsupportedOptions()
    {
        yield return () => ("X", new DesktopWindowOptions { X = 10, Y = 20 });
        yield return () => ("Y", new DesktopWindowOptions { X = 10, Y = 20 });
        yield return () => ("Centered", new DesktopWindowOptions { Centered = true });
        yield return () => ("Transparent", new DesktopWindowOptions { Transparent = true });
        yield return () => ("HighContrast", new DesktopWindowOptions { HighContrast = true });
        yield return () => ("ProfilePath", new DesktopWindowOptions { ProfilePath = "/tmp/runic-profile" });
        yield return () => ("BrowserArguments", new DesktopWindowOptions { BrowserArguments = "--flag" });
        yield return () => ("IconFile", new DesktopWindowOptions { IconFile = "icon.png" });
    }

    [Test]
    [DisplayName("Gtk4PreflightReportsEachUnsupportedOption(option: $option, requested: $requested)")]
    [MethodDataSource(nameof(Gtk4UnsupportedOptions))]
    [SupportedOSPlatform("linux")]
    public async Task Gtk4PreflightReportsEachUnsupportedOption(string option, DesktopWindowOptions requested)
    {
        // As on Wayland, where GTK 4 cannot position a window.
        using var wayland = new GdkEnvironment("wayland", "wayland-0", null);
        await Gtk4PreflightReportsUnsupportedOptionAsync(option, requested);
    }

    [SupportedOSPlatform("linux")]
    private static async Task Gtk4PreflightReportsUnsupportedOptionAsync(string option, DesktopWindowOptions requested)
    {
        await using var host = await StartGtk4HostAsync();
        var options = requested with { HighContrast = requested.HighContrast ?? false };

        var preflight = host.GetPresentationPreflight(options);
        var validation = host.Validate(options);

        var diagnostic = Assert.Single(preflight.OptionDiagnostics, item =>
            item.Option == $"DesktopWindowOptions.{option}" && item.Severity == DesktopDiagnosticSeverity.Error);
        Assert.Equal("window-option-unsupported", diagnostic.Code);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Remediation));
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, item => item.Option == diagnostic.Option);
        var error = Assert.Throws<DesktopConfigurationException>(validation.ThrowIfInvalid);
        Assert.Equal(DesktopConfigurationException.ConfigurationInvalidCode, error.Code);
        Assert.Contains($"window-option-unsupported: {diagnostic.Message}", error.Message);
    }


    // I-146: GTK 4 positions windows on X11, and on Wayland still rejects a position. Before GTK opens its display,
    // validation follows GDK's backend order: GDK_BACKEND's entries, by default Wayland before X11.
    [Test]
    [SupportedOSPlatform("linux")]
    public async Task Gtk4AcceptsAPositionOnlyOnX11()
    {
        using (new GdkEnvironment("wayland,x11", "wayland-0", ":0"))
        await using (var host = await StartGtk4HostAsync())
        {
            Assert.False(new Gtk4WindowHostFactory().Capabilities.HasFlag(DesktopWindowCapabilities.Move));
            Assert.Contains(host.GetPresentationPreflight(new DesktopWindowOptions { HighContrast = false, X = 10, Y = 20 }).OptionDiagnostics,
                static item => item.Option == "DesktopWindowOptions.X" && item.Code == "window-option-unsupported");
        }
        // The X11 backend's exports and Xlib are needed, so this half runs where the GTK 4 runtime is installed.
        if (!new Gtk4WindowHostFactory().IsSupported) return;
        foreach (var (backends, wayland) in new[] { ((string?)null, (string?)null), ("x11", "wayland-0") })
        {
            using (new GdkEnvironment(backends, wayland, ":0"))
            await using (var host = await StartGtk4HostAsync())
            {
                Assert.True(new Gtk4WindowHostFactory().Capabilities.HasFlag(DesktopWindowCapabilities.Move));
                Assert.Empty(host.GetPresentationPreflight(new DesktopWindowOptions { HighContrast = false, X = 10, Y = 20 }).OptionDiagnostics);
                // Centering stays the window manager's.
                var centered = host.GetPresentationPreflight(new DesktopWindowOptions { HighContrast = false, Centered = true });
                Assert.Equal("DesktopWindowOptions.Centered", Assert.Single(centered.OptionDiagnostics).Option);
            }
        }
    }

    // Sets the variables GDK chooses its backend by, for one case; the suite runs its cases one at a time.
    private sealed class GdkEnvironment : IDisposable
    {
        private static readonly string[] Names = ["GDK_BACKEND", "WAYLAND_DISPLAY", "DISPLAY"];
        private readonly string?[] _saved = Names.Select(Environment.GetEnvironmentVariable).ToArray();

        internal GdkEnvironment(string? backends, string? waylandDisplay, string? display)
        {
            string?[] values = [backends, waylandDisplay, display];
            for (var index = 0; index < Names.Length; index++) Environment.SetEnvironmentVariable(Names[index], values[index]);
        }

        public void Dispose()
        {
            for (var index = 0; index < Names.Length; index++) Environment.SetEnvironmentVariable(Names[index], _saved[index]);
        }
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public async Task Gtk4PreflightMapsPlacementAsOpeningDoes()
    {
        await using var host = await StartGtk4HostAsync();

        // One coordinate is ignored when opening, so GTK4 does not reject it; the pair check warns instead.
        var lone = host.GetPresentationPreflight(new DesktopWindowOptions { HighContrast = false, X = 10 });
        // Centered replaces a position, so only centering is rejected.
        var centered = host.GetPresentationPreflight(new DesktopWindowOptions
        {
            HighContrast = false,
            X = 10,
            Y = 20,
            Centered = true,
        });

        var incomplete = Assert.Single(lone.OptionDiagnostics);
        Assert.Equal("window-option-incomplete", incomplete.Code);
        Assert.Equal(DesktopDiagnosticSeverity.Warning, incomplete.Severity);
        Assert.Equal("DesktopWindowOptions.Centered", Assert.Single(centered.OptionDiagnostics).Option);
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public async Task Gtk4PreflightAcceptsTheOptionsItSupports()
    {
        await using var host = await StartGtk4HostAsync();

        var preflight = host.GetPresentationPreflight(new DesktopWindowOptions
        {
            HighContrast = false,
            MinimumWidth = 400,
            MinimumHeight = 300,
            Frameless = true,
            Hidden = true,
            Kiosk = true,
            Resizable = false,
            AllowedPermissions = DesktopPermissionGrant.MediaCapture,
            ConfirmCloseAsync = static _ => ValueTask.FromResult(true),
        });

        Assert.Empty(preflight.OptionDiagnostics);
    }

    [Test]
    public async Task ABrowserIgnoresColorSchemeWithAColorSchemeWarning()
    {
        await using var host = await DesktopHost.StartAsync();

        var validation = host.Validate(new DesktopWindowOptions { Browser = BrowserKind.Chrome, ColorScheme = Runic.Platform.DesktopColorScheme.Dark });

        var warning = Assert.Single(validation.Warnings, static item => item.Option == "DesktopWindowOptions.ColorScheme");
        Assert.Equal("color-scheme-unsupported", warning.Code);
        Assert.False(string.IsNullOrWhiteSpace(warning.Remediation));
        Assert.DoesNotContain(validation.Errors, static item => item.Option == "DesktopWindowOptions.ColorScheme");
        Assert.DoesNotContain(host.Validate(new DesktopWindowOptions { Browser = BrowserKind.Chrome }).Warnings,
            static item => item.Code == "color-scheme-unsupported");
    }

    [Test]
    public void OnlyTheGtk3WindowIgnoresColorSchemeAmongTheBuiltInEmbeddedWindows()
    {
        foreach (var scheme in new[] { Runic.Platform.DesktopColorScheme.Light, Runic.Platform.DesktopColorScheme.Dark })
        {
            List<DesktopDiagnostic> diagnostics = [];
            Internal.DesktopWindowOptionValidation.AddEmbeddedChecks(new DesktopWindowOptions { ColorScheme = scheme }, null,
                LinuxEmbeddedBackend.Gtk3WebKit41, diagnostics);
            // macOS and Windows set it; the GTK 3 window follows the GTK theme.
            if (OperatingSystem.IsLinux())
            {
                var warning = Assert.Single(diagnostics);
                Assert.Equal(("color-scheme-unsupported", DesktopDiagnosticSeverity.Warning, "DesktopWindowOptions.ColorScheme"),
                    (warning.Code, warning.Severity, warning.Option));
            }
            else
            {
                Assert.Empty(diagnostics);
            }
        }
        List<DesktopDiagnostic> none = [];
        Internal.DesktopWindowOptionValidation.AddEmbeddedChecks(new DesktopWindowOptions(), null, LinuxEmbeddedBackend.Gtk3WebKit41, none);
        Assert.Empty(none);
    }

    [Test]
    public void ColorSchemeReachesACustomHostAndAnUndefinedValueIsRejected()
    {
        Assert.Equal(Runic.Platform.DesktopColorScheme.NoPreference, new DesktopWindowOptions().ColorScheme);
        Assert.Equal(Runic.Platform.DesktopColorScheme.Dark, Internal.DesktopWindowOptionValidation.ToHostOptions(
            new DesktopWindowOptions { ColorScheme = Runic.Platform.DesktopColorScheme.Dark }).ColorScheme);
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            DesktopSurface.ValidateWindowOptions(new DesktopWindowOptions { ColorScheme = (Runic.Platform.DesktopColorScheme)7 }));
        Assert.Contains("ColorScheme", error.Message);
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public async Task Gtk4IgnoresColorSchemeWithAWarning()
    {
        await using var host = await StartGtk4HostAsync();

        var options = new DesktopWindowOptions { HighContrast = false, ColorScheme = Runic.Platform.DesktopColorScheme.Light };

        // The option diagnostics, as the other GTK 4 preflight tests: without GTK 4 on the machine, Validate also reports
        // the unavailable presentation, so it is not valid there whatever the options say.
        var warning = Assert.Single(host.GetPresentationPreflight(options).OptionDiagnostics);
        Assert.Equal(("color-scheme-unsupported", DesktopDiagnosticSeverity.Warning, "DesktopWindowOptions.ColorScheme"),
            (warning.Code, warning.Severity, warning.Option));
        var validation = host.Validate(options);
        Assert.Equal("", string.Join("; ", validation.Errors.Where(static item => item.Option == "DesktopWindowOptions.ColorScheme")
            .Select(static item => $"{item.Code}: {item.Message}")));
        Assert.Contains(validation.Warnings, static item => item.Code == "color-scheme-unsupported");
    }

    [Test]
    public async Task BrowserPreflightReportsIgnoredWindowOptionsAsWarnings()
    {
        await using var host = await DesktopHost.StartAsync();

        var validation = host.Validate(new DesktopWindowOptions
        {
            Browser = BrowserKind.Chrome,
            Frameless = true,
            Transparent = true,
            ProfileName = "default",
        });

        Assert.All(validation.Warnings, static item => Assert.Equal("window-option-unsupported", item.Code));
        Assert.Equal(
            ["DesktopWindowOptions.Frameless", "DesktopWindowOptions.ProfileName", "DesktopWindowOptions.Transparent"],
            validation.Warnings.Select(static item => item.Option!).Order());
        Assert.DoesNotContain(validation.Errors, static item => item.Code == "window-option-unsupported");
    }

    [Test]
    public async Task FirefoxPreflightReportsPlacementProxyAndAnUnappliedGrant()
    {
        await using var host = await DesktopHost.StartAsync();

        var preflight = host.GetPresentationPreflight(new DesktopWindowOptions
        {
            Browser = BrowserKind.Firefox,
            X = 1,
            Y = 2,
            ProxyServer = "http://proxy.invalid:8080",
            AllowedPermissions = DesktopPermissionGrant.MediaCapture,
        });

        Assert.Contains(preflight.OptionDiagnostics, static item => item.Option == "DesktopWindowOptions.X");
        Assert.Contains(preflight.OptionDiagnostics, static item => item.Option == "DesktopWindowOptions.ProxyServer");
        var grant = Assert.Single(preflight.OptionDiagnostics, static item => item.Code == "permission-grant-unsupported");
        Assert.Equal("DesktopWindowOptions.AllowedPermissions", grant.Option);
        Assert.Equal(DesktopDiagnosticSeverity.Warning, grant.Severity);
    }

    [Test]
    public async Task InstalledBrowsersReportANegativePositionAsIgnored()
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new OptionsReportingFactory(),
        });
        static bool IsPosition(DesktopDiagnostic item) =>
            item.Option is "DesktopWindowOptions.X" or "DesktopWindowOptions.Y";

        var browser = host.Validate(new DesktopWindowOptions { Browser = BrowserKind.Chrome, X = 10, Y = -20 });
        var ignored = Assert.Single(browser.Diagnostics, IsPosition);
        Assert.Equal("DesktopWindowOptions.Y", ignored.Option);
        Assert.Equal("window-option-unsupported", ignored.Code);
        Assert.Equal(DesktopDiagnosticSeverity.Warning, ignored.Severity);

        var fallback = host.Validate(new DesktopWindowOptions
        {
            PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
            X = -10,
            Y = 20,
        });
        var fallbackIgnored = Assert.Single(fallback.Diagnostics, IsPosition);
        Assert.Equal("DesktopWindowOptions.X", fallbackIgnored.Option);
        Assert.Equal(DesktopDiagnosticSeverity.Warning, fallbackIgnored.Severity);
        Assert.Contains("browser fallback", fallbackIgnored.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(host.Validate(new DesktopWindowOptions { Browser = BrowserKind.Chrome, X = 10, Y = 20 }).Diagnostics,
            IsPosition);
    }

    // SEC-11: Chromium cannot limit an automatic grant to one origin, so it asks the user instead.
    [Test]
    public async Task ExplicitChromiumGrantIsReportedAsAskingTheUser()
    {
        await using var host = await DesktopHost.StartAsync();

        var preflight = host.GetPresentationPreflight(new DesktopWindowOptions
        {
            Browser = BrowserKind.Chrome,
            AllowedPermissions = DesktopPermissionGrant.MediaCapture,
        });

        var grant = Assert.Single(preflight.OptionDiagnostics);
        Assert.Equal("permission-grant-unsupported", grant.Code);
        Assert.Equal(DesktopDiagnosticSeverity.Warning, grant.Severity);
        Assert.Equal("DesktopWindowOptions.AllowedPermissions", grant.Option);
        Assert.Contains("asks the user", grant.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task BrowserFallbackReportsThatItWithholdsGrants()
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new OptionsReportingFactory(),
        });

        var preflight = host.GetPresentationPreflight(new DesktopWindowOptions
        {
            PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
            AllowedPermissions = DesktopPermissionGrant.MediaCapture,
        });

        var withheld = Assert.Single(preflight.OptionDiagnostics, static item => item.Code == "permission-grant-withheld");
        Assert.Equal(DesktopDiagnosticSeverity.Warning, withheld.Severity);
        Assert.Equal("DesktopWindowOptions.AllowedPermissions", withheld.Option);
    }

    [Test]
    [DisplayName("UnlaunchableBrowsersAreReportedAsUnsupported(browser: $browser)")]
    [Arguments(BrowserKind.Safari)]
    [Arguments(BrowserKind.Opera)]
    public async Task UnlaunchableBrowsersAreReportedAsUnsupported(BrowserKind browser)
    {
        await using var host = await DesktopHost.StartAsync();

        var validation = host.Validate(new DesktopWindowOptions { Browser = browser });

        var error = Assert.Single(validation.Errors);
        Assert.Equal("browser-unsupported", error.Code);
        Assert.Equal("DesktopWindowOptions.Browser", error.Option);
    }

    [Test]
    public async Task ValidationReportsInvalidValuesInsteadOfThrowing()
    {
        await using var host = await DesktopHost.StartAsync();

        var validation = host.Validate(new DesktopWindowOptions { Browser = (BrowserKind)999 });

        Assert.Equal("window-option-invalid", Assert.Single(validation.Errors).Code);
    }

    [Test]
    [Arguments(0, 600, null)]
    [Arguments(-1, 600, null)]
    [Arguments(800, -600, null)]
    [Arguments(800, 600, -1)]
    public async Task ValidationReportsANonPositiveWindowSize(int width, int height, int? minimum)
    {
        await using var host = await DesktopHost.StartAsync();

        var validation = host.Validate(new DesktopWindowOptions
        {
            Width = width, Height = height, MinimumWidth = minimum, MinimumHeight = minimum,
        });

        var error = Assert.Single(validation.Errors);
        Assert.Equal("window-option-invalid", error.Code);
        Assert.Contains("must be positive", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task CloseConfirmationInABrowserIsACodedDiagnostic()
    {
        await using var host = await DesktopHost.StartAsync();
        static ValueTask<bool> Confirm(CancellationToken _) => ValueTask.FromResult(true);

        var explicitBrowser = host.Validate(new DesktopWindowOptions { Browser = BrowserKind.Chrome, ConfirmCloseAsync = Confirm });
        var fallback = host.GetPresentationPreflight(new DesktopWindowOptions
        {
            PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
            ConfirmCloseAsync = Confirm,
        });

        var error = Assert.Single(explicitBrowser.Errors, static item => item.Code == "close-confirmation-unavailable");
        Assert.Equal("DesktopWindowOptions.ConfirmCloseAsync", error.Option);
        Assert.Contains("BrowserKind.Embedded", error.Remediation);
        var warning = Assert.Single(fallback.OptionDiagnostics, static item => item.Code == "close-confirmation-unavailable");
        Assert.Equal(DesktopDiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("RequestedOnly", warning.Remediation);
    }

    [Test]
    public async Task OneCoordinateOfAPairIsReported()
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new OptionsReportingFactory(),
        });

        var preflight = host.GetPresentationPreflight(new DesktopWindowOptions { X = 5, MinimumHeight = 100 });

        Assert.Contains(preflight.OptionDiagnostics, static item =>
            item.Code == "window-option-incomplete" && item.Option == "DesktopWindowOptions.X");
        Assert.Contains(preflight.OptionDiagnostics, static item =>
            item.Code == "window-option-incomplete" && item.Option == "DesktopWindowOptions.MinimumHeight");
    }

    [Test]
    public async Task CustomProvidersReportTheirOptionsAndCloseConfirmationSupport()
    {
        var factory = new OptionsReportingFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory });

        var validation = host.Validate(new DesktopWindowOptions
        {
            Kiosk = true,
            ProxyServer = "http://proxy.invalid:8080",
            ConfirmCloseAsync = static _ => ValueTask.FromResult(true),
        });

        Assert.True(factory.Received?.Kiosk);
        Assert.Contains(validation.Errors, static item => item.Option == "DesktopWindowOptions.Kiosk");
        Assert.Contains(validation.Errors, static item => item.Option == "DesktopWindowOptions.ConfirmCloseAsync");
        Assert.Contains(validation.Warnings, static item => item.Option == "DesktopWindowOptions.ProxyServer");
    }

    [Test]
    public async Task ValidationLogsEachDiagnosticThroughTheDesktopLogger()
    {
        var loggers = new RecordingLoggerFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new OptionsReportingFactory(),
            LoggerFactory = loggers,
        });

        var validation = host.Validate(new DesktopWindowOptions { Kiosk = true, ProxyServer = "http://proxy.invalid" });

        Assert.Equal(validation.Diagnostics.Count, loggers.Entries.Count);
        Assert.Contains(loggers.Entries, static entry =>
            entry.Category == "Runic.Desktop" && entry.Level == LogLevel.Error && entry.EventId.Id == 3002 &&
            entry.Message.Contains("DesktopWindowOptions.Kiosk", StringComparison.Ordinal));
        Assert.Contains(loggers.Entries, static entry => entry.Level == LogLevel.Warning && entry.EventId.Id == 3003);
    }

    [Test]
    public async Task BrowserFallbackOpensWithoutTheGrant()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Directory.CreateTempSubdirectory("runic-desktop-grant-fallback-");
        try
        {
            var executable = Path.Combine(folder.FullName, OperatingSystem.IsMacOS()
                ? "Google Chrome.app/Contents/MacOS/Google Chrome"
                : "google-chrome");
            var arguments = Path.Combine(folder.FullName, "arguments.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllTextAsync(executable, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{arguments}.tmp'\nmv '{arguments}.tmp' '{arguments}'\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                BrowserFolder = folder.FullName,
                WindowHostFactory = new OptionsReportingFactory { IsSupported = false },
                DiagnosticSink = diagnostics.Enqueue,
                WaitForConnection = false,
            });
            await using var surface = await host.CreateSurfaceAsync();
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Chrome,
                PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
                AllowedPermissions = DesktopPermissionGrant.MediaCapture,
            });

            Assert.True(window.IsFallback);
            var withheld = Assert.Single(diagnostics, static item => item.Code == "permission-grant-withheld");
            Assert.False(string.IsNullOrEmpty(withheld.CorrelationId));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(arguments) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            var launched = await File.ReadAllLinesAsync(arguments);
            Assert.Contains("--deny-permission-prompts", launched);
            Assert.DoesNotContain("--auto-accept-camera-and-microphone-capture", launched);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Test]
    public async Task BrowserFallbackOpensWithoutCloseConfirmation()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Directory.CreateTempSubdirectory("runic-desktop-close-fallback-");
        try
        {
            var executable = Path.Combine(folder.FullName, OperatingSystem.IsMacOS()
                ? "Google Chrome.app/Contents/MacOS/Google Chrome"
                : "google-chrome");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                BrowserFolder = folder.FullName,
                WindowHostFactory = new OptionsReportingFactory { IsSupported = false },
                DiagnosticSink = diagnostics.Enqueue,
                WaitForConnection = false,
            });
            await using var surface = await host.CreateSurfaceAsync();
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Chrome,
                PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
                ConfirmCloseAsync = static _ => ValueTask.FromResult(false),
            });

            Assert.True(window.IsFallback);
            var unavailable = Assert.Single(diagnostics, static item => item.Code == "close-confirmation-unavailable");
            Assert.Equal(DesktopDiagnosticSeverity.Warning, unavailable.Severity);
            Assert.False(string.IsNullOrEmpty(unavailable.CorrelationId));
            Assert.False(window.Capabilities.HasFlag(DesktopWindowCapabilities.CloseConfirmation));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Test]
    public void LinuxSelectionDiagnosticsOfferABrowserAndReportUndefinedBackends()
    {
        var unselected = DesktopPlatform.GetLinuxSelectionDiagnostic(new LinuxDesktopOptions(), factory: null);
        var undefined = DesktopPlatform.GetLinuxSelectionDiagnostic(
            new LinuxDesktopOptions { EmbeddedBackend = (LinuxEmbeddedBackend)42 }, factory: null);

        Assert.Equal("linux-embedded-backend-not-selected", unselected?.Code);
        Assert.Contains("BrowserKind.Any", unselected!.Remediation);
        Assert.Contains("EmbeddedThenBrowser", unselected.Remediation);
        Assert.Equal("linux-embedded-backend-invalid", undefined?.Code);
        Assert.Equal("DesktopHostOptions.Linux.EmbeddedBackend", undefined!.Option);
    }

    [Test]
    public async Task UnselectedLinuxToolkitIsNotNamedInOptionWarnings()
    {
        await using var host = await DesktopHost.StartAsync();

        var preflight = host.GetPresentationPreflight(new DesktopWindowOptions { ProfilePath = "/tmp/runic-profile" });

        // Windows applies the profile; Linux reports the missing toolkit through availability instead.
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("linux-embedded-backend-not-selected", preflight.Diagnostics[0].Code);
        }
        Assert.DoesNotContain(preflight.OptionDiagnostics, static item => item.Message.Contains("WebKitGTK", StringComparison.Ordinal));
    }

    [SupportedOSPlatform("linux")]
    private static ValueTask<DesktopHost> StartGtk4HostAsync() => DesktopHost.StartAsync(new DesktopHostOptions
    {
        Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk4WebKit6 },
        WindowHostFactory = new Gtk4WindowHostFactory(),
    });

    // Rejects kiosk windows and does not advertise close confirmation.
    private sealed class OptionsReportingFactory : IDesktopWindowHostFactory
    {
        public bool IsSupported { get; init; } = true;
        public DesktopWindowHostOptions? Received { get; private set; }

        public IDesktopWindowHost Create() => throw new InvalidOperationException("Validation must not create a host.");

        public IReadOnlyList<DesktopDiagnostic> ValidateOptions(DesktopWindowHostOptions options)
        {
            Received = options;
            return options.Kiosk
                ? [new DesktopDiagnostic(DesktopErrorCategory.CapabilityDenied, "window-option-unsupported", "No kiosk.", false)
                {
                    Option = "DesktopWindowOptions.Kiosk",
                }]
                : [];
        }
    }

    internal sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public ConcurrentQueue<(string Category, LogLevel Level, EventId EventId, string Message)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Logger(RecordingLoggerFactory owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((category, logLevel, eventId, formatter(state, exception)));
        }
    }
}
