using Runic.Desktop.Internal;
using Runic.Platform;
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
    public void ReportMessagesDecode()
    {
        Assert.Equal("folder-7", FileDropSupport.DecodeLabel("""{"label":"folder-7","effect":"copy","origin":null}"""));
        Assert.Equal("", FileDropSupport.DecodeLabel("""{"label":""}"""));
        Assert.Null(FileDropSupport.DecodeLabel("""{"label":null}"""));
        Assert.Null(FileDropSupport.DecodeLabel(null));
        Assert.Null(FileDropSupport.DecodeLabel(""));
        Assert.Null(FileDropSupport.DecodeLabel("not json"));
        Assert.Null(FileDropSupport.DecodeLabel("""["label"]"""));
        var frame = FileDropSupport.DecodeReport("""{"label":"x","effect":"move","origin":"https://editor.example"}""");
        Assert.Equal(new FileDropReport("x", DesktopDragEffects.Move, "https://editor.example"), frame);
        Assert.Equal(DesktopDragEffects.Link, FileDropSupport.DecodeReport("""{"effect":"link"}""").Effect);
        Assert.Equal(DesktopDragEffects.Copy, FileDropSupport.DecodeReport("""{"effect":"none"}""").Effect);
        Assert.Equal(DesktopDragEffects.Copy, FileDropSupport.DecodeReport("""{"effect":7}""").Effect);
        Assert.Null(FileDropSupport.DecodeReport("""{"label":"x"}""").Origin);
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
        Assert.Equal(DesktopDragEffects.Move, FileDropMessage.Parse("""{"runicFileDrop":"0123abcd","effect":"move"}""", token)!.Effect);
        Assert.Equal(DesktopDragEffects.Link, FileDropMessage.Parse("""{"runicFileDrop":"0123abcd","effect":"link"}""", token)!.Effect);
        Assert.Equal(DesktopDragEffects.Copy, FileDropMessage.Parse("""{"runicFileDrop":"0123abcd","effect":"none"}""", token)!.Effect);
        Assert.Equal(DesktopDragEffects.Copy, FileDropMessage.Parse("""{"runicFileDrop":"0123abcd"}""", token)!.Effect);
    }

    [Test]
    public void ScriptsRunInEveryFrameAndReadTheDropEffect()
    {
        var script = FileDropSupport.WebView2Script("feedface");
        Assert.Contains("runicFileDrop: \"feedface\"", script);
        Assert.Contains("postMessageWithAdditionalObjects", script);
        // Both scripts run in subframes too; the native side checks the frame's origin.
        Assert.DoesNotContain("globalThis.top !== globalThis", script);
        Assert.DoesNotContain("globalThis.top !== globalThis", FileDropSupport.WebKitScript);
        Assert.Contains("globalThis.top === globalThis ? null : String(globalThis.location?.origin", FileDropSupport.WebKitScript);
        foreach (var text in (string[])[script, FileDropSupport.WebKitScript])
        {
            Assert.Contains("data-runic-drop-target", text);
            Assert.Contains("[data-runic-drop-effect]", text);
            Assert.Contains("event.dataTransfer.dropEffect = effect", text);
            // "none" refuses the drop without navigating: it is cancelled and never reported.
            Assert.Contains("if (effect === \"none\"", text);
            Assert.Contains("\"dragenter\", allow, true", text);
            Assert.Contains("\"dragover\", allow, true", text);
        }
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
        Assert.Empty(drop.PromisedFiles);
        Assert.Equal(DesktopDragEffects.Copy, drop.Effect);
        // A literal null label still binds to the paths-only constructor.
        Assert.Null(new DesktopFileDropEventArgs([Path.Combine(root, "a.txt")], null).Target);
    }

    [Test]
    public void PromisedFilesMayStandAlone()
    {
        var lease = new TestLease();
        var drop = new DesktopFileDropEventArgs([], [lease], "inbox") { Effect = DesktopDragEffects.Move };
        Assert.Empty(drop.Paths);
        Assert.Same(lease, Assert.Single(drop.PromisedFiles));
        Assert.Equal("inbox", drop.Target);
        Assert.Equal(DesktopDragEffects.Move, drop.Effect);
        Assert.Throws<ArgumentException>(() => new DesktopFileDropEventArgs([], [], null));
        Assert.Throws<ArgumentException>(() => new DesktopFileDropEventArgs(["relative.txt"], [lease], null));
        Assert.Throws<ArgumentException>(() => new DesktopFileDropEventArgs([], [null!], null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopFileDropEventArgs([], [lease], null) { Effect = DesktopDragEffects.None });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DesktopFileDropEventArgs([], [lease], null) { Effect = DesktopDragEffects.Copy | DesktopDragEffects.Move });
    }

    [Test]
    public async Task AStagedLeaseIsReadOnceAndDeletesItsCopy()
    {
        var directory = Directory.CreateTempSubdirectory("runic-staged-").FullName;
        var path = Path.Combine(directory, "mail attachment.pdf");
        await File.WriteAllTextAsync(path, "content");
        var lease = new StagedFileLease(path, directory);
        Assert.Equal("mail attachment.pdf", lease.DisplayName);
        var stream = await lease.OpenReadAsync();
        using (var reader = new StreamReader(stream, leaveOpen: true)) Assert.Equal("content", await reader.ReadToEndAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.OpenReadAsync());
        await lease.DisposeAsync();
        Assert.False(Directory.Exists(directory));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await lease.OpenReadAsync());

        var unopened = Directory.CreateTempSubdirectory("runic-staged-").FullName;
        await File.WriteAllTextAsync(Path.Combine(unopened, "x"), "x");
        await new StagedFileLease(Path.Combine(unopened, "x"), unopened).DisposeAsync();
        Assert.False(Directory.Exists(unopened));
        Assert.Equal("_etc_passwd", StagedFileLease.SafeName("/etc/passwd"));
        Assert.Equal("file", StagedFileLease.SafeName(".."));
        Assert.Equal("file", StagedFileLease.SafeName(" "));
    }
}

internal sealed class TestLease : IReadFileLease
{
    internal int Disposals;
    public string DisplayName => "promised.txt";
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3]));
    public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
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
    public void TheNativeEffectAndPromisedFilesTravelWithTheDrop()
    {
        var delivered = new List<DesktopFileDropEventArgs>();
        using var pairing = new FileDropPairing(delivered.Add, TimeSpan.FromSeconds(30));
        var lease = new TestLease();
        pairing.NativeDrop([], [lease], DesktopDragEffects.Move);
        pairing.PageReport("inbox");
        var drop = Assert.Single(delivered);
        Assert.Empty(drop.Paths);
        Assert.Same(lease, Assert.Single(drop.PromisedFiles));
        Assert.Equal(DesktopDragEffects.Move, drop.Effect);
        Assert.Equal(0, lease.Disposals);
        // Nothing dropped is not a drop; an effect the drop cannot have is a copy.
        pairing.NativeDrop([], [], DesktopDragEffects.Copy);
        pairing.NativeDrop([File1], [], DesktopDragEffects.None);
        pairing.PageReport(null);
        Assert.Equal(2, delivered.Count);
        Assert.Equal(DesktopDragEffects.Copy, delivered[1].Effect);
    }

    [Test]
    public void AReportFromAFrameOfAnotherOriginDiscardsTheDrop()
    {
        var delivered = new List<DesktopFileDropEventArgs>();
        using var pairing = new FileDropPairing(delivered.Add, TimeSpan.FromSeconds(30));
        var lease = new TestLease();
        pairing.NativeDrop([File1], [lease], DesktopDragEffects.Copy);
        pairing.PageReport("third-party", refused: true);
        Assert.Empty(delivered);
        Assert.Equal(1, lease.Disposals);
        // The next drop is unaffected.
        pairing.NativeDrop([File2]);
        pairing.PageReport("mine");
        Assert.Equal("mine", Assert.Single(delivered).Target);
    }

    [Test]
    public async Task DisposalDropsPendingDrops()
    {
        var delivered = 0;
        var pairing = new FileDropPairing(_ => Interlocked.Increment(ref delivered), TimeSpan.FromMilliseconds(20));
        var lease = new TestLease();
        pairing.NativeDrop([File1], [lease], DesktopDragEffects.Copy);
        pairing.Dispose();
        Assert.Equal(1, lease.Disposals);
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
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }
}
