using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Runic.Desktop.Gtk4;

namespace Runic.Desktop.Tests;

public sealed class ConfigurationValidationTests
{
    [Fact]
    public void WindowsDefaultToTheEmbeddedPresentation()
    {
        Assert.Equal(BrowserKind.Embedded, new DesktopWindowOptions().Browser);
        Assert.Equal(DesktopPresentationPolicy.RequestedOnly, new DesktopWindowOptions().PresentationPolicy);
    }

    public static TheoryData<string, DesktopWindowOptions> Gtk4UnsupportedOptions => new()
    {
        { "X", new DesktopWindowOptions { X = 10, Y = 20 } },
        { "Y", new DesktopWindowOptions { X = 10, Y = 20 } },
        { "Centered", new DesktopWindowOptions { Centered = true } },
        { "Transparent", new DesktopWindowOptions { Transparent = true } },
        { "HighContrast", new DesktopWindowOptions { HighContrast = true } },
        { "ProfilePath", new DesktopWindowOptions { ProfilePath = "/tmp/runic-profile" } },
        { "BrowserArguments", new DesktopWindowOptions { BrowserArguments = "--flag" } },
        { "IconFile", new DesktopWindowOptions { IconFile = "icon.png" } },
    };

    [Theory]
    [MemberData(nameof(Gtk4UnsupportedOptions))]
    [SupportedOSPlatform("linux")]
    public async Task Gtk4PreflightReportsEachUnsupportedOption(string option, DesktopWindowOptions requested)
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


    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task ExplicitChromiumGrantIsNotReported()
    {
        await using var host = await DesktopHost.StartAsync();

        var preflight = host.GetPresentationPreflight(new DesktopWindowOptions
        {
            Browser = BrowserKind.Chrome,
            AllowedPermissions = DesktopPermissionGrant.MediaCapture,
        });

        Assert.Empty(preflight.OptionDiagnostics);
    }

    [Fact]
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

    [Theory]
    [InlineData(BrowserKind.Safari)]
    [InlineData(BrowserKind.Opera)]
    public async Task UnlaunchableBrowsersAreReportedAsUnsupported(BrowserKind browser)
    {
        await using var host = await DesktopHost.StartAsync();

        var validation = host.Validate(new DesktopWindowOptions { Browser = browser });

        var error = Assert.Single(validation.Errors);
        Assert.Equal("browser-unsupported", error.Code);
        Assert.Equal("DesktopWindowOptions.Browser", error.Option);
    }

    [Fact]
    public async Task ValidationReportsInvalidValuesInsteadOfThrowing()
    {
        await using var host = await DesktopHost.StartAsync();

        var validation = host.Validate(new DesktopWindowOptions
        {
            Browser = BrowserKind.Chrome,
            ConfirmCloseAsync = static _ => ValueTask.FromResult(true),
        });

        Assert.Equal("window-option-invalid", Assert.Single(validation.Errors).Code);
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

            Assert.True(window.FellBack);
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

    [Fact]
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

    [Fact]
    public async Task UnselectedLinuxToolkitIsNotNamedInOptionWarnings()
    {
        await using var host = await DesktopHost.StartAsync();

        var preflight = host.GetPresentationPreflight(new DesktopWindowOptions { ProfilePath = "/tmp/runic-profile" });

        // Windows applies the profile; Linux reports the missing toolkit through availability instead.
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("linux-embedded-backend-not-selected", preflight.Diagnostic?.Code);
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
