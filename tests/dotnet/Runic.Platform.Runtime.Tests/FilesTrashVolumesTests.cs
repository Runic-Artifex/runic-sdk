using System.Collections.Immutable;
using Runic.Platform;
using Runic.Platform.MacOS;
using Runic.Platform.Runtime;
using Runic.Platform.Windows;
using TUnit.Core;

namespace Runic.Platform.Runtime.Tests;

// The 0.11 additions for files, trash and volumes (B14): MIME filters, the chosen filter, the macOS filter pop-up option,
// multiple directory selection, and the default members of IDesktopTrash and IDesktopVolumes.
internal sealed class FilesTrashVolumesTests
{
    private static readonly FileDialogFilter Images = new("Images", ["png", "jpg"]) { MimeTypes = ["image/*"] };
    private static readonly FileDialogFilter Text = new("Text", ["txt"]);

    [Test, DisplayName("FILTERS: MIME types are validated, optional and part of equality")]
    public void MimeTypes()
    {
        ImmutableArray<string> png = ["png"];
        var filter = new FileDialogFilter("Pictures", png);
        Check(filter.MimeTypes.IsEmpty && (filter with { MimeTypes = default }).MimeTypes.IsEmpty, "Empty by default.");
        Check(filter == new FileDialogFilter("Pictures", png) && filter == (filter with { MimeTypes = [] }), "Filters without MIME types stay equal.");
        var typed = filter with { MimeTypes = ["image/png", "image/*", "application/vnd.oasis.opendocument.text", "Image/X-Icon"] };
        Check(typed.MimeTypes.Length == 4 && typed != filter, "Valid types are kept.");
        foreach (string invalid in new[] { "", "image", "/png", "image/", "*/*", "image/png;q=1", "image /png", "image/p ng", "image/pn\0g", "-x/y", "a/b/c" })
            Throws<ArgumentException>(() => _ = filter with { MimeTypes = [invalid] }, invalid);
        Throws<ArgumentException>(() => _ = filter with { MimeTypes = [null!] }, "null");
        Throws<ArgumentException>(() => _ = filter with { MimeTypes = [new string('a', 128) + "/b"] }, "a type of 128 characters");
        var (name, extensions) = typed;
        Check(name == "Pictures" && extensions is ["png"], "Deconstruct is unchanged.");

        Check(!new OpenFileOptions().ShowFilterChoice && !new SaveFileOptions("a.txt").ShowFilterChoice, "No pop-up by default.");
        Check(new OpenFileOptions { ShowFilterChoice = true } != new OpenFileOptions(), "ShowFilterChoice is part of equality.");
    }

    [Test, DisplayName("FILTERS: the chosen filter and the pop-up option pass through the backend and presentation")]
    public async Task ChosenFilterAsync()
    {
        string directory = CreateDirectory(out string first, out string second);
        try
        {
            var picker = new Picker { Files = [first], Chosen = Text };
            var backend = new NativePickerBackend(new Owner(), picker);
            var options = new OpenFileOptions { Filters = [Images, Text], ShowFilterChoice = true };
            var opened = await backend.OpenFileAsync(options);
            Check(picker.Request is { ShowFilterChoice: true, Filters: [_, _] }, "The pop-up option reaches the picker.");
            Check(opened is PickerResult<IReadFileLease>.Selected { SelectedFilter: var chosen } && chosen == Text, $"Open: {opened}");
            if (opened is PickerResult<IReadFileLease>.Selected(var lease)) await lease.DisposeAsync();

            // A filter the request did not offer is not reported.
            picker.Chosen = new FileDialogFilter("Other", ["bin"]);
            var other = await backend.OpenFileAsync(options);
            Check(other is PickerResult<IReadFileLease>.Selected { SelectedFilter: null }, $"Foreign filter: {other}");
            if (other is PickerResult<IReadFileLease>.Selected(var otherLease)) await otherLease.DisposeAsync();

            picker.Chosen = Images;
            picker.Files = [first, second];
            var many = await backend.OpenFilesAsync(options);
            Check(many is PickerResult<IReadFileLeaseList>.Selected { SelectedFilter: var manyFilter } && manyFilter == Images, $"Open several: {many}");
            if (many is PickerResult<IReadFileLeaseList>.Selected(var leases)) await leases.DisposeAsync();

            picker.Files = [Path.Combine(directory, "saved.txt")];
            picker.Chosen = Text;
            var saved = await backend.SaveFileAsync(new SaveFileOptions("saved.txt") { Filters = [Images, Text], ShowFilterChoice = true });
            Check(picker.Request is { Save: true, ShowFilterChoice: true }, "The save request asks for the pop-up.");
            Check(saved is PickerResult<ISaveFileLease>.Selected { SelectedFilter: var savedFilter } && savedFilter == Text, $"Save: {saved}");
            if (saved is PickerResult<ISaveFileLease>.Selected(var saveLease)) await saveLease.DisposeAsync();

            // The presentation keeps the chosen filter when it wraps the lease.
            picker.Files = [first];
            await using var lifetime = new PresentationLifetime();
            lifetime.AttachTestOwner();
            var files = new PresentationFiles(lifetime, backend);
            var presented = await files.OpenFileAsync(options);
            Check(presented is PickerResult<IReadFileLease>.Selected { SelectedFilter: var presentedFilter } && presentedFilter == Text, $"Presented: {presented}");
            if (presented is PickerResult<IReadFileLease>.Selected(var presentedLease)) await presentedLease.DisposeAsync();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("DIRECTORIES: several directories are selected all or none, owned by the presentation")]
    public async Task MultipleDirectoriesAsync()
    {
        string directory = CreateDirectory(out string first, out _);
        string a = Directory.CreateDirectory(Path.Combine(directory, "a")).FullName, b = Directory.CreateDirectory(Path.Combine(directory, "b")).FullName;
        try
        {
            var picker = new Picker { Directories = [a, b] };
            var backend = new NativePickerBackend(new Owner(), picker);
            Check(backend.SupportsMultipleDirectorySelection, "The backend reports the picker's support.");
            var result = await backend.OpenDirectoriesAsync(new OpenDirectoryOptions { InitialDirectory = directory });
            Check(picker.DirectoryRequest == directory, "The initial folder reaches the picker.");
            if (result is not PickerResult<IDirectoryLeaseList>.Selected(var list)) throw new InvalidOperationException($"No selection: {result}");
            Check(list.Count == 2 && list.Select(static lease => lease.DisplayName).SequenceEqual(["a", "b"]) && list[1].LocalPath == b, "Every directory in order.");
            await list.DisposeAsync();
            await list.DisposeAsync();
            Check(picker.Released == 2, "Disposing the list releases each directory once.");
            Throws<ObjectDisposedException>(() => _ = list[0].LocalPath, "a lease of a disposed list");

            picker.Released = 0;
            picker.Directories = [a, first];
            Check(await backend.OpenDirectoriesAsync(new()) is PickerResult<IDirectoryLeaseList>.Failed { Code: PlatformFailureCode.InvalidData } && picker.Released == 2,
                "A selection with a file releases every directory.");
            picker.Released = 0;
            picker.Directories = [a, Path.Combine(directory, "missing")];
            Check(await backend.OpenDirectoriesAsync(new()) is PickerResult<IDirectoryLeaseList>.Failed { Code: PlatformFailureCode.IOError } && picker.Released == 2,
                "A directory removed after selection releases every directory.");
            picker.Directories = [];
            Check(await backend.OpenDirectoriesAsync(new()) is PickerResult<IDirectoryLeaseList>.Dismissed, "An empty selection is a dismissal.");

            // The presentation owns the list and reports the capability.
            picker.Released = 0;
            picker.Directories = [a, b];
            var lifetime = new PresentationLifetime();
            lifetime.AttachTestOwner();
            var files = new PresentationFiles(lifetime, backend);
            Check(files.GetSnapshot().Statuses[PlatformCapabilityIds.DirectoriesOpenMultiple] is CapabilityStatus.Available, "Capability available.");
            var owned = ((PickerResult<IDirectoryLeaseList>.Selected)await files.OpenDirectoriesAsync(new())).Value;
            Check(owned.Count == 2 && owned[0].LocalPath == a, "The presentation's list.");
            await lifetime.DisposeAsync();
            Check(picker.Released == 2, "Closing the presentation releases the list.");
            Throws<ObjectDisposedException>(() => _ = owned[1].LocalPath, "a lease after the presentation closed");

            // A picker with single directory selection only.
            var single = new NativePickerBackend(new Owner(), new SingleDirectoryPicker(a));
            Check(!single.SupportsMultipleDirectorySelection && single.SupportsDirectorySelection, "Single directory picker.");
            Check(await single.OpenDirectoriesAsync(new()) is PickerResult<IDirectoryLeaseList>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
                "Multiple selection is unavailable.");
            await using (var one = ((PickerResult<IDirectoryLease>.Selected)await single.OpenDirectoryAsync(new())).Value)
                Check(one.LocalPath == a, "The single selection still works.");
            await using var singleLifetime = new PresentationLifetime();
            singleLifetime.AttachTestOwner();
            var singleFiles = new PresentationFiles(singleLifetime, single);
            Check(singleFiles.GetSnapshot().Statuses[PlatformCapabilityIds.DirectoriesOpenMultiple] is CapabilityStatus.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
                && await singleFiles.OpenDirectoriesAsync(new()) is PickerResult<IDirectoryLeaseList>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
                "The presentation reports multiple directory selection unavailable.");

            Check(WindowPlatformServices.CapabilityIds.Contains(PlatformCapabilityIds.DirectoriesOpenMultiple), "The capability is listed.");
            Check(await WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerClosed).FileDialogs.OpenDirectoriesAsync(new())
                is PickerResult<IDirectoryLeaseList>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed }, "Unavailable services report their reason.");
            Check(await ((IFileDialogs)new MinimalDialogs()).OpenDirectoriesAsync(new()) is PickerResult<IDirectoryLeaseList>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
                "The default implementation reports the backend unavailable.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("FILTERS: Windows file type index and macOS content types")]
    public void NativeMappings()
    {
        ImmutableArray<FileDialogFilter> filters = [Images, Text];
        Check(WindowsFilePicker.ChosenFilter(filters, 2) == Text && WindowsFilePicker.ChosenFilter(filters, 1) == Images, "Windows indexes are one-based.");
        Check(WindowsFilePicker.ChosenFilter(filters, 0) is null && WindowsFilePicker.ChosenFilter(filters, 3) is null
            && WindowsFilePicker.ChosenFilter([], 1) is null, "Windows reports no filter outside the list.");

        var typed = new FileDialogFilter("Documents", ["ODT"]) { MimeTypes = ["application/vnd.oasis.opendocument.text", "text/*", "application/*", "Image/PNG"] };
        Check(MacOsFilePicker.AllowedTypes([typed]) is
            [
                { Source: MacOsFilePicker.ContentTypeSource.Extension, Value: "odt" },
                { Source: MacOsFilePicker.ContentTypeSource.MimeType, Value: "application/vnd.oasis.opendocument.text" },
                { Source: MacOsFilePicker.ContentTypeSource.Identifier, Value: "public.text" },
                { Source: MacOsFilePicker.ContentTypeSource.MimeType, Value: "image/png" },
            ], "macOS maps extensions, MIME types and top-level wildcards; application/* has no identifier.");
        Check(MacOsFilePicker.AllowedTypes(filters, showsChoice: false)!.Length == 4, "Without a pop-up, the union of every filter.");
        Check(MacOsFilePicker.AllowedTypes(filters, showsChoice: true)!.Length == 3, "With a pop-up, the first filter's types.");
        Check(MacOsFilePicker.AllowedTypes([Text, new FileDialogFilter("All", ["*"])], showsChoice: false) is null
            && MacOsFilePicker.AllowedTypes([], showsChoice: true) is null, "Every file with an all-files filter or none.");
        Check(MacOsFilePicker.WildcardIdentifier("video") == "public.movie" && MacOsFilePicker.WildcardIdentifier("x-custom") is null, "Wildcards.");
    }

    [Test, DisplayName("TRASH: MoveAllToTrashAsync moves each item, reports progress and stops on cancellation")]
    public async Task TrashBatchAsync()
    {
        string root = Path.GetTempPath();
        string[] paths = [Path.Combine(root, "a"), Path.Combine(root, "b"), Path.Combine(root, "c")];
        var trash = new RecordingTrash { Missing = paths[1] };
        IDesktopTrash service = trash;
        var progress = new List<DesktopTrashProgress>();
        var results = await service.MoveAllToTrashAsync(paths, new SynchronousProgress(progress.Add));
        Check(trash.Moved.SequenceEqual(paths), "Each path is moved in order.");
        Check(results is [PlatformResult<PlatformUnit>.Success, PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.NotFound }, PlatformResult<PlatformUnit>.Success],
            "One result per path; a failure does not stop the others.");
        Check(progress.SequenceEqual([new DesktopTrashProgress(1, 3), new(2, 3), new(3, 3)]), "Progress after each item.");
        Check((await service.MoveAllToTrashAsync([])).IsEmpty, "No paths, no results.");

        trash.Moved.Clear();
        await ThrowsAsync<ArgumentException>(() => service.MoveAllToTrashAsync([paths[0], "relative"]).AsTask(), "a relative path");
        await ThrowsAsync<ArgumentNullException>(() => service.MoveAllToTrashAsync(null!).AsTask(), "null paths");
        Check(trash.Moved.Count == 0, "An invalid path moves nothing.");

        using var cancellation = new CancellationTokenSource();
        trash.OnMove = path => { if (path == paths[1]) cancellation.Cancel(); };
        progress.Clear();
        await ThrowsAsync<OperationCanceledException>(() => service.MoveAllToTrashAsync(paths, new SynchronousProgress(progress.Add), cancellation.Token).AsTask(),
            "cancellation");
        Check(trash.Moved.SequenceEqual(paths[..2]) && progress.Count == 2, "The move in progress completes; the next one does not start.");

        Check(await service.ListAsync() is PlatformResult<ImmutableArray<DesktopTrashItem>>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
            && await service.RestoreAsync("item") is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
            && await service.EmptyAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "List, restore and empty default to unavailable.");
        await ThrowsAsync<ArgumentException>(() => service.RestoreAsync("").AsTask(), "an empty item ID");

        IDesktopTrash unavailable = DesktopTrash.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        Check(await unavailable.ListAsync() is PlatformResult<ImmutableArray<DesktopTrashItem>>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured }
            && await unavailable.RestoreAsync("item") is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured }
            && await unavailable.EmptyAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured }
            && (await unavailable.MoveAllToTrashAsync([paths[0]])) is [PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured }],
            "The unavailable trash reports its reason.");

        var item = new DesktopTrashItem("id", paths[0]) { IsDirectory = true, Size = 10 };
        Check(item.DeletedAt is null && item == new DesktopTrashItem("id", paths[0]) { IsDirectory = true, Size = 10 }, "Trash items are values.");
    }

    [Test, DisplayName("VOLUMES: mount options, unlock and the new volume properties")]
    public async Task VolumeDefaultsAsync()
    {
        var volumes = new RecordingVolumes();
        IDesktopVolumes service = volumes;
        Check(await service.MountAsync("v", new DesktopVolumeMountOptions(), default) is PlatformResult<DesktopVolume>.Success && volumes.Mounted == 1,
            "Default options forward to MountAsync(id, token).");
        Check(await service.MountAsync("v", new DesktopVolumeMountOptions { Options = [] }, default) is PlatformResult<DesktopVolume>.Success && volumes.Mounted == 2,
            "An empty option list is the default.");
        Check(await service.MountAsync("v", new DesktopVolumeMountOptions { ReadOnly = true }, default) is PlatformResult<DesktopVolume>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
            && await service.MountAsync("v", new DesktopVolumeMountOptions { Options = ["noexec"] }, default) is PlatformResult<DesktopVolume>.Unavailable
            && volumes.Mounted == 2, "Other options are unavailable without an implementation.");
        // Two arguments keep binding to the 0.10 overload.
        Check(await service.MountAsync("v", default) is PlatformResult<DesktopVolume>.Success && volumes.Mounted == 3, "MountAsync(id, default).");
        await ThrowsAsync<ArgumentNullException>(() => service.MountAsync("v", null!, default).AsTask(), "null options");
        foreach (string invalid in new[] { "", "a,b", "a b", "a\0", "\t" })
            Throws<ArgumentException>(() => _ = new DesktopVolumeMountOptions { Options = [invalid] }, $"option '{invalid}'");

        Check(await service.UnlockAsync("v") is PlatformResult<DesktopVolume>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
            && await service.UnlockAsync("v", "secret".AsMemory(), default) is PlatformResult<DesktopVolume>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "Unlock defaults to unavailable.");
        await ThrowsAsync<ArgumentException>(() => service.UnlockAsync("v", ReadOnlyMemory<char>.Empty, default).AsTask(), "an empty passphrase");
        await ThrowsAsync<ArgumentException>(() => service.UnlockAsync("").AsTask(), "an empty ID");

        await using var unavailable = DesktopVolumes.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        Check(await unavailable.MountAsync("v", new DesktopVolumeMountOptions(), default) is PlatformResult<DesktopVolume>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured }
            && await unavailable.UnlockAsync("v") is PlatformResult<DesktopVolume>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured }
            && await unavailable.UnlockAsync("v", "secret".AsMemory(), default) is PlatformResult<DesktopVolume>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured },
            "The unavailable volumes report their reason.");

        var volume = new DesktopVolume("id", "name");
        Check(!volume.IsEncrypted && volume.UnlockMethod == DesktopVolumeUnlockMethod.None, "Not encrypted by default.");
        Check(volume with { IsEncrypted = true, UnlockMethod = DesktopVolumeUnlockMethod.Passphrase } != volume, "Part of equality, so a watch reports a change.");
        var (id, name) = volume;
        Check(id == "id" && name == "name", "Deconstruct is unchanged.");
    }

    private static string CreateDirectory(out string first, out string second)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"runic-b14-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        first = Path.Combine(directory, "first.png");
        second = Path.Combine(directory, "second.txt");
        File.WriteAllText(first, "first");
        File.WriteAllText(second, "second");
        return directory;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}: {message}");
    }
    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}: {message}");
    }

    private sealed class SynchronousProgress(Action<DesktopTrashProgress> report) : IProgress<DesktopTrashProgress>
    {
        public void Report(DesktopTrashProgress value) => report(value);
    }

    private sealed class Owner : INativeWindowOwner
    {
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => true;
        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // Returns Files or Directories, each with an access grant that counts its release; the first file carries Chosen.
    private sealed class Picker : INativeFilePicker, INativeDirectoryPicker
    {
        internal string[] Files = [], Directories = [];
        internal FileDialogFilter? Chosen;
        internal NativeFileDialogRequest? Request;
        internal string? DirectoryRequest;
        internal int Released;
        public bool SupportsMultipleSelection => true;
        public bool SupportsMultipleDirectorySelection => true;
        public ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult<IReadOnlyList<NativeFileSelection>?>(
                [.. Files.Select((path, index) => new NativeFileSelection(path, new Access(this), true) { SelectedFilter = index == 0 ? Chosen : null })]);
        }
        public ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(string? initialDirectory, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<IReadOnlyList<NativeDirectorySelection>?> SelectDirectoriesAsync(string? initialDirectory, CancellationToken cancellationToken)
        {
            DirectoryRequest = initialDirectory;
            return ValueTask.FromResult<IReadOnlyList<NativeDirectorySelection>?>([.. Directories.Select(path => new NativeDirectorySelection(path, new Access(this)))]);
        }
        private sealed class Access(Picker picker) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { Interlocked.Increment(ref picker.Released); return ValueTask.CompletedTask; }
        }
    }

    // A 0.10 directory picker: single selection only.
    private sealed class SingleDirectoryPicker(string path) : INativeFilePicker, INativeDirectoryPicker
    {
        public ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(string? initialDirectory, CancellationToken cancellationToken) =>
            ValueTask.FromResult<NativeDirectorySelection?>(new(path, null));
    }

    private sealed class MinimalDialogs : IFileDialogs
    {
        public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    // A 0.10 trash: MoveToTrashAsync only.
    private sealed class RecordingTrash : IDesktopTrash
    {
        internal readonly List<string> Moved = [];
        internal string? Missing;
        internal Action<string>? OnMove;
        public ValueTask<PlatformResult<PlatformUnit>> MoveToTrashAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Moved.Add(path);
            OnMove?.Invoke(path);
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(path == Missing
                ? new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.NotFound) : new PlatformResult<PlatformUnit>.Success(default));
        }
    }

    // 0.10 volumes: MountAsync(id, token) only.
    private sealed class RecordingVolumes : IDesktopVolumes
    {
        internal int Mounted;
        public ValueTask<PlatformResult<ImmutableArray<DesktopVolume>>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<PlatformResult<DesktopVolumeChange>> WatchAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlatformResult<DesktopVolume>> MountAsync(string volumeId, CancellationToken cancellationToken = default)
        {
            Mounted++;
            return ValueTask.FromResult<PlatformResult<DesktopVolume>>(new PlatformResult<DesktopVolume>.Success(new(volumeId, "Volume")));
        }
        public ValueTask<PlatformResult<PlatformUnit>> UnmountAsync(string volumeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<PlatformResult<PlatformUnit>> EjectAsync(string volumeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
