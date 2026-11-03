using System.Xml.Linq;
using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// B5: the application's recent documents on Linux (recently-used.xbel, as GLib's GBookmarkFile reads and writes it),
// the Unavailable contract, and DesktopHost's methods forwarding to the shared instance. The tests replace the shared
// instance with one over a temporary file, so they never touch the user's recent documents.
[NotInParallel("RecentDocuments")]
public sealed class RecentDocumentsTests
{
    private static readonly XNamespace Bookmark = "http://www.freedesktop.org/standards/desktop-bookmarks";
    private static readonly XNamespace Mime = "http://www.freedesktop.org/standards/shared-mime-info";
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\Users\me\" : "/home/me/";

    private static string TemporaryFile() => Path.Combine(Path.GetTempPath(), "runic-xbel-" + Guid.NewGuid().ToString("N"), "recently-used.xbel");

    private static XElement[] Bookmarks(string file) => [.. XDocument.Load(file).Root!.Elements("bookmark")];

    private static string[] Applications(XElement bookmark) =>
        [.. bookmark.Descendants(Bookmark + "application").Select(static element => (string)element.Attribute("name")!)];

    [Test]
    public async Task AddWritesAnXbelBookmarkForThisApplication()
    {
        var file = TemporaryFile();
        try
        {
            var recent = new RecentDocuments(file);
            var path = Root + "Notes/plan #1 ü.md";
            Assert.True((await recent.AddAsync(path)).Succeeded);
            var bookmark = Assert.Single(Bookmarks(file));
            Assert.Equal(RecentDocuments.Xbel.FileUri(path), (string?)bookmark.Attribute("href"));
            Assert.NotNull(bookmark.Attribute("added"));
            Assert.Equal("text/markdown", (string?)bookmark.Descendants(Mime + "mime-type").Single().Attribute("type"));
            var application = bookmark.Descendants(Bookmark + "application").Single();
            Assert.Equal(RecentDocuments.Xbel.ApplicationName, (string?)application.Attribute("name"));
            Assert.Equal("1", (string?)application.Attribute("count"));
            Assert.EndsWith(" %u'", (string?)application.Attribute("exec"));

            // Again: the same bookmark, counted.
            Assert.True((await recent.AddAsync(path)).Succeeded);
            Assert.Equal("2", (string?)Assert.Single(Bookmarks(file)).Descendants(Bookmark + "application").Single().Attribute("count"));
            Assert.True((await recent.AddAsync(Root + "data.bin")).Succeeded);
            Assert.Equal(2, Bookmarks(file).Length);
            Assert.Equal("application/octet-stream", (string?)Bookmarks(file)[1].Descendants(Mime + "mime-type").Single().Attribute("type"));
            Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(file)!), static name => Path.GetFileName(name).StartsWith('.'));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Test]
    public async Task ClearRemovesOnlyThisApplication()
    {
        var file = TemporaryFile();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            var name = RecentDocuments.Xbel.ApplicationName;
            await File.WriteAllTextAsync(file, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <xbel version="1.0" xmlns:bookmark="{Bookmark.NamespaceName}" xmlns:mime="{Mime.NamespaceName}">
                  <bookmark href="file:///home/me/other.txt" added="2026-01-01T00:00:00.000000Z" modified="2026-01-01T00:00:00.000000Z" visited="2026-01-01T00:00:00.000000Z">
                    <info><metadata owner="http://freedesktop.org"><mime:mime-type type="text/plain"/>
                      <bookmark:applications><bookmark:application name="gedit" exec="'gedit %u'" modified="2026-01-01T00:00:00.000000Z" count="3"/></bookmark:applications>
                    </metadata></info>
                  </bookmark>
                  <bookmark href="file:///home/me/shared.txt" added="2026-01-01T00:00:00.000000Z" modified="2026-01-01T00:00:00.000000Z" visited="2026-01-01T00:00:00.000000Z">
                    <info><metadata owner="http://freedesktop.org"><mime:mime-type type="text/plain"/>
                      <bookmark:applications>
                        <bookmark:application name="gedit" exec="'gedit %u'" modified="2026-01-01T00:00:00.000000Z" count="1"/>
                        <bookmark:application name="{name}" exec="'{name} %u'" modified="2026-01-01T00:00:00.000000Z" count="1"/>
                      </bookmark:applications>
                    </metadata></info>
                  </bookmark>
                </xbel>
                """);
            var recent = new RecentDocuments(file);
            Assert.True((await recent.AddAsync(Root + "mine.txt")).Succeeded);
            Assert.Equal(3, Bookmarks(file).Length);
            Assert.True((await recent.ClearAsync()).Succeeded);
            var left = Bookmarks(file);
            Assert.Equal(["file:///home/me/other.txt", "file:///home/me/shared.txt"], left.Select(static bookmark => (string)bookmark.Attribute("href")!));
            Assert.Equal(["gedit"], Applications(left[0]));
            Assert.Equal(["gedit"], Applications(left[1]));
            Assert.Equal("3", (string?)left[0].Descendants(Bookmark + "application").Single().Attribute("count"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Test]
    public async Task AMissingOrBrokenFileStartsANewList()
    {
        var file = TemporaryFile();
        try
        {
            var recent = new RecentDocuments(file);
            Assert.True((await recent.ClearAsync()).Succeeded);
            Assert.False(File.Exists(file));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "not xml <");
            Assert.True((await recent.AddAsync(Root + "a.txt")).Succeeded);
            Assert.Single(Bookmarks(file));
            Assert.Equal("xbel", XDocument.Load(file).Root!.Name.LocalName);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Test]
    public void FileUrisAndTimestampsAreGLibs()
    {
        Assert.Equal("file:///home/me/a%20b%23c%25%C3%BC.txt", RecentDocuments.Xbel.FileUri("/home/me/a b#c%ü.txt"));
        Assert.Equal("file:///home/me/a,b@c=d+e.txt", RecentDocuments.Xbel.FileUri("/home/me/a,b@c=d+e.txt"));
        Assert.Equal("2026-11-03T10:20:30.123456Z",
            RecentDocuments.Xbel.Timestamp(new DateTime(2026, 11, 3, 10, 20, 30, DateTimeKind.Utc).AddTicks(1_234_560)));
    }

    [Test]
    public async Task UnavailableChecksArgumentsAndReportsTheReason()
    {
        var recent = DesktopRecentDocuments.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        Assert.Equal(new CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable), DesktopServiceStatus.Get(recent));
        Assert.Equal(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable), await recent.AddAsync(Root + "a.txt"));
        Assert.Equal(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable), await recent.ClearAsync());
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await recent.AddAsync(null!));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await recent.AddAsync("a.txt"));
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopRecentDocuments.Unavailable((PlatformUnavailableReason)99));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await new RecentDocuments(TemporaryFile()).AddAsync("relative.txt"));
    }

    // DesktopHost's methods forward to the shared instance off macOS.
    [Test]
    public async Task DesktopHostForwardsOffMacOS()
    {
        if (OperatingSystem.IsMacOS()) return;
        var file = TemporaryFile();
        var previous = RecentDocuments.Shared;
        RecentDocuments.Shared = new RecentDocuments(file);
        try
        {
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new ClosableWindowHostFactory(), WaitForConnection = false });
            await host.NoteRecentDocumentAsync(Root + "plan.md");
            Assert.Single(Bookmarks(file));
            await host.ClearRecentDocumentsAsync();
            Assert.Empty(Bookmarks(file));
        }
        finally
        {
            RecentDocuments.Shared = previous;
            if (Directory.Exists(Path.GetDirectoryName(file))) Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}
