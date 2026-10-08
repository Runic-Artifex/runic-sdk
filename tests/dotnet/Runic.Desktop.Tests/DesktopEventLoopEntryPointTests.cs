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
    public void UnsupportedEventLoopFactoryIsNotStarted()
    {
        // The AppKit loop needs the process main thread, which a test worker is not.
        if (OperatingSystem.IsMacOS()) return;
        var factory = new LoopFactory { IsSupported = false };

        int result = DesktopEventLoop.Run(new DesktopHostOptions { WindowHostFactory = factory }, host =>
            Task.FromResult(host.Validate(new() { Browser = BrowserKind.Embedded }).IsValid ? 1 : 3));

        Assert.Equal(3, result);
        Assert.Equal(0, factory.Runs);
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

    private sealed class LoopFactory : IDesktopEventLoopWindowHostFactory
    {
        public bool IsSupported { get; init; }
        public int Runs { get; private set; }
        public bool InsideLoop { get; private set; }

        public IDesktopWindowHost Create() => throw new NotSupportedException();

        public int RunEventLoop(Func<Task<int>> application)
        {
            Runs++;
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
