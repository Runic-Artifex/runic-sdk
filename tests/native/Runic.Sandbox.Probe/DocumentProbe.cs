using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text;
using Runic.Platform;
using Runic.Platform.MacOS;
using static Runic.Sandbox.Probe.ProbeWindow;

namespace Runic.Sandbox.Probe;

/// <summary>
/// <c>--document-probe</c> and <c>--document-reopen</c>: a document app in the App Sandbox (A1). The first mode keeps a
/// file of the container with a security-scoped bookmark, opens it as a document lease through
/// <see cref="IDesktopFileBookmarks.OpenDocumentAsync"/>, saves it three times, which in the sandbox stages each write in
/// the item replacement directory rather than beside the file, resolves the first bookmark again and stores the lease's
/// own bookmark. The second mode, a new launch, reopens the document from that stored bookmark, reads the last save and
/// saves once more.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class DocumentProbe
{
    private const string Name = "document.txt", BookmarkName = "document.bookmark";

    internal static int Run() => ProbeWindow.Run(RunAsync);

    internal static int Reopen() => ProbeWindow.Run(ReopenAsync);

    private static async Task RunAsync(ProbeContext context)
    {
        Check(context.Services.GetSnapshot().Statuses.GetValueOrDefault(PlatformCapabilityIds.FilesOpenDocument) is CapabilityStatus.Available,
            "documents are available in the sandbox");
        string directory = Directory();
        string file = Path.Combine(directory, Name);
        await File.WriteAllTextAsync(file, "original");
        File.Delete(Path.Combine(directory, BookmarkName));

        var bookmarks = MacOSPlatformProvider.CreateFileBookmarks();
        var created = await bookmarks.CreateAsync(file);
        Check(created.TryGetValue(out var bookmark), $"a security-scoped bookmark of the container file: {created}");
        ImmutableArray<byte> own;
        var opened = await bookmarks.OpenDocumentAsync(bookmark);
        Check(opened.TryGetValue(out var document), $"the bookmark opens as a document: {opened}");
        await using (document!)
        {
            Check(await ReadAsync(document!) == "original", "the document reads the file");
            for (int index = 1; index <= 3; index++)
            {
                var committed = await WriteAsync(document!, $"save {index}");
                Check(committed is FileCommitResult.Committed && await File.ReadAllTextAsync(file) == $"save {index}", $"save {index} commits: {committed}");
                // The item replacement directory, not the file's folder, held the staged bytes.
                string[] entries = System.IO.Directory.GetFileSystemEntries(directory);
                Check(entries.Length == 1, $"no staging file beside the document after save {index}: {string.Join(", ", entries.Select(Path.GetFileName))}");
            }
            Console.WriteLine("PASS document saves: three saves through NSItemReplacementDirectory staging and replaceItemAtURL.");
            var again = await document!.CreateBookmarkAsync();
            Check(again.TryGetValue(out own), $"the document lease creates its own bookmark: {again}");
        }

        // The bookmark created before the three replacements still reaches the file.
        var resolved = await bookmarks.ResolveAsync(bookmark);
        Check(resolved.TryGetValue(out var access), $"the first bookmark resolves after the saves: {resolved}");
        await using (access!)
            Check(await File.ReadAllTextAsync(access!.LocalPath) == "save 3", $"the first bookmark reaches the last save (stale {access.IsStale})");
        Console.WriteLine($"PASS bookmark after replacement: the first bookmark resolves to the saved file (stale {access!.IsStale}).");

        await File.WriteAllBytesAsync(Path.Combine(directory, BookmarkName), [.. own]);
        Console.WriteLine("DOCUMENT_PROBE_OK");
    }

    private static async Task ReopenAsync(ProbeContext context)
    {
        string directory = Directory();
        string file = Path.Combine(directory, Name), stored = Path.Combine(directory, BookmarkName);
        try
        {
            Check(File.Exists(stored), "the first launch stored the document's bookmark");
            ImmutableArray<byte> bookmark = [.. await File.ReadAllBytesAsync(stored)];
            var opened = await MacOSPlatformProvider.CreateFileBookmarks().OpenDocumentAsync(bookmark);
            Check(opened.TryGetValue(out var document), $"the stored bookmark reopens the document in a new launch: {opened}");
            await using (document!)
            {
                Check(await ReadAsync(document!) == "save 3", "the reopened document holds the last save of the first launch");
                var committed = await WriteAsync(document!, "reopened");
                Check(committed is FileCommitResult.Committed && await ReadAsync(document!) == "reopened", $"the reopened document saves: {committed}");
            }
            Console.WriteLine("PASS document reopen: a new launch reopened the document from its bookmark, read it and saved it.");
        }
        finally
        {
            File.Delete(file);
            File.Delete(stored);
        }
        Console.WriteLine("DOCUMENT_REOPEN_OK");
    }

    // The container's own Application Support folder, as in SandboxProbe.
    private static string Directory()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "org.runic.tests.SandboxProbe", "documents");
        Check(directory.Contains("/Library/Containers/", StringComparison.Ordinal), $"application data in the container: {directory}");
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<string> ReadAsync(IDocumentFileLease document)
    {
        var read = await document.OpenReadAsync();
        Check(read.TryGetValue(out var stream), $"the document reads: {read}");
        await using (stream!)
        using (var reader = new StreamReader(stream!))
            return await reader.ReadToEndAsync();
    }

    private static async Task<FileCommitResult> WriteAsync(IDocumentFileLease document, string text)
    {
        var begun = await document.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace);
        Check(begun.TryGetValue(out var transaction), $"a staged write of the document: {begun}");
        await using (transaction!)
        {
            await transaction!.Content.WriteAsync(Encoding.UTF8.GetBytes(text));
            return await transaction.CommitAsync();
        }
    }
}
