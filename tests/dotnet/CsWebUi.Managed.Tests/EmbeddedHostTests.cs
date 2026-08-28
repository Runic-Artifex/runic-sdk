using CsWebUi.Managed;

namespace CsWebUi.Managed.Tests;

public sealed class EmbeddedHostTests
{
    [Fact]
    public async Task CustomHostReceivesWindowOptionsAndParticipatesInLifecycle()
    {
        var factory = new RecordingHostFactory();
        WebUiApplication.SetEmbeddedHostFactory(factory);
        WebUiApplication.SetConfiguration(WebUiConfiguration.ShowWaitConnection, false);
        var icon = Path.GetTempFileName();
        try
        {
            Assert.True(WebUiApplication.EmbeddedWebViewExists);
            Assert.True(WebUiApplication.BrowserExists(WebUiBrowser.WebView));
            await using var window = new WebUiWindow();
            window.SetSize(900, 700);
            window.SetMinimumSize(400, 300);
            window.SetPosition(12, 34);
            window.SetResizable(false);
            window.SetFrameless(true);
            window.SetTransparent(true);
            window.SetHidden(true);
            window.SetKiosk(true);
            window.SetHighContrast(true);
            window.SetIconFile(icon);
            window.SetCustomParameters("--custom=value");

            var firstUrl = await window.ShowWebViewAsync("first");
            var host = Assert.IsType<RecordingHost>(factory.Host);
            Assert.Equal(firstUrl, host.Url);
            Assert.Equal((nint)42, window.NativeWindowHandle);
            Assert.Equal(WebUiBrowser.WebView, window.CurrentBrowser);
            Assert.True(window.IsShown);
            Assert.Equal(900u, host.Options!.Width);
            Assert.Equal(700u, host.Options.Height);
            Assert.Equal(400u, host.Options.MinimumWidth);
            Assert.Equal(300u, host.Options.MinimumHeight);
            Assert.Equal(12u, host.Options.X);
            Assert.Equal(34u, host.Options.Y);
            Assert.False(host.Options.Resizable);
            Assert.True(host.Options.Frameless);
            Assert.True(host.Options.Transparent);
            Assert.True(host.Options.Hidden);
            Assert.True(host.Options.Kiosk);
            Assert.True(host.Options.HighContrast);
            Assert.Equal(Path.GetFullPath(icon), host.Options.IconFile);
            Assert.Equal("--custom=value", host.Options.CustomParameters);

            await window.ShowWebViewAsync("second");
            Assert.Equal(1, host.NavigateCount);
            await window.FocusAsync();
            await window.MinimizeAsync();
            await window.MaximizeAsync();
            Assert.Equal(1, host.FocusCount);
            Assert.Equal(1, host.MinimizeCount);
            Assert.Equal(1, host.MaximizeCount);

            window.SetSize(910, 710);
            window.SetPosition(21, 43);
            window.SetHidden(false);
            await host.BeginMoveAsync();
            Assert.Equal((910u, 710u), host.LiveSize);
            Assert.Equal((21u, 43u), host.LivePosition);
            Assert.True(host.Visible);
            Assert.Equal(1, host.BeginMoveCount);

            host.CloseFromPlatform();
            await WebUiApplication.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(window.Url);
            Assert.Equal(WebUiBrowser.NoBrowser, window.CurrentBrowser);
        }
        finally
        {
            File.Delete(icon);
            WebUiApplication.SetConfiguration(WebUiConfiguration.ShowWaitConnection, true);
            WebUiApplication.SetEmbeddedHostFactory(null);
        }
    }

    [Fact]
    public async Task RunsRealLinuxWebKitGtkHostWhenDisplayIsAvailable()
    {
        if (!OperatingSystem.IsLinux()
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return;
        }
        WebUiApplication.SetEmbeddedHostFactory(null);
        if (!WebUiApplication.EmbeddedWebViewExists)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var window = new WebUiWindow();
        window.SetSize(640, 480);
        window.SetMinimumSize(320, 240);
        window.SetHidden(true);
        var url = await window.ShowWebViewAsync(Page("embedded"), timeout.Token);
        Assert.NotNull(url);
        Assert.Equal(WebUiBrowser.WebView, window.CurrentBrowser);
        Assert.NotEqual(0, window.NativeWindowHandle);
        Assert.True(window.IsShown);
        Assert.Equal("embedded", await window.ExecuteJavaScriptAsync(
            "return document.title;",
            TimeSpan.FromSeconds(5),
            cancellationToken: timeout.Token));

        await window.FocusAsync(timeout.Token);
        await window.MinimizeAsync(timeout.Token);
        await window.MaximizeAsync(timeout.Token);
        await window.CloseAsync(timeout.Token);
        Assert.False(window.IsShown);
        Assert.Equal(0, window.NativeWindowHandle);
    }

    private static string Page(string title) => $$"""
        <!doctype html><html><head><script src="webui.js"></script><title>{{title}}</title></head><body>{{title}}</body></html>
        """;

    private sealed class RecordingHostFactory : IWebUiEmbeddedHostFactory
    {
        internal IWebUiEmbeddedHost? Host { get; private set; }

        public bool IsSupported => true;

        public IWebUiEmbeddedHost Create() => Host = new RecordingHost();
    }

    private sealed class RecordingHost : IWebUiEmbeddedHost
    {
        public event EventHandler? Closed;

        public bool IsOpen { get; private set; }

        public nint NativeHandle => IsOpen ? 42 : 0;

        internal Uri? Url { get; private set; }

        internal WebUiEmbeddedHostOptions? Options { get; private set; }

        internal int NavigateCount { get; private set; }

        internal int FocusCount { get; private set; }

        internal int MinimizeCount { get; private set; }

        internal int MaximizeCount { get; private set; }

        internal int BeginMoveCount { get; private set; }

        internal (uint Width, uint Height)? LiveSize { get; private set; }

        internal (uint X, uint Y)? LivePosition { get; private set; }

        internal bool? Visible { get; private set; }

        public ValueTask ShowAsync(Uri url, WebUiEmbeddedHostOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Url = url;
            Options = options;
            IsOpen = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Url = url;
            NavigateCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseFromPlatform();
            return ValueTask.CompletedTask;
        }

        public ValueTask FocusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FocusCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask MinimizeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MinimizeCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask MaximizeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaximizeCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetSizeAsync(uint width, uint height, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LiveSize = (width, height);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetPositionAsync(uint x, uint y, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LivePosition = (x, y);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Visible = visible;
            return ValueTask.CompletedTask;
        }

        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginMoveCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            return ValueTask.CompletedTask;
        }

        internal void CloseFromPlatform()
        {
            if (!IsOpen)
            {
                return;
            }
            IsOpen = false;
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }
}
