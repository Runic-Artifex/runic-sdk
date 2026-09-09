using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>Why a capability cannot currently be used.</summary>
public enum UnavailableReason
{
    /// <summary>No provider was explicitly registered.</summary>
    ProviderNotConfigured,
    /// <summary>No verified native presentation owner exists.</summary>
    OwnerUnavailable,
    /// <summary>The presentation is shutting down or has closed.</summary>
    OwnerClosed,
    /// <summary>The native backend cannot serve this environment.</summary>
    BackendUnavailable,
    /// <summary>The acquired access cannot stage and atomically replace the destination.</summary>
    AtomicReplaceUnavailable
}

/// <summary>A classified operational failure, without exposing paths or native handles.</summary>
public enum FailureCode
{
    /// <summary>The operation was denied access.</summary>
    PermissionDenied,
    /// <summary>A conflicting operation or another process holds the resource.</summary>
    ResourceBusy,
    /// <summary>The supplied or native data is invalid.</summary>
    InvalidData,
    /// <summary>The content exceeds the requested limit.</summary>
    TooLarge,
    /// <summary>An input/output operation failed.</summary>
    IoError,
    /// <summary>The destination changed since acquisition.</summary>
    Conflict,
    /// <summary>The user dismissed a native interactive operation.</summary>
    UserDismissed
}

/// <summary>Ownership requirements for native dialogs. This preview supports owned dialogs only.</summary>
public enum OwnerPolicy
{
    /// <summary>Require the verified owner of this presentation.</summary>
    RequireOwner
}

/// <summary>Required durability behavior for a save transaction.</summary>
public enum FileWritePolicy
{
    /// <summary>Stage beside the target and replace atomically; otherwise report unavailable before modifying it.</summary>
    RequireAtomicReplace
}

/// <summary>The successful result of an operation without a return value.</summary>
public readonly record struct Unit;

/// <summary>A service outcome. Cancellation is represented by an OperationCanceledException.</summary>
public abstract record PlatformResult<T>
{
    private PlatformResult() { }
    /// <summary>The operation succeeded.</summary>
    /// <param name="Value">The returned value.</param>
    public sealed record Success(T Value) : PlatformResult<T>;
    /// <summary>The service was unavailable.</summary>
    /// <param name="Reason">The unavailability reason.</param>
    public sealed record Unavailable(UnavailableReason Reason) : PlatformResult<T>;
    /// <summary>The operation failed.</summary>
    /// <param name="Code">The classified failure.</param>
    public sealed record Failed(FailureCode Code) : PlatformResult<T>;
}

/// <summary>A dialog outcome. User dismissal is distinct from cancellation, unavailability and failure.</summary>
public abstract record PickerResult<T> where T : IAsyncDisposable
{
    private PickerResult() { }
    /// <summary>The user selected a resource whose access must be disposed.</summary>
    public sealed record Selected : PickerResult<T>
    {
        /// <summary>Creates a selection carrying acquired access.</summary>
        public Selected(T value) { ArgumentNullException.ThrowIfNull(value); Value = value; }
        /// <summary>The acquired resource.</summary>
        public T Value { get; }
    }
    /// <summary>The user dismissed the dialog without selecting a resource.</summary>
    public sealed record Dismissed : PickerResult<T>;
    /// <summary>The dialog could not be shown.</summary>
    /// <param name="Reason">The unavailability reason.</param>
    public sealed record Unavailable(UnavailableReason Reason) : PickerResult<T>;
    /// <summary>The dialog or acquisition failed.</summary>
    /// <param name="Code">The classified failure.</param>
    public sealed record Failed(FailureCode Code) : PickerResult<T>;
}

/// <summary>The observed outcome after an atomic replacement attempt. An uncertain commit must not be retried automatically.</summary>
public abstract record FileCommitResult
{
    private FileCommitResult() { }
    /// <summary>The replacement completed.</summary>
    public sealed record Committed : FileCommitResult;
    /// <summary>The replacement did not occur.</summary>
    /// <param name="Code">The classified failure.</param>
    public sealed record NotCommitted(FailureCode Code) : FileCommitResult;
    /// <summary>The replacement may have occurred; inspect the destination before deciding what to do.</summary>
    /// <param name="Code">The failure that prevented determining the outcome.</param>
    public sealed record CommitUnknown(FailureCode Code) : FileCommitResult;
}

/// <summary>Options for selecting one existing file.</summary>
/// <param name="OwnerPolicy">The required dialog ownership.</param>
public sealed record OpenFileOptions(OwnerPolicy OwnerPolicy = OwnerPolicy.RequireOwner);
/// <summary>Options for selecting one save destination.</summary>
/// <param name="SuggestedName">A filename without path separators.</param>
/// <param name="OwnerPolicy">The required dialog ownership.</param>
public sealed record SaveFileOptions(string SuggestedName, OwnerPolicy OwnerPolicy = OwnerPolicy.RequireOwner);

/// <summary>Presentation-scoped selection of file access. Leases, streams and native access remain in C#.</summary>
public interface IFileDialogs
{
    /// <summary>Selects and acquires a single-use read lease.</summary>
    ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default);
    /// <summary>Selects and acquires a save destination.</summary>
    ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Owns acquired read access until disposed or its presentation closes.</summary>
public interface IReadFileLease : IAsyncDisposable
{
    /// <summary>A display filename, without a filesystem path.</summary>
    string DisplayName { get; }
    /// <summary>Consumes the lease once and returns its acquired stream. The lease retains ownership of the stream.</summary>
    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Owns an acquired save destination and its access grant.</summary>
public interface ISaveFileLease : IAsyncDisposable
{
    /// <summary>A display filename, without a filesystem path.</summary>
    string DisplayName { get; }
    /// <summary>Begins one staged write, or reports unavailable before modifying the destination.</summary>
    ValueTask<PlatformResult<IFileWriteTransaction>> BeginWriteAsync(FileWritePolicy policy, CancellationToken cancellationToken = default);
}

/// <summary>A single-consumption staged write. Disposal removes uncommitted staging data.</summary>
public interface IFileWriteTransaction : IAsyncDisposable
{
    /// <summary>The writable staging stream owned by this transaction.</summary>
    Stream Content { get; }
    /// <summary>Checks for conflicts and commits once. After native replacement begins, returns the actual outcome despite late cancellation.</summary>
    ValueTask<FileCommitResult> CommitAsync(CancellationToken cancellationToken = default);
}

/// <summary>Presentation-scoped Unicode text clipboard operations.</summary>
public interface ITextClipboard
{
    /// <summary>Reads bounded text; successful null means no text format and an empty string means empty text.</summary>
    ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default);
    /// <summary>Writes text. Cancellation prevents queued work; an initiated native write returns its actual outcome.</summary>
    ValueTask<PlatformResult<Unit>> WriteTextAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Dispatches synchronous actions on the presentation's UI thread.</summary>
public interface IUiDispatcher
{
    /// <summary>Whether the caller is on the UI thread.</summary>
    bool CheckAccess();
    /// <summary>Runs an action once while the presentation remains valid.</summary>
    ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default);
}

/// <summary>One capability's current availability.</summary>
public abstract record CapabilityStatus
{
    private CapabilityStatus() { }
    /// <summary>The capability is currently available.</summary>
    public sealed record Available : CapabilityStatus;
    /// <summary>The capability is currently unavailable.</summary>
    /// <param name="Reason">The reason it cannot be used.</param>
    public sealed record Unavailable(UnavailableReason Reason) : CapabilityStatus;
}

/// <summary>An immutable capability view; availability may change after capture.</summary>
/// <param name="Generation">The identity of the owning presentation generation.</param>
/// <param name="Statuses">Capability identifiers and their captured status.</param>
public sealed record CapabilitySnapshot(Guid Generation, ImmutableDictionary<string, CapabilityStatus> Statuses);
/// <summary>Reports capabilities without exposing native resources.</summary>
public interface IPlatformCapabilities
{
    /// <summary>Captures the current capability statuses.</summary>
    CapabilitySnapshot GetSnapshot();
}
