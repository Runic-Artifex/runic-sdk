using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// DesktopWindow.StartDragAsync through a custom window host (IDesktopDragSourceWindowHost): the capability, the shared
// data rules, the effect reduced to one the drag allowed, and the Windows data the OLE drag offers.
public sealed class FileDragTests
{
    [Test]
    public async Task AWindowHostStartsTheDragWithCheckedData()
    {
        var file = Path.GetTempFileName();
        try
        {
            var factory = new DragFactory(DesktopWindowCapabilities.FileDrag);
            await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
            await using var surface = await host.CreateSurfaceAsync();
            await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
            Assert.True(window.Capabilities.HasFlag(DesktopWindowCapabilities.FileDrag));
            var data = new DesktopDragData { Paths = [file], AllowedEffects = DesktopDragEffects.Copy | DesktopDragEffects.Move };
            factory.Hosts[0].Result = DesktopDragEffects.Move;
            Assert.Equal(DesktopDragEffects.Move, await window.StartDragAsync(data));
            Assert.Same(data, Assert.Single(factory.Hosts[0].Started));
            // A host that reports more than one effect, or one the drag did not allow, is reduced to what was allowed.
            factory.Hosts[0].Result = DesktopDragEffects.Copy | DesktopDragEffects.Move | DesktopDragEffects.Link;
            Assert.Equal(DesktopDragEffects.Move, await window.StartDragAsync(data));
            factory.Hosts[0].Result = DesktopDragEffects.Link;
            Assert.Equal(DesktopDragEffects.None, await window.StartDragAsync(data));
            factory.Hosts[0].Result = DesktopDragEffects.None;
            Assert.Equal(DesktopDragEffects.None, await window.StartDragAsync(new DesktopDragData { Text = "late" }));
            // The data rules apply before the host sees anything.
            await Assert.ThrowsAsync<ArgumentException>(async () => await window.StartDragAsync(new DesktopDragData()));
            await Assert.ThrowsAsync<ArgumentException>(async () => await window.StartDragAsync(new DesktopDragData { Text = "x", AllowedEffects = DesktopDragEffects.Move }));
            Assert.Equal(4, factory.Hosts[0].Started.Count);
            await window.CloseAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await window.StartDragAsync(new DesktopDragData { Text = "x" }));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public async Task AWindowWithoutTheCapabilityRefusesDrags()
    {
        var factory = new DragFactory(DesktopWindowCapabilities.None);
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var error = await Assert.ThrowsAsync<DesktopException>(async () => await window.StartDragAsync(new DesktopDragData { Text = "x" }));
        Assert.Equal("window-capability-unavailable", error.Code);
        Assert.Empty(factory.Hosts[0].Started);
    }

    [Test]
    public void TheOleDragOffersTextAndEffects()
    {
        Assert.Equal(1u, WindowsDragSource.ToDropEffect(DesktopDragEffects.Copy));
        Assert.Equal(7u, WindowsDragSource.ToDropEffect(DesktopDragEffects.Copy | DesktopDragEffects.Move | DesktopDragEffects.Link));
        Assert.Equal(DesktopDragEffects.Move, WindowsDragSource.FromDropEffect(2));
        // DROPEFFECT_SCROLL (0x80000000) is not an effect.
        Assert.Equal(DesktopDragEffects.Copy, WindowsDragSource.FromDropEffect(0x80000001));
        Assert.Equal("note", WindowsDragSource.PlainText(new DesktopDragData { Text = "note", Uris = [new Uri("https://example.com/")] }));
        Assert.Equal("https://example.com/a\r\nmailto:ann@example.com",
            WindowsDragSource.PlainText(new DesktopDragData { Uris = [new Uri("https://example.com/a"), new Uri("mailto:ann@example.com")] }));
        Assert.Null(WindowsDragSource.PlainText(new DesktopDragData { Paths = [Path.GetTempPath()] }));
    }

    private sealed class DragFactory(DesktopWindowCapabilities extra) : IDesktopWindowHostFactory
    {
        internal List<DragHost> Hosts { get; } = [];
        public bool IsSupported => true;
        public IDesktopWindowHost Create() { var host = new DragHost(extra); Hosts.Add(host); return host; }
    }

    private sealed class DragHost(DesktopWindowCapabilities extra) : IDesktopDragSourceWindowHost
    {
        internal List<DesktopDragData> Started { get; } = [];
        internal DesktopDragEffects Result { get; set; }
        public event EventHandler? Closed;
        public DesktopWindowCapabilities Capabilities => DesktopWindowCapabilities.NativeHandle | extra;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? 1 : 0;
        public ValueTask<DesktopDragEffects> StartDragAsync(DesktopDragData data, CancellationToken cancellationToken = default)
        {
            Started.Add(data);
            return ValueTask.FromResult(Result);
        }
        public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
        { IsOpen = true; return ValueTask.CompletedTask; }
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
