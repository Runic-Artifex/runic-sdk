using System.Collections.Immutable;
using System.Text;
using Runic.Platform;
using Runic.Platform.Runtime;
using TUnit.Core;

// Repeatable writes and the conflict baseline of every save lease, and document leases from dialogs and bookmarks.
internal sealed class DocumentLeaseTests
{
    [Test, DisplayName("FILE: a save lease writes repeatedly and compares with the content it last saw")]
    public async Task RepeatableWritesAsync()
    {
        await using var directory = new TemporaryDirectory();
        string path = directory.File("document.txt", "original");
        await using (var save = await SaveFileLease.CreateAsync(path, new LocalAtomicFileReplacement()))
        {
            for (int index = 1; index <= 3; index++)
                Check(await WriteAsync(save, $"save {index}") is FileCommitResult.Committed && await File.ReadAllTextAsync(path) == $"save {index}",
                    $"save {index} must compare with the previous save, not the selection");

            var open = await BeginAsync(save);
            Check(await save.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace) is PlatformResult<IFileWriteTransaction>.Failed { Code: PlatformFailureCode.ResourceBusy },
                "a second write while one is open is ResourceBusy");
            await open.DisposeAsync();
            Check(await WriteAsync(save, "after dispose") is FileCommitResult.Committed, "a disposed transaction lets the next write begin");

            // Another writer: Conflict, and the lease keeps refusing until the application sees the new content.
            await File.WriteAllTextAsync(path, "external");
            Check(await WriteAsync(save, "lost") is FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict }, "an external write is a conflict");
            Check(await WriteAsync(save, "lost again") is FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict },
                "a refused commit does not move the baseline");
            Check(await File.ReadAllTextAsync(path) == "external" && directory.Count == 1, "conflicts keep the file and leave no staging");

            // A finished commit releases the next write even before the transaction is disposed.
            var first = await BeginAsync(save);
            await first.Content.WriteAsync("x"u8.ToArray());
            Check(await first.CommitAsync() is FileCommitResult.NotCommitted, "conflict");
            var second = await BeginAsync(save);
            await second.DisposeAsync();
        }
        Check(directory.Count == 1, "the lease disposes undisposed transactions and their staging");

        // A cancelled commit also finishes the transaction.
        await using (var save = await SaveFileLease.CreateAsync(path, new LocalAtomicFileReplacement()))
        {
            var cancelled = await BeginAsync(save);
            using var token = new CancellationTokenSource();
            token.Cancel();
            await Throws<OperationCanceledException>(() => cancelled.CommitAsync(token.Token).AsTask());
            Check(await WriteAsync(save, "after cancellation") is FileCommitResult.Committed, "a cancelled commit lets the next write begin");
        }
    }

    [Test, DisplayName("FILE: after an uncertain commit both the previous and the staged content are the application's")]
    public async Task UncertainCommitAsync()
    {
        await using var directory = new TemporaryDirectory();
        string path = directory.File("document.txt", "original");
        foreach (bool applied in new[] { true, false })
        {
            await File.WriteAllTextAsync(path, "original");
            var replacement = new UncertainReplacement { Apply = applied };
            await using var save = await SaveFileLease.CreateAsync(path, replacement);
            Check(await WriteAsync(save, "uncertain") is FileCommitResult.CommitUnknown, "the seam reports an uncertain outcome");
            Check(await File.ReadAllTextAsync(path) == (applied ? "uncertain" : "original"), "the seam applied the rename or not");
            replacement.Uncertain = false;
            Check(await WriteAsync(save, "clean") is FileCommitResult.Committed && await File.ReadAllTextAsync(path) == "clean",
                $"a clean commit follows an uncertain one that was {(applied ? "" : "not ")}applied");
            await File.WriteAllTextAsync(path, "original");
            Check(await WriteAsync(save, "stale") is FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict },
                "a committed write replaces the uncertain baseline");
        }
        Check(directory.Count == 1, "no staging remains");
    }

    [Test, DisplayName("FILE: a document reads the committed file, also during a write, and a read moves the baseline")]
    public async Task DocumentReadsAsync()
    {
        await using var directory = new TemporaryDirectory();
        string path = directory.File("document.txt", "original");
        var access = new Access();
        var document = await DocumentFileLease.CreateAsync(path, new LocalAtomicFileReplacement(), access, new PathFileBookmarks(), existing: true);
        Check(document.LocalPath == path && document.DisplayName == "document.txt", "path and display name");
        Check(await ReadAsync(document) == "original", "first read");

        // Read during an open transaction: committed content, never staging, and the pending commit compares with it.
        var write = await BeginAsync(document);
        await write.Content.WriteAsync("staged"u8.ToArray());
        await write.Content.FlushAsync();
        await File.WriteAllTextAsync(path, "external");
        Check(await ReadAsync(document) == "external", "a read during a write returns the committed file");
        Check(await write.CommitAsync() is FileCommitResult.Committed && await File.ReadAllTextAsync(path) == "staged",
            "the open commit compares with the content read during it");
        await write.DisposeAsync();

        // Read-then-write overwrites after a conflict.
        await File.WriteAllTextAsync(path, "other writer");
        Check(await WriteAsync(document, "mine") is FileCommitResult.NotCommitted { Code: PlatformFailureCode.Conflict }, "conflict");
        Check(await ReadAsync(document) == "other writer", "the read returns the other writer's content");
        Check(await WriteAsync(document, "mine") is FileCommitResult.Committed && await File.ReadAllTextAsync(path) == "mine", "read then write overwrites");
        for (int index = 1; index <= 3; index++)
        {
            Check(await WriteAsync(document, $"save {index}") is FileCommitResult.Committed, $"document save {index}");
            Check(await ReadAsync(document) == $"save {index}", $"reopen {index}");
        }

        // A file removed by another program: NotFound, which makes the absence the baseline, so a write recreates it.
        File.Delete(path);
        Check(await document.OpenReadAsync() is PlatformResult<Stream>.Failed { Code: PlatformFailureCode.NotFound }, "a missing document is NotFound");
        Check(await WriteAsync(document, "recreated") is FileCommitResult.Committed && await File.ReadAllTextAsync(path) == "recreated",
            "after a NotFound read a write recreates the file");

        // Streams the caller still holds close with the lease; access is released once.
        var held = (await document.OpenReadAsync() as PlatformResult<Stream>.Success)!.Value;
        var readBack = (await document.OpenReadAsync() as PlatformResult<Stream>.Success)!.Value;
        await readBack.DisposeAsync();
        Check(held.CanSeek && held.Position == 0, "each read is a new seekable stream at its start");
        await document.DisposeAsync();
        await document.DisposeAsync();
        Check(!held.CanRead && access.Releases == 1, "disposal closes open streams and releases access once");
        await Throws<ObjectDisposedException>(() => document.OpenReadAsync().AsTask());
        await Throws<ObjectDisposedException>(() => Task.FromResult(document.LocalPath));
        await Throws<ObjectDisposedException>(() => document.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace).AsTask());
        await Throws<ObjectDisposedException>(() => document.CreateBookmarkAsync().AsTask());
        Check(directory.Count == 1, "no staging remains");

        if (!OperatingSystem.IsWindows())
        {
            // A path that is no longer a regular file fails without blocking.
            string fifo = Path.Combine(directory.Path, "fifo");
            using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [fifo])) mkfifo.WaitForExit();
            Check(await Throws<IOException>(() => DocumentFileLease.CreateAsync(fifo, new LocalAtomicFileReplacement(), null, null, existing: true).AsTask()),
                "a FIFO is not a document");
            File.Delete(fifo);
        }
    }

    [Test, DisplayName("FILE: document dialogs, bookmarks from leases and presentation close")]
    public async Task DocumentDialogsAndBookmarksAsync()
    {
        await using var directory = new TemporaryDirectory();
        string path = directory.File("notes.txt", "opened");
        var owner = new Owner();
        var picker = new Picker { Path = path };
        var backend = new NativePickerBackend(owner, picker);
        Check(backend.SupportsDocumentAccess, "the native backend implements documents");
        ImmutableArray<byte> bookmark;
        await using (var lifetime = new PresentationLifetime(() => true, owner.Generation))
        {
            var files = new PresentationFiles(lifetime, backend);
            Check(files.GetSnapshot().Statuses[PlatformCapabilityIds.FilesOpenDocument] is CapabilityStatus.Available, "documents are available");
            var opened = await files.OpenDocumentAsync(new OpenFileOptions());
            var document = (opened as PickerResult<IDocumentFileLease>.Selected)?.Value ?? throw new InvalidOperationException("open: " + opened);
            Check(document.LocalPath == path && await ReadAsync(document) == "opened", "the dialog's document reads the selection");
            for (int index = 1; index <= 3; index++)
                Check(await WriteAsync(document, $"saved {index}") is FileCommitResult.Committed, $"dialog document save {index}");
            Check(await ReadAsync(document) == "saved 3", "reopen after three saves");
            // macOS dialog leases have no bookmarks until the macOS document support; path bookmarks still work there.
            bool macOS = OperatingSystem.IsMacOS();
            var created = await document.CreateBookmarkAsync();
            if (macOS)
            {
                Check(created is PlatformResult<ImmutableArray<byte>>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "macOS lease bookmarks are unavailable");
                bookmark = [.. Encoding.UTF8.GetBytes(path)];
            }
            else bookmark = (created as PlatformResult<ImmutableArray<byte>>.Success)?.Value ?? throw new InvalidOperationException("bookmark: " + created);
            Check(Encoding.UTF8.GetString(bookmark.AsSpan()) == path, "Windows and Linux bookmarks are the path");

            // Save As: the destination does not exist until the first commit.
            string saved = Path.Combine(directory.Path, "copy.txt");
            picker.Path = saved;
            var destination = await files.SaveDocumentAsync(new SaveFileOptions("copy.txt"));
            var copy = (destination as PickerResult<IDocumentFileLease>.Selected)?.Value ?? throw new InvalidOperationException("save: " + destination);
            Check(await copy.OpenReadAsync() is PlatformResult<Stream>.Failed { Code: PlatformFailureCode.NotFound }, "an unwritten destination is NotFound");
            Check(macOS || await copy.CreateBookmarkAsync() is PlatformResult<ImmutableArray<byte>>.Failed { Code: PlatformFailureCode.NotFound }, "no bookmark before the first write");
            Check(await WriteAsync(copy, "copy 1") is FileCommitResult.Committed && await WriteAsync(copy, "copy 2") is FileCommitResult.Committed
                && await ReadAsync(copy) == "copy 2", "Save As then Save");

            // A plain save lease also writes repeatedly and creates bookmarks.
            var plain = (await files.SaveFileAsync(new SaveFileOptions("copy.txt")) as PickerResult<ISaveFileLease>.Selected)!.Value;
            Check(await WriteAsync(plain, "plain 1") is FileCommitResult.Committed && await WriteAsync(plain, "plain 2") is FileCommitResult.Committed,
                "SaveFileAsync's lease writes repeatedly");
            var plainBookmark = await plain.CreateBookmarkAsync();
            Check(macOS ? plainBookmark is PlatformResult<ImmutableArray<byte>>.Unavailable : plainBookmark is PlatformResult<ImmutableArray<byte>>.Success,
                "a save lease creates a bookmark");
            Check(picker.Releases == 0, "leases keep their access");
            await lifetime.DisposeAsync();
            Check(picker.Releases == 3, "presentation close releases dialog documents");
            await Throws<ObjectDisposedException>(() => document.OpenReadAsync().AsTask());
            await Throws<ObjectDisposedException>(() => Task.FromResult(copy.LocalPath));
        }

        // A bookmark's document belongs to no presentation.
        var bookmarks = new PathFileBookmarks();
        var reopened = await bookmarks.OpenDocumentAsync(bookmark);
        await using (var document = (reopened as PlatformResult<IDocumentFileLease>.Success)?.Value ?? throw new InvalidOperationException("reopen: " + reopened))
        {
            Check(await ReadAsync(document) == "saved 3", "the bookmark reopens the document");
            Check(await WriteAsync(document, "from bookmark") is FileCommitResult.Committed && await ReadAsync(document) == "from bookmark", "and saves to it");
            var again = await document.CreateBookmarkAsync();
            Check(again is PlatformResult<ImmutableArray<byte>>.Success(var same) && same.SequenceEqual(bookmark), "a bookmark lease creates the same bookmark");
        }
        Check(await bookmarks.OpenDocumentAsync([.. Encoding.UTF8.GetBytes(directory.Path)]) is PlatformResult<IDocumentFileLease>.Failed { Code: PlatformFailureCode.InvalidData },
            "a directory is not a document");
        Check(await bookmarks.OpenDocumentAsync([.. Encoding.UTF8.GetBytes(Path.Combine(directory.Path, "gone.txt"))]) is PlatformResult<IDocumentFileLease>.Failed { Code: PlatformFailureCode.NotFound },
            "a missing file is NotFound");
        Check(await bookmarks.OpenDocumentAsync([0xFF]) is PlatformResult<IDocumentFileLease>.Failed { Code: PlatformFailureCode.InvalidData }, "invalid bytes");
        await Throws<ArgumentException>(() => bookmarks.OpenDocumentAsync([]).AsTask());
        bool classified = false;
        var portal = new PathFileBookmarks((_, _) => { classified = true; return ValueTask.FromResult(NativeFileLocation.DocumentPortal); });
        await using (var document = ((PlatformResult<IDocumentFileLease>.Success)await portal.OpenDocumentAsync(bookmark)).Value)
            Check(classified && await WriteAsync(document, "document view") is FileCommitResult.Committed, "the classifier selects the document view");
        Check(directory.Count == 2, "no staging remains");
    }

    [Test, DisplayName("FILE: default document members and backends without document access")]
    public async Task DefaultMembersAsync()
    {
        IFileDialogs dialogs = new MinimalDialogs();
        Check(await dialogs.OpenDocumentAsync(new OpenFileOptions()) is PickerResult<IDocumentFileLease>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "default OpenDocumentAsync");
        Check(await dialogs.SaveDocumentAsync(new SaveFileOptions("a.txt")) is PickerResult<IDocumentFileLease>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "default SaveDocumentAsync");
        await Throws<ArgumentNullException>(() => dialogs.OpenDocumentAsync(null!).AsTask());
        await Throws<ArgumentException>(() => dialogs.SaveDocumentAsync(new SaveFileOptions("a/b.txt")).AsTask());
        await Throws<ArgumentOutOfRangeException>(() => dialogs.OpenDocumentAsync(new OpenFileOptions((OwnerPolicy)99)).AsTask());
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Throws<OperationCanceledException>(() => dialogs.OpenDocumentAsync(new OpenFileOptions(), cancelled.Token).AsTask());
        }
        ISaveFileLease lease = new MinimalLease();
        Check(await lease.CreateBookmarkAsync() is PlatformResult<ImmutableArray<byte>>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "default CreateBookmarkAsync");
        IDesktopFileBookmarks bookmarks = new MinimalBookmarks();
        Check(await bookmarks.OpenDocumentAsync([1]) is PlatformResult<IDocumentFileLease>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "default IDesktopFileBookmarks.OpenDocumentAsync");
        await Throws<ArgumentException>(() => bookmarks.OpenDocumentAsync(default).AsTask());

        var unavailable = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable).FileDialogs;
        Check(await unavailable.OpenDocumentAsync(new OpenFileOptions()) is PickerResult<IDocumentFileLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerUnavailable }
            && await unavailable.SaveDocumentAsync(new SaveFileOptions("a.txt")) is PickerResult<IDocumentFileLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerUnavailable },
            "unavailable services report their reason");

        // PresentationFiles never calls a backend that reports no document access.
        var legacy = new LegacyBackend();
        await using var lifetime = new PresentationLifetime(() => true);
        var files = new PresentationFiles(lifetime, legacy);
        Check(files.GetSnapshot().Statuses[PlatformCapabilityIds.FilesOpenDocument] is CapabilityStatus.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "a backend without document access reports the capability unavailable");
        Check(await files.OpenDocumentAsync(new OpenFileOptions()) is PickerResult<IDocumentFileLease>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
            && await files.SaveDocumentAsync(new SaveFileOptions("a.txt")) is PickerResult<IDocumentFileLease>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
            && legacy.Calls == 0, "and is not called");
        Check(new PresentationFiles(lifetime).GetSnapshot().Statuses[PlatformCapabilityIds.FilesOpenDocument] is CapabilityStatus.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured },
            "no provider");
    }

    [Test, DisplayName("FILE: Windows reports a replaced file held open without delete sharing as ResourceBusy")]
    public async Task WindowsHeldOpenAsync()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP held-open replacement: ReplaceFile is Windows only."); return; }
        await using var directory = new TemporaryDirectory();
        string path = directory.File("held.txt", "original");
        await using var document = await DocumentFileLease.CreateAsync(path, new LocalAtomicFileReplacement(), null, new PathFileBookmarks(), existing: true);
        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var result = await WriteAsync(document, "blocked");
            Check(result is FileCommitResult.NotCommitted { Code: PlatformFailureCode.ResourceBusy, Diagnostic: { Domain: "HRESULT", Code: 0x80070497L } },
                "ERROR_UNABLE_TO_REMOVE_REPLACED is a refused replacement: " + result);
        }
        Check(await File.ReadAllTextAsync(path) == "original" && directory.Count == 1, "the held file is unchanged and staging is removed");
        Check(await WriteAsync(document, "released") is FileCommitResult.Committed && await File.ReadAllTextAsync(path) == "released",
            "the next write commits once the other program closes the file");
    }

    private static async Task<string> ReadAsync(IDocumentFileLease document)
    {
        var read = await document.OpenReadAsync();
        if (read is not PlatformResult<Stream>.Success(var stream)) throw new InvalidOperationException("read: " + read);
        await using (stream)
        using (var reader = new StreamReader(stream))
            return await reader.ReadToEndAsync();
    }

    private static async Task<IFileWriteTransaction> BeginAsync(ISaveFileLease save) =>
        (await save.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace) as PlatformResult<IFileWriteTransaction>.Success)?.Value
        ?? throw new InvalidOperationException("Staging unavailable.");

    private static async Task<FileCommitResult> WriteAsync(ISaveFileLease save, string text)
    {
        await using var transaction = await BeginAsync(save);
        await transaction.Content.WriteAsync(Encoding.UTF8.GetBytes(text));
        return await transaction.CommitAsync();
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<bool> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return true; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"runic-documents-{Guid.NewGuid():N}");
        internal TemporaryDirectory() => Directory.CreateDirectory(Path);
        internal int Count => Directory.GetFileSystemEntries(Path).Length;
        internal string File(string name, string text)
        {
            string path = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(path, text);
            return path;
        }
        public ValueTask DisposeAsync() { Directory.Delete(Path, recursive: true); return ValueTask.CompletedTask; }
    }

    // Applies the rename or not, then reports an uncertain outcome while Uncertain is set.
    private sealed class UncertainReplacement : IAtomicFileReplacement
    {
        internal bool Apply { get; init; }
        internal bool Uncertain { get; set; } = true;
        public bool IsSupported => true;
        public async ValueTask<FileCommitResult> ReplaceAsync(string stagingPath, string targetPath, bool existed)
        {
            if (!Uncertain || Apply)
            {
                var outcome = await new LocalAtomicFileReplacement().ReplaceAsync(stagingPath, targetPath, existed);
                if (!Uncertain) return outcome;
            }
            return new FileCommitResult.CommitUnknown(PlatformFailureCode.IOError);
        }
    }

    private sealed class Access : IAsyncDisposable
    {
        internal int Releases;
        public ValueTask DisposeAsync() { Releases++; return ValueTask.CompletedTask; }
    }

    private sealed class Owner : INativeWindowOwner
    {
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => true;
        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // Returns Path with an access grant that counts its release.
    private sealed class Picker : INativeFilePicker
    {
        internal string Path = "";
        internal int Releases;
        public ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<NativeFileSelection>?>([new(Path, new Release(this), AllowsSiblingReplacement: true)]);
        private sealed class Release(Picker picker) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { Interlocked.Increment(ref picker.Releases); return ValueTask.CompletedTask; }
        }
    }

    private sealed class LegacyBackend : IPickerBackend
    {
        internal int Calls;
        public bool IsAvailable => true;
        public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PickerResult<IDocumentFileLease>> OpenDocumentAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
        { Calls++; throw new NotSupportedException(); }
        public ValueTask<PickerResult<IDocumentFileLease>> SaveDocumentAsync(SaveFileOptions options, CancellationToken cancellationToken = default)
        { Calls++; throw new NotSupportedException(); }
    }

    private sealed class MinimalDialogs : IFileDialogs
    {
        public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class MinimalLease : ISaveFileLease
    {
        public string DisplayName => "a.txt";
        public ValueTask<PlatformResult<IFileWriteTransaction>> BeginWriteAsync(FileWritePolicy policy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MinimalBookmarks : IDesktopFileBookmarks
    {
        public ValueTask<PlatformResult<ImmutableArray<byte>>> CreateAsync(string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlatformResult<IDesktopBookmarkAccess>> ResolveAsync(ImmutableArray<byte> bookmark, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
