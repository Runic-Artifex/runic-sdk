using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// macOS document conventions (M5, G-6) off macOS: argument and window checks, then nothing. The macOS native smoke
// (--documents) checks representedURL, documentEdited, the recent documents and Open Recent.
public sealed class DocumentConventionsTests
{
    private static readonly string Document = OperatingSystem.IsWindows() ? @"C:\Users\me\Notes\plan.md" : "/home/me/Notes/plan.md";

    [Test]
    public async Task WindowDocumentStateChecksArgumentsAndDoesNothingOffMacOS()
    {
        await using var host = await StartAsync();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("x") });
        var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });

        foreach (var invalid in (string[])["", "notes/plan.md", "plan.md", "/tmp/a\0b"])
            await Assert.ThrowsAnyAsync<ArgumentException>(async () => await window.SetRepresentedFileAsync(invalid));
        // Also on macOS: a custom window host has no NSWindow of Runic's.
        await window.SetRepresentedFileAsync(Document);
        await window.SetRepresentedFileAsync(null);
        await window.SetDocumentEditedAsync(true);
        await window.SetDocumentEditedAsync(false);
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await window.SetDocumentEditedAsync(true, cancelled.Token));
        }

        await window.CloseAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await window.SetDocumentEditedAsync(true));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await window.SetRepresentedFileAsync(Document));
    }

    [Test]
    public async Task RecentDocumentsCheckArgumentsAndDoNothingOffMacOS()
    {
        var host = await StartAsync();
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await host.NoteRecentDocumentAsync(null!));
        foreach (var invalid in (string[])["", "plan.md", "./plan.md", "/tmp/a\0b"])
            await Assert.ThrowsAnyAsync<ArgumentException>(async () => await host.NoteRecentDocumentAsync(invalid));
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await host.NoteRecentDocumentAsync(Document, cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await host.ClearRecentDocumentsAsync(cancelled.Token));
        }
        if (!OperatingSystem.IsMacOS())
        {
            await host.NoteRecentDocumentAsync(Document);
            await host.ClearRecentDocumentsAsync();
        }

        await host.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await host.NoteRecentDocumentAsync(Document));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await host.ClearRecentDocumentsAsync());
    }

    [Test]
    public async Task OpenRecentMenuIsOptInWithTranslatableTitles()
    {
        Assert.False(new MacOSDesktopOptions().OpenRecentMenu);
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new ClosableWindowHostFactory(),
            WaitForConnection = false,
            MacOS = new MacOSDesktopOptions { OpenRecentMenu = true },
            StandardMenuTitles = new DesktopStandardMenuTitles { OpenRecent = "Benutzte Dokumente", ClearMenu = "Einträge löschen" },
        });
        Assert.True(host.Options.MacOS.OpenRecentMenu);
        Assert.Equal("Open Recent", new DesktopStandardMenuTitles().OpenRecent);
        Assert.Equal("Clear Menu", new DesktopStandardMenuTitles().ClearMenu);
        Assert.Equal("clearRecentDocuments:", MacOsMainMenu.ClearRecentAction);

        var invalid = new DesktopStandardMenuTitles { OpenRecent = "{0} recent", ClearMenu = " " };
        Assert.Equal(
            ["DesktopHostOptions.StandardMenuTitles.OpenRecent", "DesktopHostOptions.StandardMenuTitles.ClearMenu"],
            invalid.Check().Select(static diagnostic => diagnostic.Option));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await DesktopHost.StartAsync(new DesktopHostOptions { StandardMenuTitles = invalid }));
    }

    [Test]
    public void RecentDocumentTitlesNameTheFolderOnlyForEqualNames()
    {
        (string Path, string Name)[] documents =
            [("/Users/me/Work/plan.md", "plan.md"), ("/Users/me/Home/plan.md", "plan.md"), ("/Users/me/Work/notes.md", "notes.md")];
        Assert.Equal("plan.md – Work", MacOsMainMenu.RecentDocumentTitle("plan.md", "/Users/me/Work/plan.md", documents));
        Assert.Equal("plan.md – Home", MacOsMainMenu.RecentDocumentTitle("plan.md", "/Users/me/Home/plan.md", documents));
        Assert.Equal("notes.md", MacOsMainMenu.RecentDocumentTitle("notes.md", "/Users/me/Work/notes.md", documents));
        Assert.Equal("runicOpenRecentDocument:", MacOsMainMenu.OpenRecentAction);
    }

    private static ValueTask<DesktopHost> StartAsync() => DesktopHost.StartAsync(new DesktopHostOptions
    {
        WindowHostFactory = new ClosableWindowHostFactory(),
        WaitForConnection = false,
    });
}
