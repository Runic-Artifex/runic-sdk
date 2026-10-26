using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Runic.Platform;
using Runic.Platform.MacOS;
using Runic.Platform.Runtime;
using TUnit.Core;
using static Runic.Platform.MacOS.MacDesktopNative;

// NSURL bookmarks: ordinary bookmarks in the test process (CI's macOS runner, JIT and NativeAOT), and security-scoped
// ones in the App Sandbox fixture (tests/native/platform-sandbox), which CI runs with --bookmark-probe (Program.cs).
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed partial class MacBookmarksNativeTests
{
    [Test, Explicit, Category("NativeOpenEvents"), DisplayName("native bookmarks: create, resolve, follow a moved file, report a deleted one"), Timeout(60_000)]
    public async Task BookmarksAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("NSURL bookmarks need macOS.");
        cancellationToken.ThrowIfCancellationRequested();
        await CheckAsync(Path.GetTempPath());
    }

    // The document sequence without a panel: a bookmark of a file becomes a document lease, which saves three times
    // beside the file (unsandboxed), loses to another writer, recovers by reading, and reopens through the lease's own
    // bookmark. Then the item replacement of sandboxed selections, outside the sandbox: replace, create, conflict.
    [Test, Explicit, Category("NativeOpenEvents"), DisplayName("native documents: open, save three times, conflict, reopen; item replacement"), Timeout(60_000)]
    public async Task DocumentsAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("NSURL bookmarks need macOS.");
        cancellationToken.ThrowIfCancellationRequested();
        var bookmarks = MacOSPlatformProvider.CreateFileBookmarks();
        string folder = Path.Combine(Path.GetTempPath(), "runic-documents-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "notes.txt");
            await File.WriteAllTextAsync(file, "original", cancellationToken);
            var created = await bookmarks.CreateAsync(file, cancellationToken);
            Check(created.TryGetValue(out var bookmark), $"a bookmark: {created}");
            Check(await bookmarks.OpenDocumentAsync([.. folder.Select(static c => (byte)c)], cancellationToken) is PlatformResult<IDocumentFileLease>.Failed,
                "bytes that are not a bookmark do not open");

            ImmutableArray<byte> reopen;
            var opened = await bookmarks.OpenDocumentAsync(bookmark, cancellationToken);
            Check(opened.TryGetValue(out var document), $"the bookmark opens as a document: {opened}");
            await using (document!)
            {
                Check(await ReadAsync(document!) == "original" && document!.LocalPath.EndsWith("notes.txt", StringComparison.Ordinal), $"reads {document!.LocalPath}");
                for (int index = 1; index <= 3; index++)
                    Check(await WriteAsync(document, $"save {index}") is FileCommitResult.Committed && await File.ReadAllTextAsync(file, cancellationToken) == $"save {index}",
                        $"save {index} commits against the previous save");
                await File.WriteAllTextAsync(file, "external", cancellationToken);
                Check(await WriteAsync(document, "lost") is FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict }, "another writer is a conflict");
                Check(await ReadAsync(document) == "external" && await WriteAsync(document, "after read") is FileCommitResult.Committed,
                    "reading makes the external content the baseline");
                var again = await document.CreateBookmarkAsync(cancellationToken);
                Check(again.TryGetValue(out reopen), $"the lease creates a bookmark: {again}");
                Check(Directory.GetFileSystemEntries(folder).Length == 1, "no staging file remains beside the document");
            }
            opened = await bookmarks.OpenDocumentAsync(reopen, cancellationToken);
            Check(opened.TryGetValue(out document), $"the lease's bookmark reopens: {opened}");
            await using (document!) Check(await ReadAsync(document!) == "after read", "the reopened document holds the last save");
            Check(await bookmarks.OpenDocumentAsync((await bookmarks.CreateAsync(folder, cancellationToken) as PlatformResult<ImmutableArray<byte>>.Success)!.Value, cancellationToken)
                is PlatformResult<IDocumentFileLease>.Failed { Code: PlatformFailureCode.InvalidData }, "a directory is not a document");

            // MacItemReplacement, as a sandboxed selection uses it: an existing file, then a new one, then a name that
            // appeared after selection.
            await using (var replaced = await DocumentFileLease.CreateAsync(file, new MacItemReplacement(), null, null, existing: true, cancellationToken))
            {
                for (int index = 1; index <= 3; index++)
                    Check(await WriteAsync(replaced, $"replaced {index}") is FileCommitResult.Committed && await File.ReadAllTextAsync(file, cancellationToken) == $"replaced {index}",
                        $"replaceItemAtURL commits save {index}");
                Check(Directory.GetFileSystemEntries(folder).Length == 1, "item replacement stages outside the document's folder");
            }
            string fresh = Path.Combine(folder, "new.txt");
            await using (var save = await SaveFileLease.CreateAsync(fresh, new MacItemReplacement(), cancellationToken: cancellationToken))
            {
                Check(await WriteAsync(save, "new") is FileCommitResult.Committed && await File.ReadAllTextAsync(fresh, cancellationToken) == "new", "a new file is renamed into place");
                Check(await WriteAsync(save, "new again") is FileCommitResult.Committed && await File.ReadAllTextAsync(fresh, cancellationToken) == "new again", "and then replaced");
            }
            string raced = Path.Combine(folder, "raced.txt");
            await using (var save = await SaveFileLease.CreateAsync(raced, new MacItemReplacement(), cancellationToken: cancellationToken))
            {
                var transaction = (await save.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace, cancellationToken) as PlatformResult<IFileWriteTransaction>.Success)!.Value;
                await using (transaction)
                {
                    await transaction.Content.WriteAsync("mine"u8.ToArray(), cancellationToken);
                    await File.WriteAllTextAsync(raced, "theirs", cancellationToken);
                    Check(await transaction.CommitAsync(cancellationToken) is FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict }
                        && await File.ReadAllTextAsync(raced, cancellationToken) == "theirs", "a name that appeared after selection is never replaced");
                }
            }
            // The exclusive rename itself refuses a name that appears between the check and the rename.
            var exclusive = new MacItemReplacement((source, target, flags) => { File.WriteAllText(target, "racing"); return flags == 4 ? 17 : 0; });
            string staged = exclusive.CreateStagingPath(Path.Combine(folder, "exclusive.txt"));
            await File.WriteAllTextAsync(staged, "mine", cancellationToken);
            Check(await exclusive.ReplaceAsync(staged, Path.Combine(folder, "exclusive.txt"), existed: false) is FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict },
                "EEXIST from RENAME_EXCL is a conflict");
            File.Delete(staged);
            exclusive.ReleaseStaging(staged);
            Check(!Directory.Exists(Path.GetDirectoryName(staged)), "the item replacement directory is removed");
            Console.WriteLine("PASS documents: bookmark → document → save ×3 → conflict → read → save → reopen; item replacement replace, create, conflict.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static async Task<string> ReadAsync(IDocumentFileLease document)
    {
        var read = await document.OpenReadAsync();
        if (read is not PlatformResult<Stream>.Success(var stream)) throw new InvalidOperationException("read: " + read);
        await using (stream)
        using (var reader = new StreamReader(stream))
            return await reader.ReadToEndAsync();
    }

    private static async Task<FileCommitResult> WriteAsync(ISaveFileLease save, string text)
    {
        var begun = await save.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace);
        if (begun is not PlatformResult<IFileWriteTransaction>.Success(var transaction)) throw new InvalidOperationException("write: " + begun);
        await using (transaction)
        {
            await transaction.Content.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));
            return await transaction.CommitAsync();
        }
    }

    // The sandboxed fixture: bookmarks of a file in the container are security-scoped; a file outside the container,
    // which the user never chose, gets none.
    internal static int RunSandboxProbe(string outside)
    {
        try
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APP_SANDBOX_CONTAINER_ID")))
                throw new InvalidOperationException("The probe does not run in the App Sandbox.");
            NativeLibrary.TryLoad("/System/Library/Frameworks/Foundation.framework/Foundation", out _);
            string container;
            using (new Pool()) container = Text(TemporaryDirectory());
            CheckAsync(container).GetAwaiter().GetResult();
            var refused = MacOSPlatformProvider.CreateFileBookmarks().CreateAsync(outside).AsTask().GetAwaiter().GetResult();
            Check(refused is PlatformResult<ImmutableArray<byte>>.Failed, $"no bookmark for a file outside the container: {refused}");
            Console.WriteLine($"PASS sandboxed bookmarks: security-scoped in the container; outside it {refused}.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error);
            return 1;
        }
    }

    private static async Task CheckAsync(string directory)
    {
        var bookmarks = MacOSPlatformProvider.CreateFileBookmarks();
        string folder = Path.Combine(directory, "runic-bookmarks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "probe é.txt"), moved = Path.Combine(folder, "moved.txt");
            await File.WriteAllTextAsync(file, "probe");
            var created = await bookmarks.CreateAsync(file);
            Check(created.TryGetValue(out var bookmark) && bookmark.Length > 0, $"a bookmark: {created}");

            var resolved = await bookmarks.ResolveAsync(bookmark);
            Check(resolved.TryGetValue(out var access), $"resolves: {resolved}");
            await using (access!)
            {
                // APFS keeps the name as created, but NSURL may report it decomposed (NFD).
                Check(Path.GetFileName(access!.LocalPath).Normalize() == "probe é.txt".Normalize() && await File.ReadAllTextAsync(access.LocalPath) == "probe" && !access.IsStale,
                    $"resolves to the file: {access.LocalPath} stale={access.IsStale}");
            }

            File.Move(file, moved);
            resolved = await bookmarks.ResolveAsync(bookmark);
            Check(resolved.TryGetValue(out access), $"a moved file resolves: {resolved}");
            await using (access!)
                Check(Path.GetFileName(access!.LocalPath) == "moved.txt", $"the bookmark follows the moved file: {access.LocalPath} stale={access.IsStale}");

            File.Delete(moved);
            resolved = await bookmarks.ResolveAsync(bookmark);
            Check(resolved is PlatformResult<IDesktopBookmarkAccess>.Failed, $"a deleted file does not resolve: {resolved}");
            Check(await bookmarks.CreateAsync(moved) is PlatformResult<ImmutableArray<byte>>.Failed, "no bookmark for a missing file");
            var garbage = await bookmarks.ResolveAsync([1, 2, 3, 4]);
            Check(garbage is PlatformResult<IDesktopBookmarkAccess>.Failed, $"bytes that are not a bookmark: {garbage}");
            Console.WriteLine($"PASS bookmarks: round trip, moved file, deleted file ({resolved}), invalid bytes ({garbage}).");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [LibraryImport("/System/Library/Frameworks/Foundation.framework/Foundation", EntryPoint = "NSTemporaryDirectory")] private static partial nint TemporaryDirectory();

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
