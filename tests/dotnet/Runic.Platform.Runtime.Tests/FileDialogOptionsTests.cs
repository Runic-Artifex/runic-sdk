using System.Collections.Immutable;
using Runic.Platform;
using Runic.Platform.MacOS;
using Runic.Platform.Runtime;
using Runic.Platform.Windows;
using TUnit.Core;

// Filters, the initial folder and multiple selection: option validation, what reaches a native picker, and the
// leases of a multiple selection.
internal sealed class FileDialogOptionsTests
{
    private static readonly FileDialogFilter Images = new("Images", ["png", "jpg"]);
    private static readonly FileDialogFilter Everything = new("All files", ["*"]);

    [Test, DisplayName("OPTIONS: filters and the initial folder are checked when they are set")]
    public void Validation()
    {
        Check(new FileDialogFilter("Archives", ["tar.gz", "7z"]).Extensions.Length == 2 && Everything.MatchesAllFiles && !Images.MatchesAllFiles,
            "valid filters");
        foreach (var extensions in new ImmutableArray<string>[] { default, [], [""], [" "], [".png"], ["png."], ["*.png"], ["p?g"], ["a/b"], ["a\\b"],
            ["a;b"], ["a,b"], ["a:b"], ["a b"], ["a\0"], ["[ab]"], ["png", null!] })
            Throws<ArgumentException>(() => _ = new FileDialogFilter("Files", extensions), $"extensions {string.Join('|', extensions.IsDefault ? [] : extensions)}");
        Throws<ArgumentException>(() => _ = new FileDialogFilter(" ", ["png"]), "empty name");
        Throws<ArgumentException>(() => _ = new FileDialogFilter("a\0", ["png"]), "NUL name");
        Throws<ArgumentException>(() => _ = Images with { Extensions = [".png"] }, "with bypassed extension validation");
        Throws<ArgumentException>(() => _ = Images with { Name = "" }, "with bypassed name validation");

        var options = new OpenFileOptions();
        Check(options.Filters.IsEmpty && options.InitialDirectory is null && new SaveFileOptions("a.txt").Filters.IsEmpty, "defaults offer every file");
        Check(new OpenFileOptions { Filters = default }.Filters.IsEmpty, "a default array is empty");
        string folder = Path.GetTempPath();
        Check(new OpenFileOptions { Filters = [Images], InitialDirectory = folder } is { Filters: [var first], InitialDirectory: var set } && first == Images && set == folder,
            "options keep their values");
        Throws<ArgumentException>(() => _ = new OpenFileOptions { InitialDirectory = "relative" }, "relative open folder");
        Throws<ArgumentException>(() => _ = new SaveFileOptions("a.txt") { InitialDirectory = folder + "\0" }, "NUL save folder");
        Throws<ArgumentException>(() => _ = new OpenDirectoryOptions { InitialDirectory = "." }, "relative directory folder");
        Throws<ArgumentException>(() => _ = new SaveFileOptions("a.txt") { Filters = [Images, null!] }, "null filter");
        // #713: a title, null for the platform's own; NUL is rejected when it is set.
        Check(options.Title is null && new OpenDirectoryOptions().Title is null && new SaveFileOptions("a.txt").Title is null, "no title by default");
        Check(new OpenFileOptions { Title = "Import backup" }.Title == "Import backup" && new SaveFileOptions("a.txt") { Title = "" }.Title == "",
            "options keep their title");
        Throws<ArgumentException>(() => _ = new OpenFileOptions { Title = "a\0" }, "NUL open title");
        Throws<ArgumentException>(() => _ = new OpenDirectoryOptions { Title = "a\0" }, "NUL directory title");
        Throws<ArgumentException>(() => _ = new SaveFileOptions("a.txt") { Title = "a\0" }, "NUL save title");
        // The shipped positional shape is unchanged.
        new OpenFileOptions { Filters = [Images] }.Deconstruct(out var policy);
        Check(policy == OwnerPolicy.RequireOwner, "Deconstruct keeps the owner policy");
    }

    [Test, DisplayName("OPTIONS: filters, folder and multiplicity reach the native picker")]
    public async Task RequestsAsync()
    {
        string directory = CreateDirectory(out var first, out var second);
        try
        {
            var picker = new RecordingPicker { Paths = [first] };
            var backend = new NativePickerBackend(new TestOwner(), picker);
            await using ((await backend.OpenFileAsync(new OpenFileOptions { Filters = [Images], InitialDirectory = directory }) as PickerResult<IReadFileLease>.Selected)!.Value) { }
            Check(picker.Request is { Save: false, SuggestedName: null, AllowMultiple: false, Filters: [var filter], InitialDirectory: var folder }
                && filter == Images && folder == directory, "open request");

            picker.Paths = [Path.Combine(directory, "saved.txt")];
            var saved = await backend.SaveFileAsync(new SaveFileOptions("saved.txt") { Filters = [Everything], InitialDirectory = directory });
            if (saved is PickerResult<ISaveFileLease>.Selected(var lease)) await lease.DisposeAsync();
            Check(saved is PickerResult<ISaveFileLease>.Selected && picker.Request is { Save: true, SuggestedName: "saved.txt", AllowMultiple: false, Filters: [var all] }
                && all == Everything && picker.Request.InitialDirectory == directory, "save request");

            picker.Paths = [first, second];
            picker.Released = 0;
            Check(await backend.SaveFileAsync(new("saved.txt")) is PickerResult<ISaveFileLease>.Failed { Code: PlatformFailureCode.InvalidData }
                && picker.Released == 2, "a save that returns two files fails and releases both");
            picker.Released = 0;
            Check(await backend.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Failed { Code: PlatformFailureCode.InvalidData } && picker.Released == 2,
                "a single selection that returns two files fails and releases both");

            await using (var directoryLease = (await backend.OpenDirectoryAsync(new OpenDirectoryOptions { InitialDirectory = directory })
                as PickerResult<IDirectoryLease>.Selected)!.Value) { }
            Check(picker.DirectoryRequest == directory, "directory request");

            // #713: the title reaches every kind of dialog, also a picker that implements the directory request overloads.
            picker.Paths = [first];
            await using ((await backend.OpenFileAsync(new OpenFileOptions { Title = "Import backup" }) as PickerResult<IReadFileLease>.Selected)!.Value) { }
            Check(picker.Request is { Title: "Import backup" }, "open title");
            picker.Paths = [Path.Combine(directory, "saved.txt")];
            if (await backend.SaveFileAsync(new SaveFileOptions("saved.txt") { Title = "Export backup" }) is PickerResult<ISaveFileLease>.Selected(var titled))
                await titled.DisposeAsync();
            Check(picker.Request is { Title: "Export backup", Save: true }, "save title");
            var titledPicker = new TitledDirectoryPicker();
            var titledBackend = new NativePickerBackend(new TestOwner(), titledPicker);
            await using (var titledDirectory = (await titledBackend.OpenDirectoryAsync(new OpenDirectoryOptions { InitialDirectory = directory, Title = "Log folder" })
                as PickerResult<IDirectoryLease>.Selected)!.Value) { }
            Check(titledPicker.Request is { Title: "Log folder", AllowMultiple: false } request && request.InitialDirectory == directory, "directory title");
            if (await titledBackend.OpenDirectoriesAsync(new OpenDirectoryOptions { InitialDirectory = directory, Title = "Folders" })
                is PickerResult<IDirectoryLeaseList>.Selected(var titledList)) await titledList.DisposeAsync();
            Check(titledPicker.Request is { Title: "Folders", AllowMultiple: true }, "directories title");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("OPTIONS: a multiple selection opens every file or none")]
    public async Task MultipleSelectionAsync()
    {
        string directory = CreateDirectory(out var first, out var second);
        try
        {
            var picker = new RecordingPicker { Paths = [first, second] };
            var backend = new NativePickerBackend(new TestOwner(), picker);
            Check(backend.SupportsMultipleSelection, "the backend reports the picker's multiple selection");
            var result = await backend.OpenFilesAsync(new OpenFileOptions { Filters = [Images] });
            Check(picker.Request is { AllowMultiple: true, Save: false }, "multiple request");
            if (result is not PickerResult<IReadFileLeaseList>.Selected(var leases)) throw new InvalidOperationException($"No selection: {result}");
            Check(leases.Count == 2 && leases.Select(static lease => lease.DisplayName).SequenceEqual(["first.png", "second.png"]), "every file in order");
            await using (var stream = await leases[1].OpenReadAsync())
                Check(new StreamReader(stream).ReadToEnd() == "second", "a lease reads its own file");
            await leases.DisposeAsync();
            await leases.DisposeAsync();
            Check(picker.Released == 2, "disposing the list releases every lease once");
            await Throws<ObjectDisposedException>(() => leases[0].OpenReadAsync().AsTask(), "a lease of a disposed list");

            // The second file disappeared after selection: nothing is returned and every access is released.
            picker.Released = 0;
            picker.Paths = [first, Path.Combine(directory, "missing.png"), second];
            Check(await backend.OpenFilesAsync(new()) is PickerResult<IReadFileLeaseList>.Failed { Code: PlatformFailureCode.IOError } && picker.Released == 3,
                "a selection that cannot be opened releases every file");
            picker.Paths = [];
            Check(await backend.OpenFilesAsync(new()) is PickerResult<IReadFileLeaseList>.Dismissed, "an empty selection is a dismissal");
            using (var cancelled = new CancellationTokenSource())
            {
                picker.Released = 0;
                picker.Paths = [first, second];
                picker.OnSelect = cancelled.Cancel;
                await Throws<OperationCanceledException>(() => backend.OpenFilesAsync(new(), cancelled.Token).AsTask(), "late cancellation");
                Check(picker.Released == 2, "a selection that arrives after cancellation is released");
                picker.OnSelect = null;
            }

            // A picker without multiple selection serves one file and reports multiple selection unavailable.
            var legacy = new NativePickerBackend(new TestOwner(), new SingleFilePicker(first));
            Check(!legacy.SupportsMultipleSelection
                && await legacy.OpenFilesAsync(new()) is PickerResult<IReadFileLeaseList>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
                "legacy picker multiple selection");
            await using ((await legacy.OpenFileAsync(new OpenFileOptions { Filters = [Images] }) as PickerResult<IReadFileLease>.Selected)!.Value) { }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("OPTIONS: the presentation owns a multiple selection and reports the capability")]
    public async Task PresentationAsync()
    {
        string directory = CreateDirectory(out var first, out var second);
        try
        {
            var picker = new RecordingPicker { Paths = [first, second] };
            var lifetime = new PresentationLifetime();
            lifetime.AttachTestOwner();
            var files = new PresentationFiles(lifetime, new NativePickerBackend(new TestOwner(), picker));
            Check(files.GetSnapshot().Statuses["platform.files.openMultiple"] is CapabilityStatus.Available, "capability available");
            var leases = ((PickerResult<IReadFileLeaseList>.Selected)await files.OpenFilesAsync(new())).Value;
            Check(leases.Count == 2 && leases[0].DisplayName == "first.png", "presentation list");
            await lifetime.DisposeAsync();
            Check(picker.Released == 2, "closing the presentation releases the list");
            await Throws<ObjectDisposedException>(() => leases[1].OpenReadAsync().AsTask(), "a lease after the presentation closed");

            await using var legacyLifetime = new PresentationLifetime();
            legacyLifetime.AttachTestOwner();
            var legacy = new PresentationFiles(legacyLifetime, new NativePickerBackend(new TestOwner(), new SingleFilePicker(first)));
            Check(legacy.GetSnapshot().Statuses["platform.files.openMultiple"] is CapabilityStatus.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
                && await legacy.OpenFilesAsync(new()) is PickerResult<IReadFileLeaseList>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
                "a backend without multiple selection");
            Check(await WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable).FileDialogs.OpenFilesAsync(new())
                is PickerResult<IReadFileLeaseList>.Unavailable { Reason: PlatformUnavailableReason.OwnerUnavailable }, "unavailable services report their reason");
            Check(await ((IFileDialogs)new MinimalDialogs()).OpenFilesAsync(new()) is PickerResult<IReadFileLeaseList>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
                "the default implementation reports the backend unavailable");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test, DisplayName("OPTIONS: Windows and macOS filter mappings")]
    public void NativeMappings()
    {
        Check(WindowsFilePicker.FilterSpecs([Images, Everything]).SequenceEqual([("Images", "*.png;*.jpg"), ("All files", "*.*")]), "Windows filter specifications");
        Check(WindowsFilePicker.FilterSpecs([]).Length == 0 && WindowsFilePicker.FilterSpecs(default).Length == 0, "no Windows filters");
        Check(MacOsFilePicker.AllowedTypes([Images, new("Pictures", ["PNG", "gif"])], showsChoice: false)?.Select(static type => type.Value).ToArray() is ["png", "jpg", "gif"],
            "macOS allows the union of every filter");
        Check(MacOsFilePicker.AllowedTypes([Images, Everything], showsChoice: false) is null && MacOsFilePicker.AllowedTypes([], showsChoice: false) is null,
            "macOS stays unrestricted with an all-files filter or none");
    }

    private static string CreateDirectory(out string first, out string second)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"runic-dialog-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        first = Path.Combine(directory, "first.png");
        second = Path.Combine(directory, "second.png");
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
    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}: {message}");
    }

    private sealed class TestOwner : INativeWindowOwner
    {
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => true;
        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // Returns Paths, each with an access grant that counts its release.
    private sealed class RecordingPicker : INativeFilePicker, INativeDirectoryPicker
    {
        internal string[] Paths = [];
        internal NativeFileDialogRequest? Request;
        internal string? DirectoryRequest;
        internal int Released;
        internal Action? OnSelect;
        public bool SupportsMultipleSelection => true;
        public ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            OnSelect?.Invoke();
            return ValueTask.FromResult<IReadOnlyList<NativeFileSelection>?>(
                [.. Paths.Select(path => new NativeFileSelection(path, new Access(this), AllowsSiblingReplacement: true))]);
        }
        public ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(string? initialDirectory, CancellationToken cancellationToken)
        {
            DirectoryRequest = initialDirectory;
            return ValueTask.FromResult<NativeDirectorySelection?>(new(initialDirectory!, null));
        }
        private sealed class Access(RecordingPicker picker) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { Interlocked.Increment(ref picker.Released); return ValueTask.CompletedTask; }
        }
    }

    // A directory picker that implements the request overloads, as Runic's own pickers do.
    private sealed class TitledDirectoryPicker : INativeFilePicker, INativeDirectoryPicker
    {
        internal NativeFileDialogRequest? Request;
        public bool SupportsMultipleDirectorySelection => true;
        public ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(string? initialDirectory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The backend called the overload without the request.");
        ValueTask<NativeDirectorySelection?> INativeDirectoryPicker.SelectDirectoryAsync(NativeFileDialogRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult<NativeDirectorySelection?>(new(request.InitialDirectory!, null));
        }
        ValueTask<IReadOnlyList<NativeDirectorySelection>?> INativeDirectoryPicker.SelectDirectoriesAsync(NativeFileDialogRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult<IReadOnlyList<NativeDirectorySelection>?>([new(request.InitialDirectory!, null)]);
        }
    }

    // A picker without multiple selection.
    private sealed class SingleFilePicker(string path) : INativeFilePicker
    {
        public ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<NativeFileSelection>?>([new(path, null, true)]);
    }

    private sealed class MinimalDialogs : IFileDialogs
    {
        public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
