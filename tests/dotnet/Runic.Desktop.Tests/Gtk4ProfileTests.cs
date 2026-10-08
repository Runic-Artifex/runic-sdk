using Runic.Desktop.Gtk4;

namespace Runic.Desktop.Tests;

public sealed class Gtk4ProfileTests
{
    [Fact]
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

    [Fact]
    public void WithGtk4RejectsAnotherFactory()
    {
        var options = new DesktopHostOptions { WindowHostFactory = new UnavailableFactory([]) };

        var error = Assert.Throws<ArgumentException>(() => options.WithGtk4());

        Assert.Contains(nameof(UnavailableFactory), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithGtk4HostReportsEveryMissingLibrary()
    {
        if (!OperatingSystem.IsLinux() || !LinuxDesktopRuntime.CanUse(LinuxEmbeddedBackend.Gtk4WebKit6)) return;
        using var scope = new LinuxProbeScope(static _ => false);
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions().WithGtk4());

        var embedded = Assert.Single(host.GetAvailability().Presentations, static item => item.Browser == BrowserKind.Embedded);

        Assert.False(embedded.IsAvailable);
        Assert.Equal(["gtk4-runtime-missing", "webkitgtk6-runtime-missing"], embedded.Diagnostics.Select(static item => item.Code));
        Assert.Same(embedded.Diagnostics[0], embedded.Diagnostic);
    }

    [Fact]
    public async Task WithGtk4HostAcceptsEitherWebKitSoname()
    {
        if (!OperatingSystem.IsLinux() || !LinuxDesktopRuntime.CanUse(LinuxEmbeddedBackend.Gtk4WebKit6)) return;
        using var scope = new LinuxProbeScope(static name => name is "libgtk-4.so.1" or "libwebkitgtk-6.0.so.0");
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions().WithGtk4());

        var embedded = Assert.Single(host.GetAvailability().Presentations, static item => item.Browser == BrowserKind.Embedded);

        Assert.True(embedded.IsAvailable);
        Assert.Empty(embedded.Diagnostics);
    }

    [Fact]
    public async Task Gtk4SelectionWithoutItsProviderReportsTheProviderAndEachMissingPrerequisite()
    {
        if (!OperatingSystem.IsLinux() || !LinuxDesktopRuntime.CanUse(LinuxEmbeddedBackend.Gtk4WebKit6)) return;
        using var scope = new LinuxProbeScope(static _ => false, graphicalSession: false);
        LinuxDesktopOptions linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk4WebKit6 };
        await using var host = await DesktopHost.StartAsync(new() { Linux = linux });
        string[] expected = ["gtk4-provider-missing", "gtk4-runtime-missing", "webkitgtk6-runtime-missing", "graphical-session-missing"];

        var validation = host.Validate(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
        var embedded = Assert.Single(DesktopPlatform.GetAvailability(browserFolder: null, linux).Presentations, static item => item.Browser == BrowserKind.Embedded);

        Assert.Equal(expected, validation.Errors.Select(static item => item.Code));
        Assert.Contains("WithGtk4()", validation.Errors[0].Remediation, StringComparison.Ordinal);
        Assert.Equal(expected, embedded.Diagnostics.Select(static item => item.Code));
        Assert.Equal("gtk4-provider-missing", embedded.Diagnostic?.Code);
    }

    [Fact]
    public async Task WithGtk4AfterGtk3ReportsTheToolkitConflictOnce()
    {
        if (!OperatingSystem.IsLinux() || !LinuxDesktopRuntime.CanUse(LinuxEmbeddedBackend.Gtk4WebKit6)) return;
        bool claimed = LinuxDesktopRuntime.CanUse(LinuxEmbeddedBackend.Gtk3WebKit41);
        LinuxDesktopRuntime.ClaimBackend(LinuxEmbeddedBackend.Gtk3WebKit41);
        try
        {
            using var scope = new LinuxProbeScope(static _ => false);
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions().WithGtk4());

            var embedded = Assert.Single(host.GetAvailability().Presentations, static item => item.Browser == BrowserKind.Embedded);
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

    [Fact]
    public async Task UnavailableCustomFactoryListsAllOfItsDiagnostics()
    {
        DesktopDiagnostic[] reported =
        [
            new(DesktopErrorCategory.Unavailable, "first-missing", "First.", Retryable: false),
            new(DesktopErrorCategory.Unavailable, "second-missing", "Second.", Retryable: false),
        ];
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = new UnavailableFactory(reported) });

        var embedded = Assert.Single(host.GetAvailability().Presentations, static item => item.Browser == BrowserKind.Embedded);
        var preflight = host.GetPresentationPreflight(new() { Browser = BrowserKind.Embedded });
        var validation = host.Validate(new() { Browser = BrowserKind.Embedded });

        Assert.Equal(reported, embedded.Diagnostics);
        Assert.Equal("first-missing", preflight.Diagnostic?.Code);
        Assert.Equal(reported, preflight.Diagnostics);
        Assert.Equal(["first-missing", "second-missing"], validation.Errors.Select(static item => item.Code));
        Assert.Contains(reported[1], host.GetAvailability().Diagnostics);
    }

    [Fact]
    public async Task UnavailableFactoryWithoutDiagnosticsKeepsTheGenericDiagnostic()
    {
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = new UnavailableFactory([]) });

        var embedded = Assert.Single(host.GetAvailability().Presentations, static item => item.Browser == BrowserKind.Embedded);

        Assert.Equal("custom-window-host-unavailable", Assert.Single(embedded.Diagnostics).Code);
    }

    [Fact]
    public void PresentationDiagnosticsDefaultToTheSingleDiagnostic()
    {
        var diagnostic = new DesktopDiagnostic(DesktopErrorCategory.Unavailable, "missing", "Missing.", Retryable: false);

        Assert.Equal([diagnostic], new DesktopPresentationAvailability(BrowserKind.Chrome, false, null, DesktopWindowCapabilities.None, diagnostic).Diagnostics);
        Assert.Empty(new DesktopPresentationAvailability(BrowserKind.Chrome, true, "/bin/chrome", DesktopWindowCapabilities.None, null).Diagnostics);
    }

    [Fact]
    public void PresentationAvailabilityComparesDiagnosticsByValue()
    {
        DesktopPresentationAvailability Create() => new(BrowserKind.Embedded, false, null, DesktopWindowCapabilities.None, Missing("first"))
        {
            Diagnostics = [Missing("first"), Missing("second")],
        };

        var left = Create();
        var right = Create();

        Assert.NotSame(left.Diagnostics, right.Diagnostics);
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, right with { Diagnostics = [Missing("first")] });
        Assert.NotEqual(left, right with { Diagnostics = [Missing("second"), Missing("first")] });
    }

    [Fact]
    public async Task RepeatedAvailabilityIsEqual()
    {
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = new UnavailableFactory([Missing("first"), Missing("second")]) });

        Assert.Equal(host.GetAvailability().Presentations, host.GetAvailability().Presentations);
    }

    [Fact]
    public void PresentationDiagnosticIsAlwaysTheFirstOfDiagnostics()
    {
        var availability = new DesktopPresentationAvailability(BrowserKind.Embedded, false, null, DesktopWindowCapabilities.None, Missing("first"))
        {
            Diagnostics = [Missing("first"), Missing("second")],
        };

        var replaced = availability with { Diagnostic = Missing("other") };
        var cleared = availability with { Diagnostic = null };
        var relisted = availability with { Diagnostics = [Missing("third")] };

        Assert.Equal([Missing("other")], replaced.Diagnostics);
        Assert.Same(replaced.Diagnostics[0], replaced.Diagnostic);
        Assert.Empty(cleared.Diagnostics);
        Assert.Null(cleared.Diagnostic);
        Assert.Equal(Missing("third"), relisted.Diagnostic);
        Assert.Equal(["first", "second"], availability.Diagnostics.Select(static item => item.Code));
        Assert.Throws<ArgumentException>(() => availability with { Diagnostics = [null!] });

        // The list is a read-only view; the record's storage cannot be reached through it.
        Assert.IsNotType<DesktopDiagnostic[]>(availability.Diagnostics);
        Assert.Throws<NotSupportedException>(() => ((IList<DesktopDiagnostic>)availability.Diagnostics)[0] = Missing("changed"));
        Assert.Equal("first", availability.Diagnostic!.Code);
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

    private sealed class UnavailableFactory(IReadOnlyList<DesktopDiagnostic> diagnostics) : IDesktopWindowHostFactory
    {
        public bool IsSupported => false;
        public IDesktopWindowHost Create() => throw new NotSupportedException();
        public IReadOnlyList<DesktopDiagnostic> GetAvailabilityDiagnostics() => diagnostics;
    }
}
