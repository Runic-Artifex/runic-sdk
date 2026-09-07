using Runic.Desktop;

namespace Runic.Platform.Prototype;

internal sealed class DesktopPickerOwner(Func<DesktopWindow?> window)
{
    private DesktopWindow? _identity;
    internal bool IsAvailable => window() is { SupportsNativeDispatch: true } current
        && (_identity is null || ReferenceEquals(current, _identity));

    internal ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
    {
        var current = window();
        if (current is null || !IsAvailable) throw new OwnerClosedException();
        var identity = Interlocked.CompareExchange(ref _identity, current, null) ?? current;
        if (!ReferenceEquals(current, identity)) throw new OwnerClosedException();
        return current.DispatchNativeAsync(action, cancellationToken);
    }
}

internal sealed class NativeBackendUnavailableException : Exception;

internal sealed record NativeFileSelection(string Path, IAsyncDisposable? Access, bool AllowsSiblingReplacement);
internal interface INativeFilePicker
{
    ValueTask<NativeFileSelection?> SelectAsync(bool save, string? suggestedName, CancellationToken cancellationToken);
}

internal sealed class NativePickerBackend(DesktopPickerOwner owner, INativeFilePicker picker) : IPickerBackend
{
    public bool IsAvailable => owner.IsAvailable;

    internal static NativePickerBackend Create(DesktopPickerOwner owner) => new(owner,
        OperatingSystem.IsWindows() ? new WindowsFilePicker(owner) :
        OperatingSystem.IsMacOS() ? new MacOsFilePicker(owner) :
        OperatingSystem.IsLinux() ? new LinuxFilePicker(owner) : throw new PlatformNotSupportedException());

    public async ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
    {
        NativeFileSelection? selection = null;
        try
        {
            selection = await picker.SelectAsync(false, null, cancellationToken).ConfigureAwait(false);
            if (selection is null) return new PickerResult<IReadFileLease>.Dismissed();
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new FileStream(selection.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var lease = new ReadFileLease(Path.GetFileName(selection.Path), stream, selection.Access);
            selection = null;
            return new PickerResult<IReadFileLease>.Selected(lease);
        }
        catch (NativeBackendUnavailableException) { return new PickerResult<IReadFileLease>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (UnauthorizedAccessException) { return new PickerResult<IReadFileLease>.Failed(FailureCode.PermissionDenied); }
        catch (IOException) { return new PickerResult<IReadFileLease>.Failed(FailureCode.IoError); }
        finally { if (selection?.Access is { } access) await access.DisposeAsync().ConfigureAwait(false); }
    }

    public async ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            var selection = await picker.SelectAsync(true, options.SuggestedName, cancellationToken).ConfigureAwait(false);
            if (selection is null) return new PickerResult<ISaveFileLease>.Dismissed();
            // CreateAsync takes ownership of acquired access, including failure paths.
            var lease = await SaveFileLease.CreateAsync(selection.Path,
                selection.AllowsSiblingReplacement ? new LocalAtomicFileReplacement() : new NoAtomicReplacement(),
                selection.Access, cancellationToken).ConfigureAwait(false);
            return new PickerResult<ISaveFileLease>.Selected(lease);
        }
        catch (NativeBackendUnavailableException) { return new PickerResult<ISaveFileLease>.Unavailable(UnavailableReason.BackendUnavailable); }
        catch (UnauthorizedAccessException) { return new PickerResult<ISaveFileLease>.Failed(FailureCode.PermissionDenied); }
        catch (IOException) { return new PickerResult<ISaveFileLease>.Failed(FailureCode.IoError); }
    }

    private sealed class NoAtomicReplacement : IAtomicFileReplacement
    {
        public bool IsSupported => false;
        public ValueTask<FileCommitResult> ReplaceAsync(string stagingPath, string targetPath, bool existed) => throw new NotSupportedException();
    }
}
