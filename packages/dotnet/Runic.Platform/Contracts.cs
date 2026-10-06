using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>Why a capability cannot currently be used.</summary>
public enum PlatformUnavailableReason
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
public enum PlatformFailureCode
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
public readonly record struct PlatformUnit;

/// <summary>A service outcome. Cancellation is represented by an OperationCanceledException.</summary>
public abstract record PlatformResult<T>
{
    private PlatformResult() { }
    /// <summary>The operation succeeded.</summary>
    /// <param name="Value">The returned value.</param>
    public sealed record Success(T Value) : PlatformResult<T>;
    /// <summary>The service was unavailable.</summary>
    /// <param name="Reason">The unavailability reason.</param>
    public sealed record Unavailable(PlatformUnavailableReason Reason) : PlatformResult<T>;
    /// <summary>The operation failed.</summary>
    /// <param name="Code">The classified failure. Branch on this value.</param>
    /// <param name="Diagnostic">Native error detail for logs and support, when the provider observed one.</param>
    public sealed record Failed(PlatformFailureCode Code, PlatformDiagnostic? Diagnostic = null) : PlatformResult<T>;
}

/// <summary>
/// Native error detail attached to a failure for logs and support. It is not
/// intended for display and may be localized by the operating system. Messages come
/// only from operating-system error tables or fixed provider text; free-form text from
/// native services (GError and D-Bus messages, NSError descriptions) is omitted, so a
/// diagnostic contains no paths, file contents or native handles.
/// </summary>
/// <param name="Domain">
/// The native error space: <c>HRESULT</c>, <c>Win32</c>, <c>errno</c>, <c>OSStatus</c>, <c>IOReturn</c>,
/// an <c>NSError</c> or <c>GError</c> domain, a D-Bus error name, or
/// <c>org.freedesktop.portal.Request</c> for a portal response code.
/// </param>
/// <param name="Code">
/// The native numeric code. HRESULT and IOReturn codes keep their unsigned 32-bit value; the code is
/// zero when the domain itself names the error, as D-Bus error names do.
/// </param>
/// <param name="Message">A native description, when one is available.</param>
/// <remarks>
/// The factories describe a code only on the platform that defines it: HRESULT and Win32
/// codes on Windows, <c>errno</c> on other systems. Elsewhere <see cref="Message"/> is null.
/// </remarks>
public sealed record PlatformDiagnostic(string Domain, long Code, string? Message = null)
{
    /// <summary>Describes a COM or Windows Runtime HRESULT.</summary>
    public static PlatformDiagnostic FromHResult(int hresult) =>
        new("HRESULT", unchecked((uint)hresult), OperatingSystem.IsWindows() ? Describe(hresult) : null);

    /// <summary>Describes a Win32 error code, such as the value of <c>GetLastError</c>.</summary>
    public static PlatformDiagnostic FromWin32Error(int error) => new("Win32", error, OperatingSystem.IsWindows() ? Describe(error) : null);

    /// <summary>Describes a POSIX <c>errno</c> value.</summary>
    public static PlatformDiagnostic FromErrno(int errno) => new("errno", errno, OperatingSystem.IsWindows() ? null : Describe(errno));

    /// <summary>Formats the diagnostic as <c>Domain Code: Message</c>.</summary>
    public override string ToString()
    {
        var code = Domain is "HRESULT" or "IOReturn"
            ? $"0x{Code:X8}"
            : Code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Message is null ? $"{Domain} {code}" : $"{Domain} {code}: {Message}";
    }

    // FormatMessage on Windows and strerror elsewhere; neither includes paths. Callers
    // pass only codes from the running platform's own error space.
    private static string? Describe(int code)
    {
        try
        {
            var message = System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage(code).Trim();
            return message.Length == 0 ? null : message;
        }
        catch (Exception) { return null; }
    }
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
    public sealed record Unavailable(PlatformUnavailableReason Reason) : PickerResult<T>;
    /// <summary>The dialog or acquisition failed.</summary>
    /// <param name="Code">The classified failure.</param>
    public sealed record Failed(PlatformFailureCode Code) : PickerResult<T>;
}

/// <summary>The observed outcome after an atomic replacement attempt. An uncertain commit must not be retried automatically.</summary>
public abstract record FileCommitResult
{
    private FileCommitResult() { }
    /// <summary>The replacement completed.</summary>
    public sealed record Committed : FileCommitResult;
    /// <summary>The replacement did not occur.</summary>
    /// <param name="Code">The classified failure.</param>
    public sealed record NotCommitted(PlatformFailureCode Code) : FileCommitResult;
    /// <summary>The replacement may have occurred; inspect the destination before deciding what to do.</summary>
    /// <param name="Code">The failure that prevented determining the outcome.</param>
    public sealed record CommitUnknown(PlatformFailureCode Code) : FileCommitResult;
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
    ValueTask<PlatformResult<PlatformUnit>> WriteTextAsync(string text, CancellationToken cancellationToken = default);
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
    public sealed record Unavailable(PlatformUnavailableReason Reason) : CapabilityStatus;
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
