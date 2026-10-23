using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// The pure parts of GTK 3 and GTK 4 drag and drop (B11): GdkDragAction values, drop effects, the offered data and the
// frames whose drop reports count. The drags themselves run in the GTK smokes under Xvfb.
public sealed class GtkDragTests
{
    private const DesktopDragEffects All = DesktopDragEffects.Copy | DesktopDragEffects.Move | DesktopDragEffects.Link;

    [Test]
    public void ActionsMapToEachToolkitsGdkDragAction()
    {
        // GTK 3: DEFAULT 1, COPY 2, MOVE 4, LINK 8, PRIVATE 16, ASK 32. GTK 4: COPY 1, MOVE 2, LINK 4, ASK 8.
        Assert.Equal(2, GtkDragSupport.ToGtk3Actions(DesktopDragEffects.Copy));
        Assert.Equal(2 | 4 | 8, GtkDragSupport.ToGtk3Actions(All));
        Assert.Equal(1 | 2, GtkDragSupport.ToGtk4Actions(DesktopDragEffects.Copy | DesktopDragEffects.Move));
        Assert.Equal(4, GtkDragSupport.ToGtk4Actions(DesktopDragEffects.Link));
        Assert.Equal(DesktopDragEffects.Move, GtkDragSupport.FromGtk3Actions(4));
        Assert.Equal(DesktopDragEffects.Copy | DesktopDragEffects.Link, GtkDragSupport.FromGtk3Actions(1 | 2 | 8 | 16 | 32));
        Assert.Equal(DesktopDragEffects.None, GtkDragSupport.FromGtk3Actions(1 | 16 | 32));
        Assert.Equal(DesktopDragEffects.Link, GtkDragSupport.FromGtk4Actions(4 | 8));
        Assert.Equal(DesktopDragEffects.None, GtkDragSupport.FromGtk4Actions(8));
        foreach (var effects in new[] { DesktopDragEffects.Copy, DesktopDragEffects.Move, DesktopDragEffects.Link, All })
        {
            Assert.Equal(effects, GtkDragSupport.FromGtk3Actions(GtkDragSupport.ToGtk3Actions(effects)));
            Assert.Equal(effects, GtkDragSupport.FromGtk4Actions(GtkDragSupport.ToGtk4Actions(effects)));
        }
    }

    [Test]
    public void ADropPerformsOneEffect()
    {
        // GTK 3 reports the action WebKit chose: Move before Copy before Link, Copy when none.
        Assert.Equal(DesktopDragEffects.Move, GtkDragSupport.DropEffect(DesktopDragEffects.Move));
        Assert.Equal(DesktopDragEffects.Move, GtkDragSupport.DropEffect(All));
        Assert.Equal(DesktopDragEffects.Copy, GtkDragSupport.DropEffect(DesktopDragEffects.Copy | DesktopDragEffects.Link));
        Assert.Equal(DesktopDragEffects.Link, GtkDragSupport.DropEffect(DesktopDragEffects.Link));
        Assert.Equal(DesktopDragEffects.Copy, GtkDragSupport.DropEffect(DesktopDragEffects.None));
        // GTK 4 reports the actions the drop offers: the only one, or Copy among several, as WebKit's default.
        Assert.Equal(DesktopDragEffects.Move, GtkDragSupport.OfferedDropEffect(DesktopDragEffects.Move));
        Assert.Equal(DesktopDragEffects.Copy, GtkDragSupport.OfferedDropEffect(All));
        Assert.Equal(DesktopDragEffects.Move, GtkDragSupport.OfferedDropEffect(DesktopDragEffects.Move | DesktopDragEffects.Link));
        Assert.Equal(DesktopDragEffects.Copy, GtkDragSupport.OfferedDropEffect(DesktopDragEffects.None));
    }

    [Test]
    public void ThePageChoosesAmongTheEffectsADropOffers()
    {
        var file = Path.Combine(Path.GetTempPath(), "offered.txt");
        var delivered = new List<DesktopFileDropEventArgs>();
        using var pairing = new FileDropPairing(delivered.Add, TimeSpan.FromSeconds(30));
        // GTK 4: Copy and Move offered, the page moved.
        var offered = DesktopDragEffects.Copy | DesktopDragEffects.Move;
        pairing.NativeDrop([file], [], GtkDragSupport.OfferedDropEffect(offered), offered);
        pairing.PageReport("folder", reported: DesktopDragEffects.Move);
        Assert.Equal(DesktopDragEffects.Move, delivered[^1].Effect);
        // An effect the drop does not offer leaves the native one.
        pairing.NativeDrop([file], [], GtkDragSupport.OfferedDropEffect(offered), offered);
        pairing.PageReport("folder", reported: DesktopDragEffects.Link);
        Assert.Equal(DesktopDragEffects.Copy, delivered[^1].Effect);
        // A native side that knows the negotiated effect (GTK 3, macOS) keeps it whatever the page reports.
        pairing.NativeDrop([file], [], DesktopDragEffects.Copy);
        pairing.PageReport("folder", reported: DesktopDragEffects.Move);
        Assert.Equal(DesktopDragEffects.Copy, delivered[^1].Effect);
        // A refused report from a frame of another origin delivers nothing.
        pairing.NativeDrop([file], [], DesktopDragEffects.Copy, offered);
        pairing.PageReport("other", refused: true, reported: DesktopDragEffects.Move);
        Assert.Equal(3, delivered.Count);
    }

    [Test]
    public void OnlyTheTopDocumentAndFramesOfThePresentedOriginReportDrops()
    {
        var presented = new Uri("http://127.0.0.1:43210/index.html");
        Assert.True(GtkDragSupport.AcceptsReport(presented, null));
        Assert.True(GtkDragSupport.AcceptsReport(null, null));
        Assert.True(GtkDragSupport.AcceptsReport(presented, "http://127.0.0.1:43210"));
        Assert.False(GtkDragSupport.AcceptsReport(presented, "http://127.0.0.1:43211"));
        Assert.False(GtkDragSupport.AcceptsReport(presented, "https://127.0.0.1:43210"));
        Assert.False(GtkDragSupport.AcceptsReport(presented, "http://localhost:43210"));
        // An opaque origin (data:, sandboxed frames) reports "null".
        Assert.False(GtkDragSupport.AcceptsReport(presented, "null"));
        Assert.False(GtkDragSupport.AcceptsReport(presented, ""));
        Assert.False(GtkDragSupport.AcceptsReport(null, "http://127.0.0.1:43210"));
        // The WebKit script's subframe report, as FileDropSupport decodes it.
        var report = FileDropSupport.DecodeReport("""{"label":"frame","effect":"move","origin":"http://127.0.0.1:43210"}""");
        Assert.True(GtkDragSupport.AcceptsReport(presented, report.Origin));
        Assert.Equal(DesktopDragEffects.Move, report.Effect);
    }

    [Test]
    public void TheDragOffersFileUrisUrisAndText()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Directory.CreateTempSubdirectory("runic-gtk-drag-");
        try
        {
            var file = Path.Combine(root.FullName, "notes #1 100% ü.txt");
            File.WriteAllText(file, "x");
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "a folder?")).FullName;
            var link = new Uri("https://example.com/a?b=c#d");
            var data = new DesktopDragData { Paths = [file, folder], Uris = [link] };

            var files = GtkDragSupport.FileUris(data);
            Assert.Equal(2, files.Count);
            Assert.All(files, static uri => Assert.StartsWith("file:///", uri, StringComparison.Ordinal));
            // Escaped as g_filename_to_uri does, and read back to the same paths by the drop side.
            Assert.Contains("notes%20%231%20100%25%20%C3%BC.txt", files[0], StringComparison.Ordinal);
            Assert.EndsWith("/a%20folder%3F", files[1], StringComparison.Ordinal);
            Assert.Equal([file, folder], files.Select(static uri => FileDropSupport.ToLocalPath(uri)));
            Assert.Equal([file, folder], FileDropSupport.ParseUriList(string.Join("\r\n", files)));
            Assert.Equal([.. files, link.AbsoluteUri], GtkDragSupport.UriList(data));
            // Paths have no plain text; text wins over URIs, which are otherwise one per line.
            Assert.Null(GtkDragSupport.PlainText(new DesktopDragData { Paths = [file] }));
            Assert.Equal(link.AbsoluteUri, GtkDragSupport.PlainText(data));
            Assert.Equal("https://example.com/a\nhttps://example.com/b",
                GtkDragSupport.PlainText(new DesktopDragData { Uris = [new Uri("https://example.com/a"), new Uri("https://example.com/b")] }));
            Assert.Equal("hello", GtkDragSupport.PlainText(new DesktopDragData { Text = "hello", Uris = [link] }));
            Assert.Empty(GtkDragSupport.UriList(new DesktopDragData { Text = "hello" }));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    public void TheGtkWindowsAdvertiseDragsOut()
    {
        Assert.True(LinuxWebKitGtkHost.KnownCapabilities.HasFlag(DesktopWindowCapabilities.FileDrag));
        // GTK 4 reports FileDrag from GTK 4.22 only; its smoke checks the window.
    }
}
