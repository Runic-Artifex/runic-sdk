using System.Runtime.InteropServices;
using System.Text;
using Runic.Platform;
using Runic.Platform.Linux;
using Runic.Platform.Runtime;

internal static class DirectoryTests
{
    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "runic-directories-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, OperatingSystem.IsWindows() ? "space % Ω 文本" : "space % Ω 文本\nline");
        Directory.CreateDirectory(path);
        try
        {
            // A legacy implementer supplies only the original members. The new
            // default method must remain usable without silently picking a file.
            IFileDialogs legacy = new LegacyBackend();
            Check(await legacy.OpenDirectoryAsync(new()) is PickerResult<IDirectoryLease>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable });
            await using (var lifetime = new PresentationLifetime(() => true))
            {
                var files = new PresentationFiles(lifetime, (IPickerBackend)legacy);
                Check(files.GetSnapshot().Statuses["platform.directories.open"] is CapabilityStatus.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable });
                Check(files.GetSnapshot().Statuses["platform.files.open"] is CapabilityStatus.Available);
                Check(await files.OpenDirectoryAsync(new()) is PickerResult<IDirectoryLease>.Unavailable);
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Throws<OperationCanceledException>(() => files.OpenDirectoryAsync(new(), cancelled.Token).AsTask());
                await Throws<ArgumentOutOfRangeException>(() => files.OpenDirectoryAsync(new((OwnerPolicy)10)).AsTask());
            }

            var owner = new Owner();
            var picker = new Picker();
            var backend = new NativePickerBackend(owner, picker);
            Check(backend.SupportsDirectorySelection);
            var access = new Access();
            picker.Select = () => new(path, access);
            var selected = (PickerResult<IDirectoryLease>.Selected)await backend.OpenDirectoryAsync(new());
            Check(selected.Value.LocalPath == path && selected.Value.DisplayName == Path.GetFileName(path));
            Check(!Directory.EnumerateFileSystemEntries(selected.Value.LocalPath).Any() && access.Releases == 0);
            await selected.Value.DisposeAsync();
            await selected.Value.DisposeAsync();
            Check(access.Releases == 1);
            await Throws<ObjectDisposedException>(() => Task.FromResult(selected.Value.LocalPath));

            foreach (string invalid in new[] { Path.Combine(root, "missing"), Path.Combine(root, "ordinary-file") })
            {
                if (invalid.EndsWith("ordinary-file", StringComparison.Ordinal)) await File.WriteAllTextAsync(invalid, "content");
                access = new Access();
                picker.Select = () => new(invalid, access);
                Check(await backend.OpenDirectoryAsync(new()) is PickerResult<IDirectoryLease>.Failed);
                Check(access.Releases == 1);
            }
            picker.Select = () => null;
            Check(await backend.OpenDirectoryAsync(new()) is PickerResult<IDirectoryLease>.Dismissed);
            picker.Select = () => throw new NativeBackendUnavailableException();
            Check(await backend.OpenDirectoryAsync(new()) is PickerResult<IDirectoryLease>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable });

            // Native selection can return after either cancellation or owner
            // replacement. Both must release its acquired access before return.
            using (var cancelled = new CancellationTokenSource())
            {
                access = new Access();
                picker.Select = () => { cancelled.Cancel(); return new(path, access); };
                await Throws<OperationCanceledException>(() => backend.OpenDirectoryAsync(new(), cancelled.Token).AsTask());
                Check(access.Releases == 1);
            }
            access = new Access();
            picker.Select = () => { owner.Generation = Guid.NewGuid(); return new(path, access); };
            Check(await backend.OpenDirectoryAsync(new()) is PickerResult<IDirectoryLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed });
            Check(access.Releases == 1);

            // Directory and file dialogs share one admission slot. Shutdown
            // waits for a late selection's access release even if it ignores its token.
            owner = new Owner();
            picker = new Picker { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            await using var presentation = new PresentationLifetime(() => owner.IsAvailable, owner.Generation);
            var facade = new PresentationFiles(presentation, new NativePickerBackend(owner, picker));
            Check(facade.GetSnapshot().Statuses["platform.directories.open"] is CapabilityStatus.Available);
            var pending = facade.OpenDirectoryAsync(new()).AsTask();
            await picker.Started.Task;
            Check(await facade.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Failed { Code: PlatformFailureCode.ResourceBusy });
            var closed = presentation.DisposeAsync().AsTask();
            Check(!closed.IsCompleted);
            access = new Access { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            picker.Pending.SetResult(new(path, access));
            await access.Releasing.Task;
            Check(!closed.IsCompleted && !pending.IsCompleted);
            access.Release.SetResult();
            Check(await pending is PickerResult<IDirectoryLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed });
            await closed;
            Check(access.Releases == 1 && presentation.GetResourceSnapshot() == (0, 0));

            // A delivered lease is revoked immediately at presentation close;
            // the close operation retains and joins its asynchronous release.
            access = new Access();
            owner = new Owner();
            picker = new Picker { Select = () => new(path, access) };
            await using var owned = new PresentationLifetime(() => true, owner.Generation);
            selected = (PickerResult<IDirectoryLease>.Selected)await new PresentationFiles(owned, new NativePickerBackend(owner, picker)).OpenDirectoryAsync(new());
            await owned.DisposeAsync();
            await Throws<ObjectDisposedException>(() => Task.FromResult(selected.Value.LocalPath));
            Check(access.Releases == 1);

            // Unix filenames are native byte strings. A replacement decoder
            // would select a different resource; reject invalid UTF-8 instead.
            if (!OperatingSystem.IsWindows())
            {
                byte[] exact = [.. Encoding.UTF8.GetBytes(path), 0];
                nint memory = Marshal.AllocHGlobal(exact.Length);
                try
                {
                    Marshal.Copy(exact, 0, memory, exact.Length);
                    Check(LinuxFilePicker.ReadExactPath(memory) == path);
                    Marshal.Copy(new byte[] { (byte)'/', 0xFF, 0 }, 0, memory, 3);
                    await Throws<IOException>(() => Task.FromResult(LinuxFilePicker.ReadExactPath(memory)));
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Directory access contract failed."); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); throw new InvalidOperationException("Expected " + typeof(T).Name); }
        catch (T) { }
    }
    private sealed class Owner : INativePickerOwner
    {
        public Guid Generation { get; set; } = Guid.NewGuid();
        public bool IsAvailable => true;
        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class LegacyBackend : IPickerBackend
    {
        public bool IsAvailable => true;
        public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<PickerResult<IReadFileLease>>(new PickerResult<IReadFileLease>.Dismissed());
        public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<PickerResult<ISaveFileLease>>(new PickerResult<ISaveFileLease>.Dismissed());
    }
    private sealed class Picker : INativeFilePicker, INativeDirectoryPicker
    {
        internal Func<NativeDirectorySelection?> Select = () => null;
        internal TaskCompletionSource<NativeDirectorySelection?>? Pending;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(CancellationToken cancellationToken)
        { Started.TrySetResult(); return Pending is null ? ValueTask.FromResult(Select()) : new(Pending.Task); }
        public ValueTask<NativeFileSelection?> SelectAsync(bool save, string? suggestedName, CancellationToken cancellationToken) => ValueTask.FromResult<NativeFileSelection?>(null);
    }
    private sealed class Access : IAsyncDisposable
    {
        internal int Releases;
        internal TaskCompletionSource? Release;
        internal TaskCompletionSource Releasing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync()
        { Releases++; Releasing.TrySetResult(); if (Release is not null) await Release.Task; }
    }
}
