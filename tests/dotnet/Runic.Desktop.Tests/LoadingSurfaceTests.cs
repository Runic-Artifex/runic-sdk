using TUnit.Core;
using System.Collections.Concurrent;
using Runic.Desktop.Internal;
using Runic.Platform;

namespace Runic.Desktop.Tests;

// D053.27 (H2): the WebView2 loading surface's colors and layout, and a window closed while it opens. The native
// window itself runs in the WebView2 smoke (WindowsLoadingSurfaceSmoke).
public sealed class LoadingSurfaceTests
{
    [Test]
    public void TheGracePeriodKeepsWarmStartsWithoutTheSurface()
    {
        // Warm WebView2 starts took about 0.4 s and cold ones 2 to 6 s on runners (sdk#784).
        Assert.InRange(WindowsLoadingSurface.GracePeriod, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        Assert.Equal(WindowsLoadingSurface.GracePeriod, WindowsWebView2Host.LoadingSurfaceDelay);
        Assert.Equal("Loading…", WindowsLoadingSurface.Title);
    }

    [Test]
    public void ThePaletteFollowsTheWindowsColorSchemeThenTheSystem()
    {
        static uint System(int index) => (uint)(0x010000 * index);
        var light = WindowsLoadingSurface.PaletteFor(DesktopColorScheme.Light, systemDark: true, highContrast: false, System);
        var dark = WindowsLoadingSurface.PaletteFor(DesktopColorScheme.Dark, systemDark: false, highContrast: false, System);
        Assert.Equal(0xFFFFFFu, light.Background);
        Assert.Equal(WindowsLoadingSurface.Rgb(0x20, 0x20, 0x20), dark.Background);
        Assert.Equal(0x202020u, dark.Background);
        Assert.Equal(dark, WindowsLoadingSurface.PaletteFor(DesktopColorScheme.NoPreference, systemDark: true, highContrast: false, System));
        Assert.Equal(light, WindowsLoadingSurface.PaletteFor(DesktopColorScheme.NoPreference, systemDark: false, highContrast: false, System));
        // High contrast takes COLOR_WINDOW, COLOR_GRAYTEXT and COLOR_HIGHLIGHT whatever the scheme.
        var contrast = WindowsLoadingSurface.PaletteFor(DesktopColorScheme.Dark, systemDark: true, highContrast: true, System);
        Assert.Equal(new WindowsLoadingSurface.Palette(System(5), System(17), System(13)), contrast);
        Assert.Equal(0x0000FFu, WindowsLoadingSurface.Rgb(0xFF, 0, 0));
    }

    [Test]
    public void TheBarIsCenteredAndScalesWithTheDpi()
    {
        var bar = WindowsLoadingSurface.Layout(800, 600, 96, phase: null);
        Assert.Equal(new WindowsLoadingSurface.Bar(300, 298, 200, 4, 365, 435), bar);
        var scaled = WindowsLoadingSurface.Layout(1600, 1200, 192, phase: null);
        Assert.Equal(4 * 2, scaled.Height);
        Assert.Equal(400, scaled.Width);
        // Clamped to 96 and 240 device-independent pixels, and inside a 16-pixel margin of a narrow window.
        Assert.Equal(96, WindowsLoadingSurface.Layout(200, 100, 96, null).Width);
        Assert.Equal(240, WindowsLoadingSurface.Layout(4000, 100, 96, null).Width);
        Assert.Equal(48, WindowsLoadingSurface.Layout(80, 100, 96, null).Width);
        Assert.Equal(0, WindowsLoadingSurface.Layout(0, 0, 96, null).Width);
    }

    [Test]
    public void TheSegmentSweepsAcrossTheTrackWithinIt()
    {
        var start = WindowsLoadingSurface.Layout(800, 600, 96, phase: 0);
        Assert.Equal(start.Left, start.SegmentLeft);
        Assert.Equal(start.Left, start.SegmentRight);
        var middle = WindowsLoadingSurface.Layout(800, 600, 96, phase: 0.5);
        Assert.True(middle.SegmentLeft > middle.Left && middle.SegmentRight < middle.Right);
        Assert.Equal(70, middle.SegmentRight - middle.SegmentLeft);
        var end = WindowsLoadingSurface.Layout(800, 600, 96, phase: 0.999);
        Assert.Equal(end.Right, end.SegmentRight);
        Assert.Equal(WindowsLoadingSurface.Layout(800, 600, 96, phase: 0.25), WindowsLoadingSurface.Layout(800, 600, 96, phase: 3.25));
        for (var phase = 0d; phase < 1; phase += 0.01)
        {
            var bar = WindowsLoadingSurface.Layout(640, 480, 120, phase);
            Assert.True(bar.Left <= bar.SegmentLeft && bar.SegmentLeft <= bar.SegmentRight && bar.SegmentRight <= bar.Right);
        }
    }

    // A user who closes a window that is still opening has closed it: no fallback and no failure (D053.27).
    [Test]
    public async Task AWindowClosedWhileOpeningIsReturnedClosedWithoutAFallback()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Directory.CreateTempSubdirectory("runic-desktop-closed-opening-");
        try
        {
            // An installed browser the fallback would launch.
            var executable = Path.Combine(folder.FullName, OperatingSystem.IsMacOS()
                ? "Google Chrome.app/Contents/MacOS/Google Chrome"
                : "google-chrome");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
            var factory = new ClosedWhileOpeningHostFactory();
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                BrowserFolder = folder.FullName,
                WindowHostFactory = factory,
                DiagnosticSink = diagnostics.Enqueue,
                WaitForConnection = false,
            });
            await using var surface = await host.CreateSurfaceAsync();
            var asked = 0;
            var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Chrome,
                PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
                ConfirmCloseAsync = _ => { Interlocked.Increment(ref asked); return ValueTask.FromResult(false); },
            }).AsTask().WaitScaledAsync(TimeSpan.FromSeconds(10));

            Assert.False(window.IsOpen);
            Assert.False(window.IsFallback);
            Assert.Equal(BrowserKind.Embedded, window.Browser);
            Assert.Equal(0, asked);
            Assert.DoesNotContain(diagnostics, static item => item.Code is "presentation-start-failed" or "embedded-presentation-fallback");
            Assert.True(factory.Host!.Disposed);
            await window.WaitForCloseAsync().AsTask().WaitScaledAsync(TimeSpan.FromSeconds(5));

            // The surface opens its next window as usual.
            factory.CloseWhileOpening = false;
            await using var next = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded })
                .AsTask().WaitScaledAsync(TimeSpan.FromSeconds(10));
            Assert.True(next.IsOpen);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private sealed class ClosedWhileOpeningHostFactory : IDesktopWindowHostFactory
    {
        internal bool CloseWhileOpening { get; set; } = true;
        internal ClosedWhileOpeningHost? Host { get; private set; }
        public bool IsSupported => true;
        public IDesktopWindowHost Create() => Host = new ClosedWhileOpeningHost(CloseWhileOpening);
    }

    // Opens as the WebView2 host does when its loading window is closed: Closed, then the open fails.
    private sealed class ClosedWhileOpeningHost(bool closeWhileOpening) : IDesktopWindowHost
    {
        private int _open;
        public event EventHandler? Closed;
        public bool IsOpen => Volatile.Read(ref _open) != 0;
        public bool Disposed { get; private set; }
        public bool SupportsCloseConfirmation => true;
        public nint NativeHandle => IsOpen ? 1 : 0;

        public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
        {
            if (!closeWhileOpening)
            {
                Volatile.Write(ref _open, 1);
                return ValueTask.CompletedTask;
            }
            Closed?.Invoke(this, EventArgs.Empty);
            return ValueTask.FromException(new WindowClosedWhileOpeningException());
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _open, 0) != 0) Closed?.Invoke(this, EventArgs.Empty);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Volatile.Write(ref _open, 0);
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
