using Runic.Desktop.Internal;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// The routes of attached region frames in a host window (RegionFrames; host-protocol.md, Features in a region): a frame
// of an attached region's guest origin is routed to that guest, every other frame of another origin to nobody.
public sealed class RegionFramesTests
{
    private static RegionFrameRoute Route(string frameUrl, DesktopPermissionGrant permissions = DesktopPermissionGrant.None,
        List<string>? log = null, string name = "") =>
        new(new Uri(frameUrl), permissions, drop => log?.Add($"{name} drop {string.Join(",", drop.Paths)}"),
            hostPage => log?.Add($"{name} crashed {(hostPage ? "page" : "frame")}"));

    [Test]
    public void AFrameIsRoutedByItsGuestsOriginAndSurfacePath()
    {
        var frames = new RegionFrames();
        Assert.True(frames.IsEmpty);
        Assert.Null(frames.Find("http://[::1]:40117/w1/index.html"));
        var first = Route("http://[::1]:40117/w1/_runic_frame/" + new string('a', 43));
        var second = Route("http://[::1]:40117/w2/_runic_frame/" + new string('b', 43));
        var other = Route("http://127.0.0.1:40200/_runic_frame/" + new string('c', 43));
        Assert.Equal("http://[::1]:40117", first.Origin);
        Assert.Equal("/w1/", first.PathBase);
        Assert.Equal("/", other.PathBase);
        using var a = frames.Add(first);
        using var b = frames.Add(second);
        var c = frames.Add(other);

        Assert.Same(first, frames.Find("http://[::1]:40117/w1/index.html#ignored"));
        Assert.Same(second, frames.Find("http://[::1]:40117/w2/sub/page.html?x=1"));
        Assert.Same(other, frames.Find("http://127.0.0.1:40200/index.html"));
        // An origin alone, as some engines report it, or a path under no surface: the guest's latest route.
        Assert.Same(second, frames.Find("http://[::1]:40117"));
        Assert.Same(second, frames.Find("http://[::1]:40117/elsewhere"));
        // Another port, scheme or host is another origin.
        Assert.Null(frames.Find("http://[::1]:40118/w1/index.html"));
        Assert.Null(frames.Find("https://[::1]:40117/w1/index.html"));
        Assert.Null(frames.Find("http://localhost:40117/w1/index.html"));
        Assert.Null(frames.Find("null"));
        Assert.Null(frames.Find(null));
        Assert.Null(frames.Find("about:blank"));

        // A detach removes the route, once.
        c.Dispose();
        c.Dispose();
        Assert.Null(frames.Find("http://127.0.0.1:40200/index.html"));
    }

    [Test]
    public void AnEndedProcessCrashesTheRoutesItRendered()
    {
        var log = new List<string>();
        var frames = new RegionFrames();
        using var a = frames.Add(Route("http://[::1]:40117/w1/_runic_frame/x", log: log, name: "a"));
        using var b = frames.Add(Route("http://[::1]:40300/_runic_frame/y", log: log, name: "b"));
        frames.FrameProcessEnded(["http://[::1]:40300/index.html", "http://[::1]:40300/index.html", "http://127.0.0.1:1/x", null]);
        Assert.Equal(["b crashed frame"], log);
        log.Clear();
        frames.HostPageProcessEnded();
        Assert.Equal(["a crashed page", "b crashed page"], log);
    }

    [Test]
    public void ARegionsMediaCaptureFollowsTheGuestsGrantNotTheWindows()
    {
        var presented = new Uri("http://127.0.0.1:38200/index.html");
        var granted = Route("http://[::1]:40117/w1/_runic_frame/x", DesktopPermissionGrant.MediaCapture);
        var denied = Route("http://[::1]:40117/w1/_runic_frame/x");
        Assert.True(granted.AllowsMediaCapture);
        Assert.Equal(MacOsWebContent.PermissionGrant,
            MacOsWebContent.ForRegionMediaCapture(granted, presented, "http://[::1]:40117", "http://[::1]:40117", presented.AbsoluteUri, 0));
        Assert.Equal(MacOsWebContent.PermissionDeny,
            MacOsWebContent.ForRegionMediaCapture(denied, presented, "http://[::1]:40117", "http://[::1]:40117", presented.AbsoluteUri, 1));
        // The request must come from the region's frame, in the presented page.
        Assert.Equal(MacOsWebContent.PermissionDeny,
            MacOsWebContent.ForRegionMediaCapture(granted, presented, "http://[::1]:40118", "http://[::1]:40117", presented.AbsoluteUri, 0));
        Assert.Equal(MacOsWebContent.PermissionDeny,
            MacOsWebContent.ForRegionMediaCapture(granted, presented, "http://[::1]:40117", "http://[::1]:40117", "http://127.0.0.1:1/", 0));
        // Screen capture and other types are not part of the grant.
        Assert.Equal(MacOsWebContent.PermissionDeny,
            MacOsWebContent.ForRegionMediaCapture(granted, presented, "http://[::1]:40117", "http://[::1]:40117", presented.AbsoluteUri, 3));
    }

    [Test]
    public async Task APageReportCanHandTheNativeDropToARegionsGuest()
    {
        var window = new List<DesktopFileDropEventArgs>();
        var guest = new TaskCompletionSource<DesktopFileDropEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pairing = new FileDropPairing(window.Add, TimeSpan.FromSeconds(30));
        pairing.NativeDrop(["/tmp/a.txt"]);
        pairing.PageReport("target", deliverTo: guest.SetResult);
        var drop = await guest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["/tmp/a.txt"], drop.Paths);
        Assert.Equal("target", drop.Target);
        Assert.Empty(window);
    }

    [Test]
    public void TheWebKitReportNamesTheSubframesAddress()
    {
        Assert.Contains("location?.pathname", FileDropSupport.WebKitScript);
        var report = FileDropSupport.DecodeReport("""{"label":"x","effect":"copy","origin":"http://[::1]:40117","path":"/w1/index.html"}""");
        Assert.Equal("http://[::1]:40117/w1/index.html", report.FrameAddress);
        Assert.Null(FileDropSupport.DecodeReport("""{"label":"x","origin":null}""").FrameAddress);
        Assert.Equal("http://[::1]:40117/", FileDropSupport.DecodeReport("""{"origin":"http://[::1]:40117","path":"x"}""").FrameAddress);
    }
}
