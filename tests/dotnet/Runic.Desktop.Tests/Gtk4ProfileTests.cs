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
        if (!OperatingSystem.IsLinux()) return;
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions().WithGtk4());

        var embedded = Assert.Single(host.GetAvailability().Presentations, static item => item.Browser == BrowserKind.Embedded);

        string[] expected = ExpectedMissingGtk4Libraries();
        if (expected.Length == 0)
        {
            Assert.DoesNotContain(embedded.Diagnostics, static item => item.Code.EndsWith("-runtime-missing", StringComparison.Ordinal));
            return;
        }
        Assert.False(embedded.IsAvailable);
        Assert.Equal(expected, embedded.Diagnostics.Select(static item => item.Code).Where(static code => code.EndsWith("-runtime-missing", StringComparison.Ordinal)));
        Assert.Same(embedded.Diagnostics[0], embedded.Diagnostic);
    }

    [Fact]
    public async Task Gtk4SelectionWithoutItsProviderReportsTheProviderAndEachMissingPrerequisite()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var host = await DesktopHost.StartAsync(new() { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk4WebKit6 } });

        var validation = host.Validate(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
        var codes = validation.Errors.Select(static item => item.Code).ToArray();

        Assert.Equal("gtk4-provider-missing", codes[0]);
        Assert.Contains("WithGtk4()", validation.Errors[0].Remediation, StringComparison.Ordinal);
        foreach (var library in ExpectedMissingGtk4Libraries())
        {
            Assert.Contains(library, codes);
        }
        Assert.Equal(codes.Length, codes.Distinct().Count());
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

    private static string[] ExpectedMissingGtk4Libraries() =>
    [
        .. LinuxDesktopRuntime.IsLibraryAvailable("libgtk-4.so.1") ? Array.Empty<string>() : ["gtk4-runtime-missing"],
        .. LinuxDesktopRuntime.IsLibraryAvailable("libwebkitgtk-6.0.so.4") || LinuxDesktopRuntime.IsLibraryAvailable("libwebkitgtk-6.0.so.0")
            ? Array.Empty<string>()
            : ["webkitgtk6-runtime-missing"],
    ];

    private sealed class UnavailableFactory(IReadOnlyList<DesktopDiagnostic> diagnostics) : IDesktopWindowHostFactory
    {
        public bool IsSupported => false;
        public IDesktopWindowHost Create() => throw new NotSupportedException();
        public IReadOnlyList<DesktopDiagnostic> GetAvailabilityDiagnostics() => diagnostics;
    }
}
