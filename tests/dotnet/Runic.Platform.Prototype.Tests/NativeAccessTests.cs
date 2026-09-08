using Runic.Platform;
using Runic.Platform.Runtime;
using Runic.Application.Platform;
using Runic.Application.Platform.Desktop;

internal static class NativeAccessTests
{
    internal static async Task RunAsync()
    {
        // Exercise actual filesystem failures after a native selection, while the
        // controlled selection source avoids a manual OS dialog in managed CI.
        string directory = Path.Combine(Path.GetTempPath(), $"runic-access-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var access = new Access();
            var selected = new Selection(new(directory, access, false));
            var backend = new NativePickerBackend(new TestOwner(), selected);
            Check(await backend.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Failed { Code: FailureCode.PermissionDenied },
                "A directory cannot be consumed as a read-file lease.");
            Check(access.Releases == 1, "Failed native acquisition leaked access.");
            access = new Access();
            selected.Result = new(Path.Combine(directory, "missing.txt"), access, false);
            Check(await backend.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Failed { Code: FailureCode.IoError },
                "A file removed after selection must produce a stable failure.");
            Check(access.Releases == 1, "Disappeared selection leaked access.");
            access = new Access();
            selected.Result = new(Path.Combine(directory, "new.txt"), access, false);
            var save = (PickerResult<ISaveFileLease>.Selected)await backend.SaveFileAsync(new("new.txt"));
            Check(await save.Value.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace) is PlatformResult<IFileWriteTransaction>.Unavailable,
                "Selected-file access was incorrectly treated as permission to create sibling files.");
            await save.Value.DisposeAsync();
            Check(access.Releases == 1 && Directory.GetFiles(directory).Length == 0, "Unsupported save changed the directory or leaked access.");
            selected.Unavailable = true;
            Check(await backend.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: UnavailableReason.BackendUnavailable },
                "Missing native backend was reported as dismissal.");

            foreach (bool saveOperation in new[] { false, true })
            {
                using var canceled = new CancellationTokenSource();
                var canceledBackend = new NativePickerBackend(new TestOwner(), new CallbackSelection(() =>
                {
                    canceled.Cancel();
                    return null;
                }));
                try
                {
                    if (saveOperation) await canceledBackend.SaveFileAsync(new("new.txt"), canceled.Token);
                    else await canceledBackend.OpenFileAsync(new(), canceled.Token);
                    throw new InvalidOperationException("Canceled native dismissal was reported as dismissal.");
                }
                catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }

                var owner = new TestOwner();
                var staleAccess = new Access();
                var staleBackend = new NativePickerBackend(owner, new CallbackSelection(() =>
                {
                    owner.Generation = Guid.NewGuid();
                    return new(Path.Combine(directory, "stale.txt"), staleAccess, true);
                }));
                bool closed = saveOperation
                    ? await staleBackend.SaveFileAsync(new("stale.txt")) is PickerResult<ISaveFileLease>.Unavailable { Reason: UnavailableReason.OwnerClosed }
                    : await staleBackend.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: UnavailableReason.OwnerClosed };
                Check(closed && staleAccess.Releases == 1 && Directory.GetFiles(directory).Length == 0,
                    "A stale owner selection must release acquired access without touching files.");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class TestOwner : INativePickerOwner
    {
        public Guid Generation { get; set; } = Guid.NewGuid();
        public bool IsAvailable => true;
        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class CallbackSelection(Func<NativeFileSelection?> select) : INativeFilePicker
    {
        public ValueTask<NativeFileSelection?> SelectAsync(bool save, string? suggestedName, CancellationToken cancellationToken)
            => ValueTask.FromResult(select());
    }
    private sealed class Access : IAsyncDisposable
    {
        internal int Releases;
        public ValueTask DisposeAsync() { Releases++; return ValueTask.CompletedTask; }
    }
    private sealed class Selection(NativeFileSelection result) : INativeFilePicker
    {
        internal NativeFileSelection Result = result;
        internal bool Unavailable;
        public ValueTask<NativeFileSelection?> SelectAsync(bool save, string? suggestedName, CancellationToken cancellationToken)
            => Unavailable ? ValueTask.FromException<NativeFileSelection?>(new NativeBackendUnavailableException()) : ValueTask.FromResult<NativeFileSelection?>(Result);
    }
}
