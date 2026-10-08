using Runic.Desktop.Gtk4;

namespace Runic.Desktop.Tests;

public sealed class DesktopEventLoopEntryPointTests
{
    [Fact]
    public void SupportedEventLoopFactoryRunsTheApplicationAndDisposesTheHost()
    {
        var factory = new LoopFactory { IsSupported = true };
        DesktopHost? started = null;

        int result = DesktopEventLoop.Run(new DesktopHostOptions { WindowHostFactory = factory }, host =>
        {
            started = host;
            Assert.True(factory.InsideLoop);
            return Task.FromResult(7);
        });

        Assert.Equal(7, result);
        Assert.Equal(1, factory.Runs);
        Assert.NotNull(started);
        Assert.Throws<ObjectDisposedException>(() => started!.Validate());
    }

    [Fact]
    public void UnsupportedEventLoopFactoryIsNotStartedAndReportsWhy()
    {
        // The AppKit loop needs the process main thread, which a test worker is not.
        if (OperatingSystem.IsMacOS()) return;
        var missing = new DesktopDiagnostic(DesktopErrorCategory.Unavailable, "loop-runtime-missing", "missing", false);
        var factory = new LoopFactory { IsSupported = false, Availability = [missing] };
        List<DesktopDiagnostic> reported = [];

        int result = DesktopEventLoop.Run(
            new DesktopHostOptions { WindowHostFactory = factory, DiagnosticSink = reported.Add },
            host => Task.FromResult(host.Validate(new() { Browser = BrowserKind.Embedded }).IsValid ? 1 : 3));

        Assert.Equal(3, result);
        Assert.Equal(0, factory.Runs);
        Assert.Equal(0, factory.Prepares);
        Assert.Equal(["event-loop-unavailable", "loop-runtime-missing"], reported.Select(static d => d.Code));
        Assert.All(reported, static d => Assert.Equal(DesktopDiagnosticSeverity.Warning, d.Severity));
        Assert.Contains("loop-runtime-missing", reported[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EventLoopThatCannotPrepareFallsBackToThePlainLoopAndReportsWhy()
    {
        if (OperatingSystem.IsMacOS()) return;
        var noDisplay = new DesktopDiagnostic(DesktopErrorCategory.Unavailable, "loop-display-unavailable", "no display", false);
        var factory = new LoopFactory { IsSupported = true, Preparation = [noDisplay] };
        List<DesktopDiagnostic> reported = [];
        DesktopHost? started = null;

        int result = DesktopEventLoop.Run(
            new DesktopHostOptions { WindowHostFactory = factory, DiagnosticSink = reported.Add },
            host =>
            {
                started = host;
                Assert.False(factory.InsideLoop);
                return Task.FromResult(4);
            });

        Assert.Equal(4, result);
        Assert.Equal(1, factory.Prepares);
        Assert.Equal(0, factory.Runs);
        Assert.Equal(["event-loop-unavailable", "loop-display-unavailable"], reported.Select(static d => d.Code));
        Assert.All(reported, static d => Assert.Equal(DesktopDiagnosticSeverity.Warning, d.Severity));
        Assert.Throws<ObjectDisposedException>(() => started!.Validate());
    }

    [Fact]
    public void NestedRunThrowsOnEveryPlatform()
    {
        var factory = new LoopFactory { IsSupported = true };
        InvalidOperationException? nested = null;

        int result = DesktopEventLoop.Run(new DesktopHostOptions { WindowHostFactory = factory }, _ =>
        {
            nested = Assert.Throws<InvalidOperationException>(() =>
                DesktopEventLoop.Run(new DesktopHostOptions(), static _ => Task.FromResult(0)));
            Assert.Throws<InvalidOperationException>(() => DesktopEventLoop.Run(static () => Task.CompletedTask));
            return Task.FromResult(1);
        });

        Assert.Equal(1, result);
        Assert.Contains("already running", nested!.Message, StringComparison.Ordinal);
        // The guard is released afterwards, so a later loop can run.
        Assert.Equal(2, DesktopEventLoop.Run(new DesktopHostOptions { WindowHostFactory = factory }, static _ => Task.FromResult(2)));
    }

    [Fact]
    public void FailingEventLoopReleasesTheHostAndTheGuard()
    {
        var factory = new LoopFactory { IsSupported = true, FailBeforeApplication = true };

        Assert.Throws<NotSupportedException>(() =>
            DesktopEventLoop.Run(new DesktopHostOptions { WindowHostFactory = factory }, static _ => Task.FromResult(0)));
        Assert.Equal(1, factory.Runs);
        Assert.Equal(5, DesktopEventLoop.Run(new DesktopHostOptions { WindowHostFactory = new LoopFactory { IsSupported = true } },
            static _ => Task.FromResult(5)));
    }

    [Fact]
    public void PlainLoopReturnsTheApplicationResultAndPropagatesFailures()
    {
        if (OperatingSystem.IsMacOS()) return;

        Assert.Equal(5, DesktopEventLoop.Run(new DesktopHostOptions(), static _ => Task.FromResult(5)));
        var error = Assert.Throws<InvalidOperationException>(() =>
            DesktopEventLoop.Run(new DesktopHostOptions(), static async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("application failed");
            }));
        Assert.Equal("application failed", error.Message);
    }

    [Fact]
    public void InvalidOptionsFailBeforeTheLoopStarts()
    {
        var factory = new LoopFactory { IsSupported = true };

        Assert.Throws<ArgumentNullException>(() => DesktopEventLoop.Run(null!, static _ => Task.FromResult(0)));
        Assert.Throws<ArgumentNullException>(() => DesktopEventLoop.Run(new DesktopHostOptions(), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DesktopEventLoop.Run(new DesktopHostOptions { WindowHostFactory = factory, Port = -1 }, static _ => Task.FromResult(0)));
        Assert.Equal(0, factory.Prepares);
        Assert.Equal(0, factory.Runs);
    }

    [Fact]
    public void Gtk4ProfileCarriesTheApplicationIdIntoItsEventLoop()
    {
        var options = new DesktopHostOptions().WithGtk4("org.example.App");

        Assert.Equal(LinuxEmbeddedBackend.Gtk4WebKit6, options.Linux.EmbeddedBackend);
        if (OperatingSystem.IsLinux())
        {
            var factory = Assert.IsType<Gtk4WindowHostFactory>(options.WindowHostFactory);
            Assert.Equal("org.example.App", factory.ApplicationId);
            Assert.IsAssignableFrom<IDesktopEventLoopWindowHostFactory>(factory);
        }
        else
        {
            Assert.Null(options.WindowHostFactory);
        }
        Assert.Throws<ArgumentException>(() => new DesktopHostOptions().WithGtk4(" "));
    }

    [Theory]
    [InlineData("App")]
    [InlineData("org..App")]
    [InlineData(".org.App")]
    [InlineData("org.App.")]
    [InlineData("org.1App")]
    [InlineData("org.example.my app")]
    [InlineData("org.example.Äpp")]
    public void Gtk4ProfileRejectsInvalidApplicationIdsOnEveryOs(string applicationId) =>
        Assert.Throws<ArgumentException>(() => new DesktopHostOptions().WithGtk4(applicationId));

    [Theory]
    [InlineData("org.example.App")]
    [InlineData("dev.runic.my-app_2")]
    public void Gtk4ProfileAcceptsReverseDnsApplicationIds(string applicationId) =>
        Assert.Equal(LinuxEmbeddedBackend.Gtk4WebKit6, new DesktopHostOptions().WithGtk4(applicationId).Linux.EmbeddedBackend);

    [Fact]
    public void Gtk4EventLoopOffTheMainThreadNamesTheDesktopEntryPoint()
    {
        if (OperatingSystem.IsLinux()) AssertOnLinux();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void AssertOnLinux()
    {
        // Test methods run on worker threads, never the process main thread.
        var error = Assert.Throws<InvalidOperationException>(() =>
            new Gtk4WindowHostFactory().RunEventLoop(static () => Task.FromResult(0)));
        Assert.Contains("DesktopEventLoop.Run(options, application)", error.Message, StringComparison.Ordinal);
        Assert.Empty(new Gtk4WindowHostFactory().PrepareEventLoop());
    }

    private sealed class LoopFactory : IDesktopEventLoopWindowHostFactory
    {
        public bool IsSupported { get; init; }
        public IReadOnlyList<DesktopDiagnostic> Availability { get; init; } = [];
        public IReadOnlyList<DesktopDiagnostic> Preparation { get; init; } = [];
        public bool FailBeforeApplication { get; init; }
        public int Runs { get; private set; }
        public int Prepares { get; private set; }
        public bool InsideLoop { get; private set; }

        public IDesktopWindowHost Create() => throw new NotSupportedException();

        public IReadOnlyList<DesktopDiagnostic> GetAvailabilityDiagnostics() => Availability;

        public IReadOnlyList<DesktopDiagnostic> PrepareEventLoop()
        {
            Prepares++;
            return Preparation;
        }

        public int RunEventLoop(Func<Task<int>> application)
        {
            Runs++;
            if (FailBeforeApplication) throw new NotSupportedException("The loop could not start.");
            InsideLoop = true;
            try
            {
                return application().GetAwaiter().GetResult();
            }
            finally
            {
                InsideLoop = false;
            }
        }
    }
}
