using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// DesktopWindow.SetLayoutDirectionAsync and DesktopHost.SetStandardMenuTitles (S3) through a custom window host; the
// native hosts are covered by the GTK 4 right-to-left smoke and the WebView smoke.
public sealed class LayoutDirectionTests
{
    [Test]
    public async Task TheWindowChangesItsDirectionThroughItsHostAndReportsIt()
    {
        var factory = new DirectionFactory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new()
        {
            Browser = BrowserKind.Embedded, LayoutDirection = DesktopLayoutDirection.LeftToRight,
        });
        Assert.True(window.Capabilities.HasFlag(DesktopWindowCapabilities.LayoutDirection));
        Assert.Equal(DesktopLayoutDirection.LeftToRight, window.LayoutDirection);
        Assert.Equal(DesktopLayoutDirection.LeftToRight, factory.Hosts[0].Options!.LayoutDirection);

        await window.SetLayoutDirectionAsync(DesktopLayoutDirection.RightToLeft);
        Assert.Equal(DesktopLayoutDirection.RightToLeft, window.LayoutDirection);
        // The same direction again does not reach the host.
        await window.SetLayoutDirectionAsync(DesktopLayoutDirection.RightToLeft);
        await window.SetLayoutDirectionAsync(DesktopLayoutDirection.Default);
        Assert.Equal([DesktopLayoutDirection.RightToLeft, DesktopLayoutDirection.Default], factory.Hosts[0].Directions);

        factory.Hosts[0].Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await window.SetLayoutDirectionAsync(DesktopLayoutDirection.RightToLeft));
        Assert.Equal(DesktopLayoutDirection.Default, window.LayoutDirection);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await window.SetLayoutDirectionAsync((DesktopLayoutDirection)42));

        await window.CloseAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await window.SetLayoutDirectionAsync(DesktopLayoutDirection.RightToLeft));
    }

    [Test]
    public async Task AHostWithoutTheCapabilityRefusesTheChange()
    {
        var factory = new DirectionFactory { WithoutDirection = true };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        Assert.False(window.Capabilities.HasFlag(DesktopWindowCapabilities.LayoutDirection));
        var error = await Assert.ThrowsAsync<DesktopException>(async () => await window.SetLayoutDirectionAsync(DesktopLayoutDirection.RightToLeft));
        Assert.Equal("window-capability-unavailable", error.Code);
        Assert.Equal(DesktopLayoutDirection.Default, window.LayoutDirection);
    }

    [Test]
    public async Task StandardMenuTitlesAreCheckedWhenSetAtRunTime()
    {
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = new DirectionFactory(), WaitForConnection = false });
        host.SetStandardMenuTitles(new DesktopStandardMenuTitles { Edit = "Bearbeiten", Quit = "{0} beenden" });
        host.SetStandardMenuTitles(null);
        Assert.Throws<ArgumentException>(() => host.SetStandardMenuTitles(new DesktopStandardMenuTitles { Edit = " " }));
        Assert.Throws<ArgumentException>(() => host.SetStandardMenuTitles(new DesktopStandardMenuTitles { Copy = "{0} kopieren" }));
    }

    private sealed class DirectionFactory : IDesktopWindowHostFactory
    {
        internal List<DirectionHost> Hosts { get; } = [];
        internal bool WithoutDirection { get; init; }
        public bool IsSupported => true;
        public IDesktopWindowHost Create()
        {
            var host = new DirectionHost { WithoutDirection = WithoutDirection };
            Hosts.Add(host);
            return host;
        }
    }

    private sealed class DirectionHost : IDesktopLayoutDirectionWindowHost
    {
        private readonly List<DesktopLayoutDirection> _directions = [];
        public event EventHandler? Closed;
        internal bool WithoutDirection { get; init; }
        internal bool Fail { get; set; }
        public DesktopWindowCapabilities Capabilities => DesktopWindowCapabilities.NativeHandle |
            (WithoutDirection ? DesktopWindowCapabilities.None : DesktopWindowCapabilities.LayoutDirection);
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? 1 : 0;
        internal DesktopWindowHostOptions? Options;
        internal IReadOnlyList<DesktopLayoutDirection> Directions { get { lock (_directions) return [.. _directions]; } }

        public ValueTask SetLayoutDirectionAsync(DesktopLayoutDirection layoutDirection, CancellationToken cancellationToken = default)
        {
            if (Fail) return ValueTask.FromException(new InvalidOperationException("The window refused the direction."));
            lock (_directions) _directions.Add(layoutDirection);
            return ValueTask.CompletedTask;
        }

        public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
        { Options = options; IsOpen = true; return ValueTask.CompletedTask; }
        public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            if (IsOpen) { IsOpen = false; Closed?.Invoke(this, EventArgs.Empty); }
            return ValueTask.CompletedTask;
        }
        public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }
}
