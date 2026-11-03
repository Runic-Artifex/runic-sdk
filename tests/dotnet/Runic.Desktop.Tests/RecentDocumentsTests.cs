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
            var path = Root + "Notes/../Notes/plan #1 ü.md";
            Assert.True((await recent.AddAsync(path)).Succeeded);
            var bookmark = Assert.Single(Bookmarks(file));
            // The path is made absolute first.
            Assert.Equal(RecentDocuments.Xbel.FileUri(Path.GetFullPath(path)), (string?)bookmark.Attribute("href"));
            Assert.NotNull(bookmark.Attribute("added"));
            Assert.Equal("text/markdown", (string?)bookmark.Descendants(Mime + "mime-type").Single().Attribute("type"));
            var application = bookmark.Descendants(Bookmark + "application").Single();
            Assert.Equal(recent.ApplicationName, (string?)application.Attribute("name"));
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
            var name = new RecentDocuments(file).ApplicationName;
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
    public async Task AMissingFileStartsANewListAndABrokenOneIsLeftAlone()
    {
        var file = TemporaryFile();
        try
        {
            var recent = new RecentDocuments(file);
            Assert.True((await recent.ClearAsync()).Succeeded);
            Assert.False(File.Exists(file));
            Assert.True((await recent.AddAsync(Root + "a.txt")).Succeeded);
            Assert.Single(Bookmarks(file));
            Assert.Equal("xbel", XDocument.Load(file).Root!.Name.LocalName);

            // Other applications' history in a file that does not parse, or is no bookmark file, is never replaced.
            foreach (var broken in (string[])["not xml <", "<?xml version=\"1.0\"?><bookmarks><bookmark href=\"file:///x\"/></bookmarks>"])
            {
                await File.WriteAllTextAsync(file, broken);
                var added = await recent.AddAsync(Root + "b.txt");
                Assert.True(added is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.IOError, Diagnostic.Domain: "xbel" }, $"{added}");
                Assert.True((await recent.ClearAsync()) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.IOError });
                Assert.Equal(broken, await File.ReadAllTextAsync(file));
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    // The application's identity: its ID when given, else the entry assembly's name (also under `dotnet app.dll`, which
    // is not "dotnet"); exec is g_shell_quote of the shell-quoted command and " %u". Clearing removes only this name.
    [Test]
    public async Task TheApplicationIsNamedByItsIdOrAssemblyAndQuotedAsGLibDoes()
    {
        Assert.Equal("'a'\\''b c'", RecentDocuments.Xbel.ShellQuote("a'b c"));
        var current = RecentDocuments.Xbel.Application.Current(null);
        Assert.Equal(System.Reflection.Assembly.GetEntryAssembly()!.GetName().Name, current.Name);
        Assert.NotEqual("dotnet", current.Name);
        // g_shell_quote of "'<process>' %u": the inner quote opens as '\''.
        Assert.StartsWith("''\\''", current.Exec);
        Assert.EndsWith(" %u'", current.Exec);
        Assert.Equal("org.example.Notes", RecentDocuments.Xbel.Application.Current("org.example.Notes").Name);

        var file = TemporaryFile();
        try
        {
            var mine = new RecentDocuments(file, "org.example.Notes");
            var other = new RecentDocuments(file, "org.example.Other");
            Assert.True((await mine.AddAsync(Root + "mine.txt")).Succeeded);
            Assert.True((await other.AddAsync(Root + "theirs.txt")).Succeeded);
            Assert.True((await other.AddAsync(Root + "mine.txt")).Succeeded);
            Assert.True((await mine.ClearAsync()).Succeeded);
            var left = Bookmarks(file);
            Assert.Equal(2, left.Length);
            Assert.All(left, bookmark => Assert.Equal(["org.example.Other"], Applications(bookmark)));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    // The file keeps the newest MaximumBookmarks bookmarks.
    [Test]
    public async Task WritesKeepTheNewestBookmarks()
    {
        var file = TemporaryFile();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            var old = string.Concat(Enumerable.Range(0, RecentDocuments.Xbel.MaximumBookmarks + 20).Select(index =>
                $"<bookmark href=\"file:///home/me/old{index}.txt\" added=\"2020-01-01T00:00:00Z\" modified=\"2020-01-01T00:00:00Z\" " +
                $"visited=\"{new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index):yyyy-MM-ddTHH:mm:ssZ}\"/>"));
            await File.WriteAllTextAsync(file, $"<?xml version=\"1.0\"?><xbel version=\"1.0\">{old}</xbel>");
            Assert.True((await new RecentDocuments(file).AddAsync(Root + "new.txt")).Succeeded);
            var hrefs = Bookmarks(file).Select(static bookmark => (string)bookmark.Attribute("href")!).ToList();
            Assert.Equal(RecentDocuments.Xbel.MaximumBookmarks, hrefs.Count);
            Assert.Contains(RecentDocuments.Xbel.FileUri(Path.GetFullPath(Root + "new.txt")), hrefs);
            Assert.DoesNotContain("file:///home/me/old20.txt", hrefs);
            Assert.Contains("file:///home/me/old21.txt", hrefs);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    // Windows CI: SHAddToRecentDocs, on its STA thread, puts a shortcut into the Recent items folder.
    [Test]
    public async Task WindowsAddsAShortcutToRecentItems()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") return;
        using (var explorer = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
        {
            if (explorer?.GetValue("Start_TrackDocs") is 0)
            {
                Console.WriteLine("Recent items are off on this runner (Start_TrackDocs = 0); the shortcut check is skipped.");
                return;
            }
        }
        var directory = Path.Combine(Path.GetTempPath(), "runic-recent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var document = Path.Combine(directory, "runic-recent-check.txt");
        await File.WriteAllTextAsync(document, "recent");
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Recent), "runic-recent-check.txt.lnk");
        try
        {
            Assert.True((await new RecentDocuments(null).AddAsync(document)).Succeeded);
            for (var waited = 0; !File.Exists(shortcut); waited += 100)
            {
                if (waited > 10_000) throw new InvalidOperationException($"SHAddToRecentDocs did not create {shortcut}.");
                await Task.Delay(100);
            }
        }
        finally
        {
            if (File.Exists(shortcut)) File.Delete(shortcut);
            Directory.Delete(directory, recursive: true);
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
