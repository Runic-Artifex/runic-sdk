using TUnit.Core;
using Runic.Desktop.Gtk4;

namespace Runic.Desktop.Tests;

public sealed class Gtk4ProfileTests
{
    [Test]
    public void Gtk4IgnoresThePositionOfARestoredPlacementButRejectsAnExplicitOne()
    {
        if (!OperatingSystem.IsLinux()) return;
        var factory = new Gtk4WindowHostFactory();
        Assert.True(factory.Capabilities.HasFlag(DesktopWindowCapabilities.State));
        var placement = new DesktopWindowPlacement { X = 100, Y = 50, Width = 900, Height = 700, Maximized = true };
        Assert.Empty(factory.ValidateOptions(new DesktopWindowHostOptions { X = 100, Y = 50, Placement = placement }));
        var explicitPosition = factory.ValidateOptions(new DesktopWindowHostOptions { X = 100, Y = 50 });
        Assert.Equal(["DesktopWindowOptions.X", "DesktopWindowOptions.Y"], explicitPosition.Select(static item => item.Option));
    }

    [Test]
    public void WithGtk4SelectsTheBackendAndTheFactoryTogether()
    {
        var original = new DesktopHostOptions { Port = 0, BrowserFolder = "/opt/browsers" };

        var options = original.WithGtk4();

        Assert.Equal(LinuxEmbeddedBackend.Gtk4WebKit6, options.Linux.EmbeddedBackend);
        Assert.Equal("/opt/browsers", options.BrowserFolder);
        Assert.Null(original.Linux.EmbeddedBackend);
        if (OperatingSystem.IsLinux())
        {
            Assert.IsType<Gtk4WindowHostFactory>(options.WindowHostFactory);
            Assert.Same(options.WindowHostFactory, options.WithGtk4().WindowHostFactory);
        }
        else
        {
            // Windows and macOS keep their own embedded host.
            Assert.Null(options.WindowHostFactory);
        }
    }

    [Test]
    public void WithGtk4RejectsAnotherFactory()
    {
        var options = new DesktopHostOptions { WindowHostFactory = new UnavailableFactory([]) };

        var error = Assert.Throws<ArgumentException>(() => options.WithGtk4());

        Assert.Contains(nameof(UnavailableFactory), error.Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task WithGtk4HostReportsEveryMissingLibrary()
    {
        if (!OperatingSystem.IsLinux() || !LinuxDesktopRuntime.CanUse(LinuxEmbeddedBackend.Gtk4WebKit6)) return;
        using var scope = new LinuxProbeScope(static _ => false);
        var embedded = DesktopPlatform.GetAvailability(new DesktopHostOptions().WithGtk4()).Embedded;

        Assert.False(embedded.IsAvailable);
        Assert.Equal(["gtk4-runtime-missing", "webkitgtk6-runtime-missing"], embedded.Diagnostics.Select(static item => item.Code));
    }

    [Test]
    public async Task WithGtk4HostAcceptsEitherWebKitSoname()
    {
        if (!OperatingSystem.IsLinux() || !LinuxDesktopRuntime.CanUse(LinuxEmbeddedBackend.Gtk4WebKit6)) return;
        using var scope = new LinuxProbeScope(static name => name is "libgtk-4.so.1" or "libwebkitgtk-6.0.so.0");
        var embedded = DesktopPlatform.GetAvailability(new DesktopHostOptions().WithGtk4()).Embedded;

        Assert.True(embedded.IsAvailable);
        Assert.Empty(embedded.Diagnostics);
    }

    [Test]
    public async Task Gtk4SelectionWithoutItsProviderReportsTheProviderAndEachMissingPrerequisite()
    {
        if (!OperatingSystem.IsLinux() || !LinuxDesktopRuntime.CanUse(LinuxEmbeddedBackend.Gtk4WebKit6)) return;
        using var scope = new LinuxProbeScope(static _ => false, graphicalSession: false);
        LinuxDesktopOptions linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk4WebKit6 };
        await using var host = await DesktopHost.StartAsync(new() { Linux = linux });
        string[] expected = ["gtk4-provider-missing", "gtk4-runtime-missing", "webkitgtk6-runtime-missing", "graphical-session-missing"];

        var validation = host.Validate(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
        var embedded = DesktopPlatform.GetAvailability(new() { Linux = linux }).Embedded;

        Assert.Equal(expected, validation.Errors.Select(static item => item.Code));
        Assert.Contains("WithGtk4()", validation.Errors[0].Remediation, StringComparison.Ordinal);
        Assert.Equal(expected, embedded.Diagnostics.Select(static item => item.Code));
        Assert.Equal("gtk4-provider-missing", embedded.Diagnostics[0].Code);
    }

    [Test]
    public async Task WithGtk4AfterGtk3ReportsTheToolkitConflictOnce()
    {
        if (!OperatingSystem.IsLinux() || !LinuxDesktopRuntime.CanUse(LinuxEmbeddedBackend.Gtk4WebKit6)) return;
        bool claimed = LinuxDesktopRuntime.CanUse(LinuxEmbeddedBackend.Gtk3WebKit41);
        LinuxDesktopRuntime.ClaimBackend(LinuxEmbeddedBackend.Gtk3WebKit41);
        try
        {
            using var scope = new LinuxProbeScope(static _ => false);
            var options = new DesktopHostOptions().WithGtk4();
            await using var host = await DesktopHost.StartAsync(options);

            var embedded = DesktopPlatform.GetAvailability(options).Embedded;
            var validation = host.Validate(new DesktopWindowOptions { Browser = BrowserKind.Embedded });

            string[] expected = ["linux-embedded-backend-conflict", "gtk4-runtime-missing", "webkitgtk6-runtime-missing"];
            Assert.False(embedded.IsAvailable);
            Assert.Equal(expected, embedded.Diagnostics.Select(static item => item.Code));
            Assert.Equal(expected, validation.Errors.Select(static item => item.Code));
        }
        finally
        {
            // Only release a claim this test made; GTK 3 may really be running in this process.
            if (claimed) LinuxDesktopRuntime.ReleaseBackendClaim();
        }
    }

    [Test]
    public async Task UnavailableCustomFactoryListsAllOfItsDiagnostics()
    {
        DesktopDiagnostic[] reported =
        [
            new(DesktopErrorCategory.Unavailable, "first-missing", "First.", Retryable: false),
            new(DesktopErrorCategory.Unavailable, "second-missing", "Second.", Retryable: false),
        ];
        DesktopHostOptions options = new() { WindowHostFactory = new UnavailableFactory(reported) };
        await using var host = await DesktopHost.StartAsync(options);

        var embedded = DesktopPlatform.GetAvailability(options).Embedded;
        var preflight = host.GetPresentationPreflight(new() { Browser = BrowserKind.Embedded });
        var validation = host.Validate(new() { Browser = BrowserKind.Embedded });

        Assert.Equal(reported, embedded.Diagnostics);
        Assert.Equal("first-missing", preflight.Diagnostics[0].Code);
        Assert.Equal(reported, preflight.Diagnostics);
        Assert.Equal(["first-missing", "second-missing"], validation.Errors.Select(static item => item.Code));
        Assert.Contains(reported[1], DesktopPlatform.GetAvailability(options).Diagnostics);
    }

    [Test]
    public async Task UnavailableFactoryWithoutDiagnosticsKeepsTheGenericDiagnostic()
    {
        var embedded = DesktopPlatform.GetAvailability(new() { WindowHostFactory = new UnavailableFactory([]) }).Embedded;

        Assert.Equal("custom-window-host-unavailable", Assert.Single(embedded.Diagnostics).Code);
    }

    [Test]
    public void PresentationDiagnosticsAreTheListGiven()
    {
        var diagnostic = new DesktopDiagnostic(DesktopErrorCategory.Unavailable, "missing", "Missing.", Retryable: false);

        Assert.Equal([diagnostic], new DesktopPresentationAvailability(BrowserKind.Chrome, false, null, DesktopWindowCapabilities.None, [diagnostic]).Diagnostics);
        Assert.Empty(new DesktopPresentationAvailability(BrowserKind.Chrome, true, "/bin/chrome", DesktopWindowCapabilities.None, []).Diagnostics);
        Assert.Throws<ArgumentNullException>(() => new DesktopPresentationAvailability(BrowserKind.Chrome, true, null, DesktopWindowCapabilities.None, null!));
    }

    [Test]
    public void PresentationAvailabilityComparesDiagnosticsByValue()
    {
        DesktopPresentationAvailability Create() =>
            new(BrowserKind.Embedded, false, null, DesktopWindowCapabilities.None, [Missing("first"), Missing("second")]);

        var left = Create();
        var right = Create();

        Assert.NotSame(left.Diagnostics, right.Diagnostics);
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, right with { Diagnostics = [Missing("first")] });
        Assert.NotEqual(left, right with { Diagnostics = [Missing("second"), Missing("first")] });
    }

    [Test]
    public void RepeatedAvailabilityIsEqual()
    {
        DesktopHostOptions options = new() { WindowHostFactory = new UnavailableFactory([Missing("first"), Missing("second")]) };

        var first = DesktopPlatform.GetAvailability(options);
        var second = DesktopPlatform.GetAvailability(options);

        Assert.NotSame(first.Presentations, second.Presentations);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, second with { Presentations = second.Presentations.Take(1).ToArray() });
        Assert.NotEqual(first, second with { LinuxBackends = [new(LinuxEmbeddedBackend.Gtk3WebKit41, false, [Missing("x")])] });
    }

    [Test]
    public void PresentationDiagnosticsAreAReadOnlyCopy()
    {
        List<DesktopDiagnostic> given = [Missing("first"), Missing("second")];
        var availability = new DesktopPresentationAvailability(BrowserKind.Embedded, false, null, DesktopWindowCapabilities.None, given);
        given.Add(Missing("later"));

        var relisted = availability with { Diagnostics = [Missing("third")] };
        var cleared = availability with { Diagnostics = [] };

        Assert.Equal(["first", "second"], availability.Diagnostics.Select(static item => item.Code));
        Assert.Equal([Missing("third")], relisted.Diagnostics);
        Assert.Empty(cleared.Diagnostics);
        Assert.Throws<ArgumentException>(() => availability with { Diagnostics = [null!] });
        Assert.Throws<ArgumentNullException>(() => availability with { Diagnostics = null! });

        // The list is a read-only view; the record's storage cannot be reached through it.
        Assert.IsNotType<DesktopDiagnostic[]>(availability.Diagnostics);
        Assert.Throws<NotSupportedException>(() => ((IList<DesktopDiagnostic>)availability.Diagnostics)[0] = Missing("changed"));
        Assert.Equal("first", availability.Diagnostics[0].Code);
    }

    [Test]
    public async Task AvailabilityOfTheOptionsIsWhatTheHostsPreflightUses()
    {
        DesktopHostOptions[] configurations =
        [
            new(),
            new DesktopHostOptions().WithGtk4(),
            new() { WindowHostFactory = new SupportedFactory() },
            new() { WindowHostFactory = new UnavailableFactory([Missing("first")]) },
        ];
        foreach (var options in configurations)
        {
            await using var host = await DesktopHost.StartAsync(options);

            var availability = DesktopPlatform.GetAvailability(options);
            var embedded = host.GetPresentationPreflight(new() { Browser = BrowserKind.Embedded });
            var fallback = host.GetPresentationPreflight(new() { PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser });

            Assert.Equal(availability.Embedded, embedded.Preferred);
            Assert.Equal(availability.Embedded, fallback.Preferred);
            Assert.Equal(availability.GetPresentation(BrowserKind.Any), fallback.Fallback);
        }
    }

    [Test]
    public void AvailabilityReportsTheConfiguredWindowHost()
    {
        var factory = new SupportedFactory();

        var embedded = DesktopPlatform.GetAvailability(new() { WindowHostFactory = factory }).Embedded;

        Assert.True(embedded.IsAvailable);
        Assert.Equal(factory.Capabilities, embedded.Capabilities);
        Assert.Empty(embedded.Diagnostics);
    }

    [Test]
    public void AvailabilityRejectsALinuxFactoryForAnotherBackend()
    {
        if (!OperatingSystem.IsLinux()) return;
        var options = new DesktopHostOptions().WithGtk4() with { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } };

        Assert.Throws<ArgumentException>(() => DesktopPlatform.GetAvailability(options));
        Assert.Throws<ArgumentException>(() => DesktopPlatform.GetAvailability(new() { Linux = null! }));
    }

    [Test]
    public void DefaultOptionsOnLinuxSelectNoEmbeddedBackendAndListBothToolkits()
    {
        var availability = DesktopPlatform.GetAvailability();
        if (!OperatingSystem.IsLinux())
        {
            Assert.Empty(availability.LinuxBackends);
            return;
        }

        Assert.Equal("linux", availability.Platform);
        Assert.False(availability.Embedded.IsAvailable);
        Assert.Equal(DesktopWindowCapabilities.None, availability.Embedded.Capabilities);
        Assert.Equal("linux-embedded-backend-not-selected", availability.Embedded.Diagnostics[0].Code);
        Assert.Equal([LinuxEmbeddedBackend.Gtk3WebKit41, LinuxEmbeddedBackend.Gtk4WebKit6], availability.LinuxBackends.Select(static item => item.Backend));
        Assert.Same(availability.Embedded, availability.Embedded);
    }

    [Test]
    public void LinuxBackendsReportMissingLibraries()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var scope = new LinuxProbeScope(static name => name is "libgtk-3.so.0" or "libwebkit2gtk-4.1.so.0");

        var backends = DesktopPlatform.GetAvailability().LinuxBackends;

        Assert.True(backends[0].LibrariesDiscovered);
        Assert.Empty(backends[0].Diagnostics);
        Assert.False(backends[1].LibrariesDiscovered);
        Assert.Equal("webkitgtk6-runtime-missing", Assert.Single(backends[1].Diagnostics).Code);
    }

    [Test]
    public void BuiltInEmbeddedCapabilitiesAreWhatTheBuiltInHostAdvertises()
    {
        var advertised = DesktopPlatform.BuiltInEmbeddedCapabilities;
        const DesktopWindowCapabilities known = DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.Focus |
            DesktopWindowCapabilities.Minimize | DesktopWindowCapabilities.Maximize | DesktopWindowCapabilities.Resize |
            DesktopWindowCapabilities.Move | DesktopWindowCapabilities.Announcements;
        const DesktopWindowCapabilities placement = DesktopWindowCapabilities.FullScreen | DesktopWindowCapabilities.MonitorPlacement;
        if (OperatingSystem.IsWindows())
            Assert.Equal(known | DesktopWindowCapabilities.State | DesktopWindowCapabilities.FileDrop | placement, advertised);
        else if (OperatingSystem.IsMacOS())
            Assert.Equal(known | DesktopWindowCapabilities.State | DesktopWindowCapabilities.MenuBar | DesktopWindowCapabilities.ContextMenu | placement,
                advertised);
        else if (OperatingSystem.IsLinux())
            Assert.Equal(known, advertised);
        // Each built-in host reports its KnownCapabilities plus only what its loaded toolkit adds.
        Assert.Equal(Internal.WindowsWebView2Host.KnownCapabilities | Internal.MacOsWkWebViewHost.KnownCapabilities | Internal.LinuxWebKitGtkHost.KnownCapabilities,
            known | DesktopWindowCapabilities.State | DesktopWindowCapabilities.FileDrop | DesktopWindowCapabilities.MenuBar |
            DesktopWindowCapabilities.ContextMenu | placement);
    }

    [Test]
    public void GetPresentationResolvesRequestsLikeAWindow()
    {
        DesktopPresentationAvailability Entry(BrowserKind browser, bool available) =>
            new(browser, available, available ? $"/bin/{browser}" : null, DesktopWindowCapabilities.None, available ? [] : [Missing($"{browser}-missing")]);
        var embedded = Entry(BrowserKind.Embedded, true);
        var availability = new DesktopAvailabilityResult("test",
            [Entry(BrowserKind.Chrome, false), Entry(BrowserKind.Firefox, true), Entry(BrowserKind.Edge, true), embedded]);

        Assert.Same(embedded, availability.Embedded);
        Assert.Equal(embedded, availability.GetPresentation(BrowserKind.Embedded));
        Assert.Equal(BrowserKind.Firefox, availability.GetPresentation(BrowserKind.Any).Browser);
        Assert.Equal(BrowserKind.Edge, availability.GetPresentation(BrowserKind.ChromiumBased).Browser);
        Assert.Equal("Chrome-missing", availability.GetPresentation(BrowserKind.Chrome).Diagnostics[0].Code);
        Assert.Equal("browser-unsupported", availability.GetPresentation(BrowserKind.Safari).Diagnostics[0].Code);
        Assert.Equal("browser-unsupported", availability.GetPresentation(BrowserKind.Opera).Diagnostics[0].Code);
        var missing = availability.GetPresentation(BrowserKind.Chromium);
        Assert.Equal((BrowserKind.Chromium, false, "presentation-unavailable"), (missing.Browser, missing.IsAvailable, missing.Diagnostics[0].Code));
        Assert.Throws<ArgumentOutOfRangeException>(() => availability.GetPresentation((BrowserKind)999));

        var none = new DesktopAvailabilityResult("test", [Entry(BrowserKind.Chrome, false)]);
        var any = none.GetPresentation(BrowserKind.Any);
        Assert.Equal((BrowserKind.Any, false, "presentation-unavailable"), (any.Browser, any.IsAvailable, any.Diagnostics[0].Code));
        Assert.Equal(BrowserKind.ChromiumBased, none.GetPresentation(BrowserKind.ChromiumBased).Browser);
        Assert.Equal("presentation-unavailable", none.Embedded.Diagnostics[0].Code);
    }

    [Test]
    public void AvailabilityListsAreReadOnlyCopiesComparedByValue()
    {
        List<DesktopPresentationAvailability> given = [new(BrowserKind.Embedded, false, null, DesktopWindowCapabilities.None, [Missing("first")])];
        var availability = new DesktopAvailabilityResult("test", given);
        given.Clear();

        Assert.Single(availability.Presentations);
        Assert.Throws<NotSupportedException>(() => ((IList<DesktopPresentationAvailability>)availability.Presentations).Clear());
        Assert.Throws<ArgumentException>(() => new DesktopAvailabilityResult("test", [null!]));
        Assert.Throws<ArgumentNullException>(() => availability with { LinuxBackends = null! });

        LinuxEmbeddedBackendAvailability Backend() => new(LinuxEmbeddedBackend.Gtk4WebKit6, false, [Missing("first"), Missing("second")]);
        Assert.Equal(Backend(), Backend());
        Assert.Equal(Backend().GetHashCode(), Backend().GetHashCode());
        Assert.NotEqual(Backend(), Backend() with { Diagnostics = [Missing("second"), Missing("first")] });
        Assert.Equal(
            availability with { LinuxBackends = [Backend()] },
            new DesktopAvailabilityResult("test", [new(BrowserKind.Embedded, false, null, DesktopWindowCapabilities.None, [Missing("first")])]) { LinuxBackends = [Backend()] });
    }

    private static DesktopDiagnostic Missing(string code) => new(DesktopErrorCategory.Unavailable, code, "Missing.", Retryable: false);

    // Decides which native libraries exist and, optionally, hides the graphical session for one test.
    private sealed class LinuxProbeScope : IDisposable
    {
        private readonly string? _display = Environment.GetEnvironmentVariable("DISPLAY");
        private readonly string? _waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        private readonly bool _graphicalSession;

        public LinuxProbeScope(Func<string, bool> probe, bool graphicalSession = true)
        {
            _graphicalSession = graphicalSession;
            LinuxDesktopRuntime.LibraryProbeOverride = probe;
            if (!graphicalSession)
            {
                Environment.SetEnvironmentVariable("DISPLAY", null);
                Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
            }
        }

        public void Dispose()
        {
            LinuxDesktopRuntime.LibraryProbeOverride = null;
            if (!_graphicalSession)
            {
                Environment.SetEnvironmentVariable("DISPLAY", _display);
                Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", _waylandDisplay);
            }
        }
    }

    private sealed class SupportedFactory : IDesktopWindowHostFactory
    {
        public bool IsSupported => true;
        public DesktopWindowCapabilities Capabilities => DesktopWindowCapabilities.Focus | DesktopWindowCapabilities.FileDrop;
        public IDesktopWindowHost Create() => throw new NotSupportedException();
    }

    private sealed class UnavailableFactory(IReadOnlyList<DesktopDiagnostic> diagnostics) : IDesktopWindowHostFactory
    {
        public bool IsSupported => false;
        public IDesktopWindowHost Create() => throw new NotSupportedException();
        public IReadOnlyList<DesktopDiagnostic> GetAvailabilityDiagnostics() => diagnostics;
    }
}
