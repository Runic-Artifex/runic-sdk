using Runic.Desktop.Internal;
using TUnit.Core;

namespace Runic.Desktop.Tests;

public sealed class FileDropSupportTests
{
    [Test]
    public void UriListKeepsLocalFilesOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal([@"C:\Users\Ann\notes.txt"], FileDropSupport.ParseUriList("file:///C:/Users/Ann/notes.txt\r\n"));
            return;
        }
        var paths = FileDropSupport.ParseUriList(
            "# dragged from a file manager\r\n" +
            "file:///home/ann/My%20Notes/plan.txt\r\n" +
            "file://localhost/home/ann/photo.jpg\n" +
            "https://example.com/remote.txt\n" +
            "file://otherhost/share/file.txt\n" +
            "not a uri\n" +
            "file:///home/ann/My%20Notes/plan.txt\n" +
            "\n");
        Assert.Equal(["/home/ann/My Notes/plan.txt", "/home/ann/photo.jpg"], paths);
        Assert.Empty(FileDropSupport.ParseUriList(null));
        Assert.Empty(FileDropSupport.ParseUriList("http://a/b\n"));
    }

    [Test]
    public void LabelMessagesDecode()
    {
        Assert.Equal("folder-7", FileDropSupport.DecodeLabel("1folder-7"));
        Assert.Equal("", FileDropSupport.DecodeLabel("1"));
        Assert.Null(FileDropSupport.DecodeLabel("0"));
        Assert.Null(FileDropSupport.DecodeLabel(null));
        Assert.Null(FileDropSupport.DecodeLabel(""));
    }

    [Test]
    public void WebView2MessagesNeedTheToken()
    {
        const string token = "0123abcd";
        Assert.Equal("folder-7", FileDropMessage.Parse("""{"runicFileDrop":"0123abcd","target":"folder-7"}""", token)!.Target);
        Assert.Null(FileDropMessage.Parse("""{"runicFileDrop":"0123abcd","target":null}""", token)!.Target);
        Assert.Null(FileDropMessage.Parse("""{"runicFileDrop":"0123abcd"}""", token)!.Target);
        Assert.Null(FileDropMessage.Parse("""{"runicFileDrop":"forged","target":"x"}""", token));
        Assert.Null(FileDropMessage.Parse("""{"target":"x"}""", token));
        Assert.Null(FileDropMessage.Parse("""["runicFileDrop"]""", token));
        Assert.Null(FileDropMessage.Parse("not json", token));
        Assert.Null(FileDropMessage.Parse(null, token));
        Assert.Null(FileDropMessage.Parse("{\"runicFileDrop\":\"0123abcd\",\"pad\":\"" + new string('x', 70_000) + "\"}", token));
    }

    [Test]
    public void WebView2ScriptCarriesItsTokenAndTopFrameCheck()
    {
        var script = FileDropSupport.WebView2Script("feedface");
        Assert.Contains("runicFileDrop: \"feedface\"", script);
        Assert.Contains("globalThis.top !== globalThis", script);
        Assert.Contains("postMessageWithAdditionalObjects", script);
        Assert.Contains("data-runic-drop-target", FileDropSupport.WebKitScript);
        Assert.Contains("messageHandlers?.runicFileDrop", FileDropSupport.WebKitScript);
    }

    [Test]
    public void DropEventArgumentsRequireAbsolutePaths()
    {
        var root = Path.GetTempPath();
        var drop = new DesktopFileDropEventArgs([Path.Combine(root, "a.txt")], "x");
        Assert.Equal("x", drop.Target);
        Assert.Throws<ArgumentException>(() => new DesktopFileDropEventArgs([]));
        Assert.Throws<ArgumentException>(() => new DesktopFileDropEventArgs(["relative.txt"]));
        Assert.Throws<ArgumentException>(() => new DesktopFileDropEventArgs([""]));
    }
}

public sealed class FileDropPairingTests
{
    private static readonly string File1 = Path.Combine(Path.GetTempPath(), "one.txt");
    private static readonly string File2 = Path.Combine(Path.GetTempPath(), "two.txt");

    [Test]
    public void TheLabelJoinsThePendingDrop()
    {
        var delivered = new List<DesktopFileDropEventArgs>();
        using var pairing = new FileDropPairing(delivered.Add, TimeSpan.FromSeconds(30));
        pairing.NativeDrop([File1]);
        Assert.Empty(delivered);
        pairing.PageReport("folder-7");
        var drop = Assert.Single(delivered);
        Assert.Equal([File1], drop.Paths);
        Assert.Equal("folder-7", drop.Target);
    }

    [Test]
    public void APageReportWithoutANativeDropIsIgnored()
    {
        var delivered = new List<DesktopFileDropEventArgs>();
        using var pairing = new FileDropPairing(delivered.Add, TimeSpan.FromSeconds(30));
        pairing.PageReport("forged");
        Assert.Empty(delivered);
        pairing.NativeDrop([File1]);
        pairing.PageReport(null);
        Assert.Null(Assert.Single(delivered).Target);
    }

    [Test]
    public async Task ADropWithoutALabelIsDeliveredAfterTheWaitInOrder()
    {
        var delivered = new List<DesktopFileDropEventArgs>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pairing = new FileDropPairing(drop =>
        {
            lock (delivered)
            {
                delivered.Add(drop);
                if (delivered.Count == 2) done.TrySetResult();
            }
        }, TimeSpan.FromMilliseconds(50));
        pairing.NativeDrop([File1]);
        pairing.NativeDrop([File2]);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (delivered)
        {
            Assert.Equal([File1], delivered[0].Paths);
            Assert.Equal([File2], delivered[1].Paths);
            Assert.All(delivered, static drop => Assert.Null(drop.Target));
        }
        // A late label finds nothing to join.
        pairing.PageReport("late");
        Assert.Equal(2, delivered.Count);
    }

    [Test]
    public async Task DisposalDropsPendingDrops()
    {
        var delivered = 0;
        var pairing = new FileDropPairing(_ => Interlocked.Increment(ref delivered), TimeSpan.FromMilliseconds(20));
        pairing.NativeDrop([File1]);
        pairing.Dispose();
        await Task.Delay(100);
        pairing.NativeDrop([File1]);
        pairing.PageReport("x");
        Assert.Equal(0, Volatile.Read(ref delivered));
    }
}

public sealed class WindowFileDropTests
{
    private static readonly string Dropped = Path.Combine(Path.GetTempPath(), "dropped.txt");

    [Test]
    public async Task DropsReachTheWindowInOrderOnThePoolAndSurviveAThrowingHandler()
    {
        var factory = new DropFactory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        Assert.True(window.Capabilities.HasFlag(DesktopWindowCapabilities.FileDrop));
        var seen = new List<(string? Target, bool Pool)>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.FilesDropped += (_, _) => throw new InvalidOperationException("handler failure");
        window.FilesDropped += (sender, drop) =>
        {
            Assert.Same(window, sender);
            lock (seen)
            {
                seen.Add((drop.Target, Thread.CurrentThread.IsThreadPoolThread));
                if (seen.Count == 20) done.TrySetResult();
            }
        };
        for (var index = 0; index < 20; index++) factory.Hosts[0].Drop(new([Dropped], index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (seen)
        {
            Assert.Equal(Enumerable.Range(0, 20).Select(static index => index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                seen.Select(static item => item.Target));
            Assert.All(seen, static item => Assert.True(item.Pool));
        }
    }

    [Test]
    public async Task AClosedWindowDoesNotReceiveItsReplacementsDrops()
    {
        var factory = new DropFactory();
        await using var host = await DesktopHost.StartAsync(new() { WindowHostFactory = factory, WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync();
        var first = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var firstDrops = 0;
        first.FilesDropped += (_, _) => Interlocked.Increment(ref firstDrops);
        await first.CloseAsync();
        await using var second = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.FilesDropped += (_, _) => received.TrySetResult();
        factory.Hosts[0].Drop(new([Dropped]));
        factory.Hosts[1].Drop(new([Dropped]));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref firstDrops));
    }

    private sealed class DropFactory : IDesktopWindowHostFactory
    {
        internal List<DropHost> Hosts { get; } = [];
        public bool IsSupported => true;
        public IDesktopWindowHost Create() { var host = new DropHost(); Hosts.Add(host); return host; }
    }

    private sealed class DropHost : IDesktopFileDropWindowHost
    {
        public event EventHandler? Closed;
        public event EventHandler<DesktopFileDropEventArgs>? FilesDropped;
        public DesktopWindowCapabilities Capabilities => DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.FileDrop;
        public bool IsOpen { get; private set; }
        public nint NativeHandle => IsOpen ? 1 : 0;
        internal void Drop(DesktopFileDropEventArgs drop) => FilesDropped?.Invoke(this, drop);
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
        public ValueTask ResizeAsync(uint width, uint height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }
}
