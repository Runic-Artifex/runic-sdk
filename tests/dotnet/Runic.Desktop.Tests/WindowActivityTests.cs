using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// DesktopWindow.Activity and ActivityChanged (B1), and DesktopWindow and DesktopHost.SetColorSchemeAsync (B2), through a
// custom window host; the native hosts are covered by the WebView and GTK 4 smoke tests.
public sealed class WindowActivityTests
{
    private static readonly DesktopWindowActivity Active = new() { IsActive = true, IsVisible = true };
    private static readonly DesktopWindowActivity Inactive = new() { IsVisible = true };
    private static readonly DesktopWindowActivity Minimized = new() { IsMinimized = true };

    [Test]
    public async Task ActivityIsReadAtOpenRaisedOnThePoolAndReportsTheClose()
    {
        var factory = new ActivityFactory { InitialActivity = Active };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        Assert.Equal(Active, window.Activity);
        Assert.True(window.Capabilities.HasFlag(DesktopWindowCapabilities.Activity));

        var changes = new List<(DesktopWindowActivity Activity, bool PoolThread)>();
        var minimized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ActivityChanged += (sender, args) =>
        {
            Assert.Same(window, sender);
            lock (changes) changes.Add((args.Activity, Thread.CurrentThread.IsThreadPoolThread));
            if (args.Activity == Minimized) minimized.TrySetResult();
            if (args.Activity is { IsVisible: false, IsMinimized: false }) closed.TrySetResult();
        };
        factory.Hosts[0].Report(Minimized);
        await minimized.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Minimized, window.Activity);

        await window.CloseAsync();
        await closed.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new DesktopWindowActivity(), window.Activity);
        lock (changes) Assert.True(changes.All(static change => change.PoolThread));
        // Nothing is raised after the close.
        factory.Hosts[0].Report(Active);
        await Task.Delay(100);
        Assert.Equal(new DesktopWindowActivity(), window.Activity);
    }

    [Test]
    public async Task AWindowTheUserClosedReportsItselfInactiveAndHidden()
    {
        var factory = new ActivityFactory { InitialActivity = Active };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var closed = new TaskCompletionSource<DesktopWindowActivity>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ActivityChanged += (_, args) => closed.TrySetResult(args.Activity);
        await factory.Hosts[0].CloseAsync();
        var activity = await closed.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));
        Assert.False(activity.IsActive);
        Assert.False(activity.IsVisible);
    }

    [Test]
    public async Task AHostWithoutActivityReportsNone()
    {
        var factory = new ActivityFactory { WithoutActivity = true };
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        Assert.Null(window.Activity);
        Assert.False(window.Capabilities.HasFlag(DesktopWindowCapabilities.Activity));
        Assert.False(window.Capabilities.HasFlag(DesktopWindowCapabilities.ColorScheme));
        await Assert.ThrowsAsync<DesktopException>(async () => await window.SetColorSchemeAsync(DesktopColorScheme.Dark));
        // The application's scheme skips a window that cannot change it.
        await host.SetColorSchemeAsync(DesktopColorScheme.Dark);
    }

    [Test]
    public async Task TheApplicationSchemeReachesWindowsWithoutTheirOwn()
    {
        var factory = new ActivityFactory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var plain = await host.CreateSurfaceAsync();
        await using var own = await host.CreateSurfaceAsync();
        var follows = await plain.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var keeps = await own.OpenWindowAsync(new() { Browser = BrowserKind.Embedded, ColorScheme = DesktopColorScheme.Light });
        Assert.Equal(DesktopColorScheme.NoPreference, follows.ColorScheme);
        Assert.Equal(DesktopColorScheme.Light, keeps.ColorScheme);
        Assert.Equal(DesktopColorScheme.NoPreference, host.ColorScheme);

        await host.SetColorSchemeAsync(DesktopColorScheme.Dark);
        Assert.Equal(DesktopColorScheme.Dark, host.ColorScheme);
        Assert.Equal([DesktopColorScheme.Dark], factory.Hosts[0].Schemes);
        Assert.Equal([DesktopColorScheme.Light], factory.Hosts[1].Schemes);

        // A window's own scheme wins until it returns to NoPreference, which follows the application again.
        await follows.SetColorSchemeAsync(DesktopColorScheme.Light);
        Assert.Equal(DesktopColorScheme.Light, follows.ColorScheme);
        await follows.SetColorSchemeAsync(DesktopColorScheme.NoPreference);
        Assert.Equal([DesktopColorScheme.Dark, DesktopColorScheme.Light, DesktopColorScheme.Dark], factory.Hosts[0].Schemes);

        await host.SetColorSchemeAsync(DesktopColorScheme.NoPreference);
        Assert.Equal(DesktopColorScheme.NoPreference, factory.Hosts[0].Schemes[^1]);
        Assert.Equal(DesktopColorScheme.Light, factory.Hosts[1].Schemes[^1]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await host.SetColorSchemeAsync((DesktopColorScheme)42));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await follows.SetColorSchemeAsync((DesktopColorScheme)42));
    }

    [Test]
    public async Task AWindowOpenedLaterOpensWithTheApplicationScheme()
    {
        var factory = new ActivityFactory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await host.SetColorSchemeAsync(DesktopColorScheme.Dark);
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        Assert.Equal(DesktopColorScheme.Dark, factory.Hosts[0].Options!.ColorScheme);
        // A host whose scheme belongs to the process, as GTK's does, applies it once the window is open.
        await WaitUntilAsync(() => factory.Hosts[0].Schemes.Contains(DesktopColorScheme.Dark));
        // The window has no scheme of its own.
        Assert.Equal(DesktopColorScheme.NoPreference, window.ColorScheme);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            if (waited > 5000) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(10);
        }
    }

    private sealed class ActivityFactory : IDesktopWindowHostFactory
    {
        internal List<ActivityHost> Hosts { get; } = [];
        internal DesktopWindowActivity? InitialActivity { get; init; }
        internal bool WithoutActivity { get; init; }
        public bool IsSupported => true;
        public IDesktopWindowHost Create()
        {
            ActivityHost host = WithoutActivity ? new PlainHost() : new ActivityHost { Activity = InitialActivity };
            Hosts.Add(host);
            return host;
        }
    }

    private sealed class PlainHost : ActivityHost
    {
        public override DesktopWindowCapabilities Capabilities => DesktopWindowCapabilities.NativeHandle;
    }

    private class ActivityHost : IDesktopActivityWindowHost, IDesktopColorSchemeWindowHost
    {
        private readonly List<DesktopColorScheme> _schemes = [];
        public event EventHandler? Closed;
        public event EventHandler? ActivityChanged;
        public DesktopWindowActivity? Activity { get; set; }
        public virtual DesktopWindowCapabilities Capabilities =>
            DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.Activity | DesktopWindowCapabilities.ColorScheme;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? 1 : 0;
        internal DesktopWindowHostOptions? Options;
        internal IReadOnlyList<DesktopColorScheme> Schemes { get { lock (_schemes) return [.. _schemes]; } }

        internal void Report(DesktopWindowActivity activity)
        {
            Activity = activity;
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default)
        {
            lock (_schemes) _schemes.Add(colorScheme);
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
