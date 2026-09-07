using System.Collections.Immutable;

namespace Runic.Platform.Prototype;

// Internal experiment: no package or public compatibility promise yet.
internal enum UnavailableReason { ProviderNotConfigured, OwnerUnavailable, OwnerClosed, BackendUnavailable }
internal enum FailureCode { PermissionDenied, ResourceBusy, InvalidData, TooLarge, IoError, Conflict }
internal enum OwnerPolicy { RequireOwner, AllowUnowned }
internal enum FileWritePolicy { RequireAtomicReplace }
internal readonly record struct Unit;

internal abstract record PlatformResult<T>
{
    private PlatformResult() { }
    internal sealed record Success(T Value) : PlatformResult<T>;
    internal sealed record Unavailable(UnavailableReason Reason) : PlatformResult<T>;
    internal sealed record Failed(FailureCode Code) : PlatformResult<T>;
}

// Dismissal is representable only for UI selection, never for clipboard reads.
internal abstract record PickerResult<T> where T : IAsyncDisposable
{
    private PickerResult() { }
    internal sealed record Selected : PickerResult<T>
    {
        internal Selected(T value) { ArgumentNullException.ThrowIfNull(value); Value = value; }
        internal T Value { get; }
    }
    internal sealed record Dismissed : PickerResult<T>;
    internal sealed record Unavailable(UnavailableReason Reason) : PickerResult<T>;
    internal sealed record Failed(FailureCode Code) : PickerResult<T>;
}

internal abstract record FileCommitResult
{
    private FileCommitResult() { }
    internal sealed record Committed : FileCommitResult;
    internal sealed record NotCommitted(FailureCode Code) : FileCommitResult;
    internal sealed record CommitUnknown(FailureCode Code) : FileCommitResult;
}

internal sealed record OpenFileOptions(OwnerPolicy OwnerPolicy = OwnerPolicy.RequireOwner);
internal sealed record SaveFileOptions(string SuggestedName, OwnerPolicy OwnerPolicy = OwnerPolicy.RequireOwner);

internal interface IFileDialogs
{
    ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default);
    ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default);
}

internal interface IReadFileLease : IAsyncDisposable
{
    string DisplayName { get; }
    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default);
}

internal interface ISaveFileLease : IAsyncDisposable
{
    string DisplayName { get; }
    ValueTask<PlatformResult<IFileWriteTransaction>> BeginWriteAsync(FileWritePolicy policy, CancellationToken cancellationToken = default);
}

internal interface IFileWriteTransaction : IAsyncDisposable
{
    Stream Content { get; }
    ValueTask<FileCommitResult> CommitAsync(CancellationToken cancellationToken = default);
}

internal interface ITextClipboard
{
    ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default);
    ValueTask<PlatformResult<Unit>> WriteTextAsync(string text, CancellationToken cancellationToken = default);
}

internal interface IUiDispatcher
{
    bool CheckAccess();
    ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

internal abstract record CapabilityStatus
{
    private CapabilityStatus() { }
    internal sealed record Available : CapabilityStatus;
    internal sealed record Unavailable(UnavailableReason Reason) : CapabilityStatus;
}

internal sealed record CapabilitySnapshot(Guid Generation, ImmutableDictionary<string, CapabilityStatus> Statuses);
internal interface IPlatformCapabilities { CapabilitySnapshot GetSnapshot(); }

internal interface IPickerBackend : IFileDialogs
{
    bool IsAvailable { get; }
}
