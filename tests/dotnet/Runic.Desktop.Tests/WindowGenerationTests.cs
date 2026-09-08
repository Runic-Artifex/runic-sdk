namespace Runic.Desktop.Tests;

public sealed class WindowGenerationTests
{
    [Fact]
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
        Assert.Equal(0UL, first.ProcessId);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.FocusAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.MinimizeAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.ToggleMaximizedAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.ResizeAsync(20, 30).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.MoveAsync(20, 30).AsTask());
        Assert.Equal(0, replacement.Mutations);
        await second.FocusAsync();
        Assert.Equal(1, replacement.Mutations);
    }

    [Fact]
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

    [Fact]
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
            Assert.Null(options.CustomArguments);
            Assert.NotEqual(factory.Hosts[0].Options!.ProfilePath, options.ProfilePath);
        }
        finally { File.Delete(icon); }
    }

    [Fact]
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
        var queued = window.FocusAsync();
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

    private sealed class Factory : IDesktopWindowHostFactory
    {
        internal List<RecordingHost> Hosts { get; } = [];
        public bool IsSupported => true;
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
        public ValueTask ResizeAsync(uint width, uint height, CancellationToken cancellationToken = default) => MutateAsync();
        public ValueTask MoveAsync(uint x, uint y, CancellationToken cancellationToken = default) => MutateAsync();
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }
}
