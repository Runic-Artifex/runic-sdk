using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;
using TUnit.Core;

internal sealed class FilePickerTests
{
    // The cases of the former default run. Document-portal saving is a separate case (DocumentPortalTests).
    [Test, DisplayName("portal ownership, cancellation, local URI and access-grant checks")]
    public async Task PortalOwnershipCancellationAndAccessGrantsAsync()
    {
        var owner = new Owner();
        var transport = new FakeTransport(new(0, ["file:///tmp/a%20file.txt"]));
        var picker = new PortalFilePicker(owner, transport);
        Check((await picker.SelectAsync(false, null, default)) is { Path: "/tmp/a file.txt", AllowsSiblingReplacement: true, Location: NativeFileLocation.Host }, "local URI");
        Check(owner.Released == 1, "parent lease released");
        const string exactDirectory = "/tmp/directory space % Ω 文本\nline";
        Check((await new PortalFilePicker(owner, new FakeTransport(new(0, ["file:///tmp/directory%20space%20%25%20%CE%A9%20%E6%96%87%E6%9C%AC%0Aline"])))
            .SelectDirectoryAsync(default)) is { Path: exactDirectory }, "directory URI bytes preserve exact Unicode, spaces, percent and line feeds");
        Check(PortalFilePicker.ReadExactLocalPath("file://localhost/tmp/%2525") == "/tmp/%25", "directory percent decoding occurs once");
        Check(PortalFilePicker.ReadExactLocalPath("file:///tmp/./directory/../name") == "/tmp/./directory/../name", "directory URI does not normalize the selected path");
        foreach (string invalid in new[] { "https://example.org/a", "file://remote/tmp/a", "file:///tmp/a#fragment", "file:///tmp/a?query", "file:///tmp/%FF", "file:///tmp/%C0%AF", "file:///tmp/%00", "file:///tmp/%GG", "file:///tmp/%" })
        {
            try { await new PortalFilePicker(owner, new FakeTransport(new(0, [invalid]))).SelectDirectoryAsync(default); throw new InvalidOperationException("unsafe or lossy directory URI accepted"); }
            catch (IOException) { }
        }
        Check(await new PortalFilePicker(owner, new FakeTransport(new(1, []))).SelectDirectoryAsync(default) is null, "directory dismissal");
        using (var directoryCancelled = new CancellationTokenSource())
        {
            var waiting = new FakeTransport(null);
            var directoryRequest = new PortalFilePicker(owner, waiting).SelectDirectoryAsync(directoryCancelled.Token).AsTask();
            await waiting.Started.Task;
            directoryCancelled.Cancel();
            try { await directoryRequest; throw new InvalidOperationException("directory cancellation ignored"); } catch (OperationCanceledException) { }
            Check(waiting.Cancelled, "directory request drained on cancellation");
        }
        var directoryOwner = new Owner();
        var directoryWaiting = new FakeTransport(null);
        var ownerDirectoryRequest = new PortalFilePicker(directoryOwner, directoryWaiting).SelectDirectoryAsync(default).AsTask();
        await directoryWaiting.Started.Task;
        directoryOwner.Generation = Guid.NewGuid();
        try { await ownerDirectoryRequest.WaitScaledAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("directory owner replacement ignored"); }
        catch (OwnerClosedException) { }
        Check(directoryOwner.Released == 1 && directoryWaiting.Cancelled, "directory owner close releases parent and request");
        foreach (string uri in new[] { "https://example.com/a", "file://remote/tmp/a", "file:///tmp/a#fragment", "file:///tmp/a?query" })
        {
            try { await new PortalFilePicker(owner, new FakeTransport(new(0, [uri]))).SelectAsync(false, null, default); throw new InvalidOperationException("unsafe URI accepted"); }
            catch (IOException) { }
        }
        Check(await new PortalFilePicker(owner, new FakeTransport(new(1, []))).SelectAsync(false, null, default) is null, "dismissal");
        try { await new PortalFilePicker(new Owner { Identifier = "" }, transport).SelectAsync(false, null, default); throw new InvalidOperationException("unparented fallback accepted"); }
        catch (NativeBackendUnavailableException) { }
        var stale = new Owner { ChangeGenerationOnExport = true };
        var unused = new FakeTransport(new(1, []));
        try { await new PortalFilePicker(stale, unused).SelectAsync(false, null, default); throw new InvalidOperationException("stale exported parent accepted"); }
        catch (OwnerClosedException) { }
        Check(!unused.Started.Task.IsCompleted && stale.Released == 1, "owner replaced during export prevents a portal request");
        var pending = new FakeTransport(null);
        var replaced = new Owner();
        var request = new PortalFilePicker(replaced, pending).SelectAsync(false, null, default).AsTask();
        await pending.Started.Task;
        replaced.Generation = Guid.NewGuid();
        try { await request.WaitScaledAsync(TimeSpan.FromSeconds(3)); throw new InvalidOperationException("replacement ignored"); }
        catch (OwnerClosedException) { }
        Check(replaced.Released == 1 && pending.Cancelled, "replacement cancels request and releases parent");
        using (var cancelled = new CancellationTokenSource())
        {
            pending = new FakeTransport(null);
            request = new PortalFilePicker(owner, pending).SelectAsync(false, null, cancelled.Token).AsTask();
            await pending.Started.Task;
            cancelled.Cancel();
            try { await request; throw new InvalidOperationException("cancellation ignored"); } catch (OperationCanceledException) { }
        }
        string saveDirectory = Path.Combine(Path.GetTempPath(), "runic-portal-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(saveDirectory);
        try
        {
            string path = Path.Combine(saveDirectory, "original.txt");
            await File.WriteAllTextAsync(path, "original");
            var backend = new NativePickerBackend(owner, new PortalFilePicker(owner, new FakeTransport(new(0, [new Uri(path).AbsoluteUri]))));
            Check(backend.SupportsAtomicReplace && new PortalApplication().CreateFileDialogs(owner).SupportsAtomicReplace, "portal dialogs report atomic save support");
            await using (var lifetime = new PresentationLifetime(() => owner.IsAvailable, owner.Generation))
                Check(new PresentationFiles(lifetime, backend).GetSnapshot().Statuses["platform.files.save"] is CapabilityStatus.Available, "facade reports Linux portal save as available");
            var selected = await backend.SaveFileAsync(new SaveFileOptions("original.txt"));
            Check(selected is PickerResult<ISaveFileLease>.Selected, "portal save destination selected");
            await using (var lease = ((PickerResult<ISaveFileLease>.Selected)selected).Value)
            {
                var begun = await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace);
                Check(begun is PlatformResult<IFileWriteTransaction>.Success, "host-path portal selection stages a sibling");
                await using var transaction = ((PlatformResult<IFileWriteTransaction>.Success)begun).Value;
                await transaction.Content.WriteAsync("saved"u8.ToArray());
                Check(await transaction.CommitAsync() is FileCommitResult.Committed, "host-path portal save commits");
            }
            Check(await File.ReadAllTextAsync(path) == "saved" && Directory.GetFiles(saveDirectory).Length == 1, "atomic save replaced the file and left no staging");
        }
        finally { Directory.Delete(saveDirectory, recursive: true); }

        // Open and Save As as documents: read, save back repeatedly and reopen through the portal's host path.
        string documentDirectory = Path.Combine(Path.GetTempPath(), "runic-portal-document-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(documentDirectory);
        try
        {
            string path = Path.Combine(documentDirectory, "notes.txt");
            await File.WriteAllTextAsync(path, "opened");
            var backend = new NativePickerBackend(owner, new PortalFilePicker(owner, new FakeTransport(new(0, [new Uri(path).AbsoluteUri]))));
            Check(backend.SupportsDocumentAccess, "portal dialogs implement documents");
            var opened = await backend.OpenDocumentAsync(new OpenFileOptions());
            await using (var document = (opened as PickerResult<IDocumentFileLease>.Selected)?.Value ?? throw new InvalidOperationException("open: " + opened))
            {
                Check(document.LocalPath == path && await ReadAsync(document) == "opened", "the opened document reads the selection");
                for (int index = 1; index <= 3; index++)
                    Check(await WriteAsync(document, $"save {index}") is FileCommitResult.Committed && await ReadAsync(document) == $"save {index}", $"document save {index}");
            }
            var saved = await backend.SaveDocumentAsync(new SaveFileOptions("notes.txt"));
            await using (var document = (saved as PickerResult<IDocumentFileLease>.Selected)?.Value ?? throw new InvalidOperationException("save: " + saved))
                Check(await ReadAsync(document) == "save 3" && await WriteAsync(document, "save as") is FileCommitResult.Committed, "Save As over an existing file");
            Check(await File.ReadAllTextAsync(path) == "save as" && Directory.GetFiles(documentDirectory).Length == 1, "the document is replaced without staging left");
        }
        finally { Directory.Delete(documentDirectory, recursive: true); }

        // Classification: a document is below the Documents.GetMountPoint mount (or the
        // Flatpak alias) and on FUSE. Nothing else may skip host symlink resolution.
        string? queriedMount = "/run/user/1000/doc";
        int mountQueries = 0;
        var fuse = new DocumentPortalPaths(_ => { mountQueries++; return ValueTask.FromResult<string?>(queriedMount); }, _ => true);
        var plain = new DocumentPortalPaths(_ => { mountQueries++; return ValueTask.FromResult<string?>(queriedMount); }, _ => false);
        Check(await fuse.ClassifyAsync("/run/user/1000/doc/abc123/notes.txt", default) == NativeFileLocation.DocumentPortal, "document below the queried mount");
        Check(await fuse.ClassifyAsync("/run/user/1000/doc/by-app/org.example.App/abc123/notes.txt", default) == NativeFileLocation.DocumentPortal, "per-application document view");
        Check(await fuse.ClassifyAsync("/run/flatpak/doc/abc123/notes.txt", default) == NativeFileLocation.DocumentPortal, "Flatpak document alias");
        Check(await fuse.ClassifyAsync("/run/user/1000/document/abc123/notes.txt", default) == NativeFileLocation.Host, "a sibling prefix is not the mount");
        Check(await fuse.ClassifyAsync("/run/user/1000/doc/notes.txt", default) == NativeFileLocation.Host, "the mount root holds no documents");
        Check(await fuse.ClassifyAsync("/home/user/notes.txt", default) == NativeFileLocation.Host, "FUSE outside the mount is a host path");
        mountQueries = 0;
        Check(await plain.ClassifyAsync("/run/user/1000/doc/abc123/notes.txt", default) == NativeFileLocation.Host && mountQueries == 0,
            "a non-FUSE directory is a host path without a portal query");
        queriedMount = null;
        Check(await fuse.ClassifyAsync("/run/user/1000/doc/abc123/notes.txt", default) == NativeFileLocation.Host, "an unconfirmed mount is a host path");
        queriedMount = "/srv/runtime/doc/";
        Check(await fuse.ClassifyAsync("/srv/runtime/doc/abc123/notes.txt", default) == NativeFileLocation.DocumentPortal, "an atypical XDG_RUNTIME_DIR mount comes from the portal");
        var documentPicker = new PortalFilePicker(owner, new FakeTransport(new(0, ["file:///srv/runtime/doc/abc123/notes.txt"])), documents: fuse);
        Check(await documentPicker.SelectAsync(true, "notes.txt", default) is { AllowsSiblingReplacement: true, Location: NativeFileLocation.DocumentPortal }, "save selection carries its document location");
        Check(await documentPicker.SelectAsync(false, null, default) is { AllowsSiblingReplacement: true, Location: NativeFileLocation.DocumentPortal }, "open selection carries its document location");
        Console.WriteLine("PASS portal ownership, cancellation, local URI and access-grant checks.");
    }

    private static async Task<string> ReadAsync(IDocumentFileLease document)
    {
        var read = await document.OpenReadAsync();
        if (read is not PlatformResult<Stream>.Success(var stream)) throw new InvalidOperationException("read: " + read);
        await using (stream)
        using (var reader = new StreamReader(stream))
            return await reader.ReadToEndAsync();
    }

    private static async Task<FileCommitResult> WriteAsync(ISaveFileLease lease, string text)
    {
        var begun = await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace);
        if (begun is not PlatformResult<IFileWriteTransaction>.Success(var transaction)) throw new InvalidOperationException("staging: " + begun);
        await using (transaction)
        {
            await transaction.Content.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));
            return await transaction.CommitAsync();
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
