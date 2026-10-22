using TUnit.Core;

namespace Runic.Desktop.Tests;

public sealed class WindowGenerationTests
{
    [Test]
    public async Task ClosedWindowCannotMutateItsReplacement()
    {
        var factory = new Factory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var first = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        await first.CloseAsync();
        await using var second = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var replacement = factory.Hosts[1];
        Assert.Equal(DesktopWindowCapabilities.None, first.Capabilities);
        Assert.Equal(0, first.ProcessId);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.FocusAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.MinimizeAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.ToggleMaximizedAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.ResizeAsync(20, 30).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.MoveAsync(20, 30).AsTask());
        Assert.Equal(0, replacement.Mutations);
        await second.FocusAsync();
        Assert.Equal(1, replacement.Mutations);
    }

    [Test]
    public async Task ResizeRejectsANonPositiveSizeBeforeTheWindowHost()
    {
        var factory = new Factory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => window.ResizeAsync(0, 30).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => window.ResizeAsync(20, -30).AsTask());
        Assert.Equal(0, factory.Hosts[0].Mutations);
    }

    [Test]
    public async Task CloseAndReplacementWaitForAdmittedMutationAndRejectQueuedStaleWork()
    {
        var factory = new Factory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var first = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var original = factory.Hosts[0];
        original.ReleaseMutation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var resize = first.ResizeAsync(20, 30).AsTask();
        await original.MutationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = first.MoveAsync(40, 50).AsTask();
        var close = first.CloseAsync().AsTask();
        var replacement = surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded }).AsTask();
        try
        {
            Assert.False(close.IsCompleted);
            Assert.False(replacement.IsCompleted);
            Assert.Single(factory.Hosts);
        }
        finally { original.ReleaseMutation.TrySetResult(); }
        await resize.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        await close.WaitAsync(TimeSpan.FromSeconds(5));
        await using var second = await replacement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, original.Mutations);
        Assert.Equal(0, factory.Hosts[1].Mutations);
    }

    [Test]
    public async Task ReplacementUsesCompleteOptionsInsteadOfPreviousOptionalValues()
    {
        var factory = new Factory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        string icon = Path.GetTempFileName();
        try
        {
            await using var first = await surface.OpenWindowAsync(new()
            {
                Browser = BrowserKind.Embedded, MinimumWidth = 20, MinimumHeight = 30,
                Centered = true, HighContrast = true, IconFile = icon,
                ProfileName = "custom", ProfilePath = Path.GetDirectoryName(icon), BrowserArguments = "--custom-option", ProxyServer = "http://localhost:8888",
            });
            await first.CloseAsync();
            await using var second = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
            var options = factory.Hosts[1].Options!;
            Assert.Null(options.MinimumWidth);
            Assert.Null(options.MinimumHeight);
            Assert.Null(options.X);
            Assert.Null(options.Y);
            Assert.False(options.Centered);
            Assert.False(options.HighContrast);
            Assert.Null(options.IconFile);
            Assert.Null(options.BrowserArguments);
            Assert.NotEqual(factory.Hosts[0].Options!.ProfilePath, options.ProfilePath);
        }
        finally { File.Delete(icon); }
    }

    [Test]
    public async Task SurfaceDisposalDrainsAdmittedMutationAndRejectsQueuedAdmission()
    {
        var factory = new Factory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var native = factory.Hosts[0];
        native.ReleaseMutation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var resize = window.ResizeAsync(20, 30).AsTask();
        await native.MutationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = window.FocusAsync().AsTask();
        var open = surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded }).AsTask();
        var dispose = surface.DisposeAsync().AsTask();
        try
        {
            Assert.False(dispose.IsCompleted);
            Assert.True(native.IsOpen);
        }
        finally { native.ReleaseMutation.TrySetResult(); }
        await resize.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => open);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(native.IsOpen);
        Assert.Equal(1, native.Mutations);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => window.MoveAsync(1, 2).AsTask());
    }
    // A surface created with its own window host factory, as RunicViewHost does on a shared DesktopHost, opens its
    // embedded window and checks availability through that factory; the host's factory serves only its other surfaces.
    [Test]
    public async Task ASurfaceWithItsOwnWindowHostFactoryOpensThroughIt()
    {
        var hostFactory = new Factory();
        var surfaceFactory = new Factory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = hostFactory, WaitForConnection = false });
        await using var own = await host.CreateSurfaceAsync(null, surfaceFactory, CancellationToken.None);
        await using var window = await own.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        Assert.Single(surfaceFactory.Hosts);
        Assert.Empty(hostFactory.Hosts);
        Assert.True(surfaceFactory.Hosts[0].IsOpen);

        await using var shared = await host.CreateSurfaceAsync();
        await using var other = await shared.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        Assert.Single(hostFactory.Hosts);
        Assert.Single(surfaceFactory.Hosts);
        Assert.Equal(own.Url.Port, shared.Url.Port);

        await own.DisposeAsync();
        Assert.False(surfaceFactory.Hosts[0].IsOpen);
        Assert.True(hostFactory.Hosts[0].IsOpen);
        Assert.True(other.IsOpen);
    }

    [Test]
    public async Task ASurfaceWithAnUnsupportedWindowHostFactoryReportsTheEmbeddedWindowUnavailable()
    {
        var hostFactory = new Factory();
        var surfaceFactory = new Factory { Supported = false };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = hostFactory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync(null, surfaceFactory, CancellationToken.None);
        await Assert.ThrowsAsync<DesktopException>(() => surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded }).AsTask());
        Assert.Empty(surfaceFactory.Hosts);
        Assert.Empty(hostFactory.Hosts);
        await Assert.ThrowsAsync<ArgumentNullException>(() => host.CreateSurfaceAsync(null, null!, CancellationToken.None).AsTask());
    }

    private sealed class Factory : IDesktopWindowHostFactory
    {
        internal List<RecordingHost> Hosts { get; } = [];
        internal bool Supported { get; init; } = true;
        public bool IsSupported => Supported;
        public IDesktopWindowHost Create() { var host = new RecordingHost(); Hosts.Add(host); return host; }
    }

    private sealed class RecordingHost : IDesktopWindowHost
    {
        public event EventHandler? Closed;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? 1 : 0;
        internal DesktopWindowHostOptions? Options;
        internal int Mutations;
        internal TaskCompletionSource MutationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource? ReleaseMutation;
        public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
        { Options = options; IsOpen = true; return ValueTask.CompletedTask; }
        public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        { IsOpen = false; Closed?.Invoke(this, EventArgs.Empty); return ValueTask.CompletedTask; }
        private async ValueTask MutateAsync()
        {
            MutationEntered.TrySetResult();
            if (ReleaseMutation is { } release) await release.Task;
            Mutations++;
        }
        public ValueTask FocusAsync(CancellationToken cancellationToken = default) => MutateAsync();
        public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => MutateAsync();
        public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => MutateAsync();
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => MutateAsync();
        public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => MutateAsync();
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }
}
