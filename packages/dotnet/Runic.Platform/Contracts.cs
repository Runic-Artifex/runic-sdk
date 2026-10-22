using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Runic.Platform;

/// <summary>Why a capability cannot currently be used.</summary>
/// <remarks>This set is open: a minor release may add reasons, so handle unknown values.</remarks>
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
/// <remarks>
/// This set is open: a minor release may add codes, so handle unknown values, for example as <see cref="Other"/>.
/// Zero is never a real failure: <see cref="Unknown"/> is only the default value, and no provider reports it.
/// </remarks>
public enum PlatformFailureCode
{
    /// <summary>No code; the default value. Runic never reports it.</summary>
    Unknown = 0,
    /// <summary>The operation was denied access.</summary>
    PermissionDenied = 1,
    /// <summary>A conflicting operation or another process holds the resource.</summary>
    ResourceBusy = 2,
    /// <summary>The supplied or native data is invalid.</summary>
    InvalidData = 3,
    /// <summary>The content exceeds the requested limit.</summary>
    TooLarge = 4,
    /// <summary>An input/output operation failed.</summary>
    IOError = 5,
    /// <summary>The destination changed since acquisition.</summary>
    Conflict = 6,
    /// <summary>The user dismissed a native interactive operation.</summary>
    UserDismissed = 7,
    /// <summary>The store is locked and could not ask the user to unlock it.</summary>
    Locked = 8,
    /// <summary>The file or directory does not exist.</summary>
    NotFound = 9,
    /// <summary>A failure that no other code describes, such as an unclassified native error; the
    /// <see cref="PlatformDiagnostic"/> carries the native detail.</summary>
    Other = 10
}

/// <summary>Ownership requirements for native dialogs. Runic supports owned dialogs only; other values may be added.</summary>
/// <remarks>This set is open: a minor release may add policies.</remarks>
public enum OwnerPolicy
{
    /// <summary>Require the verified owner of this presentation.</summary>
    RequireOwner
}

/// <summary>Required durability behavior for a save transaction.</summary>
/// <remarks>This set is open: a minor release may add policies.</remarks>
public enum FileWritePolicy
{
    /// <summary>Stage beside the target and replace atomically; otherwise report unavailable before modifying it.</summary>
    RequireAtomicReplace
}

/// <summary>The successful result of an operation without a return value.</summary>
public readonly record struct PlatformUnit;

/// <summary>A service outcome. Cancellation is represented by an OperationCanceledException.</summary>
/// <remarks>The set of cases is closed: <see cref="Success"/>, <see cref="Unavailable"/> and <see cref="Failed"/> are
/// the only ones for this major version.</remarks>
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

    /// <summary>Whether this is a <see cref="Success"/>.</summary>
    public bool Succeeded => this is Success;

    /// <summary>Gets the value of a <see cref="Success"/>.</summary>
    /// <param name="value">The returned value when this is a <see cref="Success"/>; otherwise the default.</param>
    /// <returns><see langword="true"/> for a <see cref="Success"/>; <see langword="false"/> for
    /// <see cref="Unavailable"/> and <see cref="Failed"/>.</returns>
    /// <example><code>
    /// if ((await store.ReadAsync(key)).TryGetValue(out var secret)) { … }
    /// </code></example>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        if (this is Success success)
        {
            value = success.Value;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Calls the function for this case and returns its result.</summary>
    /// <typeparam name="TResult">The type the functions return.</typeparam>
    /// <param name="success">Called with the value of a <see cref="Success"/>.</param>
    /// <param name="unavailable">Called with an <see cref="Unavailable"/> case.</param>
    /// <param name="failed">Called with a <see cref="Failed"/> case.</param>
    /// <returns>What the called function returned.</returns>
    /// <exception cref="ArgumentNullException">A function is <see langword="null"/>.</exception>
    public TResult Match<TResult>(Func<T, TResult> success, Func<Unavailable, TResult> unavailable, Func<Failed, TResult> failed)
    {
        ArgumentNullException.ThrowIfNull(success);
        ArgumentNullException.ThrowIfNull(unavailable);
        ArgumentNullException.ThrowIfNull(failed);
        return this switch
        {
            Success value => success(value.Value),
            Unavailable reason => unavailable(reason),
            Failed failure => failed(failure),
            _ => throw new UnreachableException(),
        };
    }

    // Keeps ToString as before Succeeded was added: the cases print their own members only.
    /// <summary>Prints the members of a case; the base prints none.</summary>
    /// <param name="builder">The text being built.</param>
    /// <returns><see langword="false"/>: the base has no members to print.</returns>
    protected virtual bool PrintMembers(StringBuilder builder) => false;
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
/// <remarks>The set of cases is closed: <see cref="Selected"/>, <see cref="Dismissed"/>, <see cref="Unavailable"/> and
/// <see cref="Failed"/> are the only ones for this major version.</remarks>
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
        /// <summary>The filter that was active when the user confirmed the dialog, one of the options' filters.</summary>
        /// <remarks>
        /// <see langword="null"/> when the dialog offered no filters, for a directory selection, and when the dialog
        /// does not report its filter: Windows always reports it, macOS only with a filter pop-up
        /// (<c>ShowsFilterChoice</c>), the Linux GTK chooser always and the xdg-desktop-portal FileChooser when the
        /// portal returns <c>current_filter</c>. The selection itself may still match no filter.
        /// </remarks>
        public FileDialogFilter? SelectedFilter { get; init; }
        /// <summary>Deconstructs the selection, for example in <c>result is PickerResult&lt;IReadFileLease&gt;.Selected(var lease)</c>.</summary>
        /// <param name="value">The acquired resource.</param>
        public void Deconstruct(out T value) => value = Value;
    }
    /// <summary>The user dismissed the dialog without selecting a resource.</summary>
    public sealed record Dismissed : PickerResult<T>;
    /// <summary>The dialog could not be shown.</summary>
    /// <param name="Reason">The unavailability reason.</param>
    public sealed record Unavailable(PlatformUnavailableReason Reason) : PickerResult<T>;
    /// <summary>The dialog or acquisition failed.</summary>
    /// <param name="Code">The classified failure. Branch on this value.</param>
    /// <param name="Diagnostic">Native error detail for logs and support, when the provider observed one.</param>
    public sealed record Failed(PlatformFailureCode Code, PlatformDiagnostic? Diagnostic = null) : PickerResult<T>;
}

/// <summary>The observed outcome after an atomic replacement attempt. An uncertain commit must not be retried automatically.</summary>
/// <remarks>The set of cases is closed: <see cref="Committed"/>, <see cref="NotCommitted"/> and <see cref="CommitUnknown"/>
/// are the only ones for this major version.</remarks>
public abstract record FileCommitResult
{
    private FileCommitResult() { }
    /// <summary>The replacement completed.</summary>
    public sealed record Committed : FileCommitResult;
    /// <summary>The replacement did not occur.</summary>
    /// <param name="Code">The classified failure.</param>
    /// <param name="Diagnostic">Native error detail for logs and support, when the provider observed one.</param>
    public sealed record NotCommitted(PlatformFailureCode Code, PlatformDiagnostic? Diagnostic = null) : FileCommitResult;
    /// <summary>The replacement may have occurred; inspect the destination before deciding what to do.</summary>
    /// <param name="Code">The failure that prevented determining the outcome.</param>
    /// <param name="Diagnostic">Native error detail for logs and support, when the provider observed one.</param>
    public sealed record CommitUnknown(PlatformFailureCode Code, PlatformDiagnostic? Diagnostic = null) : FileCommitResult;
}

/// <summary>A named file type a file dialog offers, such as "Images" for <c>png</c> and <c>jpg</c>.</summary>
/// <remarks>
/// A filter is a hint to the dialog: it limits which files the dialog shows, but the user can still choose or type a
/// name that matches no filter on some platforms, and Runic does not reject such a selection. Validate the content
/// you read. Extensions match case-insensitively on every platform. macOS shows no filter pop-up unless
/// <see cref="OpenFileOptions.ShowsFilterChoice"/> or <see cref="SaveFileOptions.ShowsFilterChoice"/> asks for one:
/// without it, its panel offers the files of every filter at once.
/// </remarks>
/// <param name="Name">The name the dialog shows, in the user's language.</param>
/// <param name="Extensions">
/// Extensions without the leading dot, such as <c>png</c> or <c>tar.gz</c>, or <c>*</c> for every file. At least one.
/// </param>
/// <exception cref="ArgumentException">The name is empty, or an extension is empty or contains a path separator, a
/// wildcard other than a lone <c>*</c>, a leading dot or a character that cannot appear in a filename pattern.</exception>
public sealed record FileDialogFilter(string Name, ImmutableArray<string> Extensions)
{
    private readonly string _name = ValidName(Name);
    private readonly ImmutableArray<string> _extensions = ValidExtensions(Extensions);

    /// <summary>The name the dialog shows, in the user's language.</summary>
    public string Name { get => _name; init => _name = ValidName(value); }
    /// <summary>Extensions without the leading dot, or <c>*</c> for every file.</summary>
    public ImmutableArray<string> Extensions { get => _extensions; init => _extensions = ValidExtensions(value); }

    /// <summary>MIME types the filter also matches, such as <c>image/png</c>, or <c>image/*</c> for every image; empty by
    /// default.</summary>
    /// <remarks>
    /// MIME types widen the filter where the dialog matches content types: macOS adds the corresponding uniform type
    /// identifiers, and the Linux GTK chooser and xdg-desktop-portal FileChooser match the file's MIME type. Windows
    /// filters by extension only and ignores them, so <see cref="Extensions"/> stays required. Types match without
    /// regard to case.
    /// </remarks>
    /// <exception cref="ArgumentException">A type is not <c>type/subtype</c> or <c>type/*</c> made of MIME token
    /// characters.</exception>
    public ImmutableArray<string> MimeTypes { get => _mimeTypes; init => _mimeTypes = ValidMimeTypes(value); }
    private readonly ImmutableArray<string> _mimeTypes = [];

    /// <summary>Whether the filter matches every file.</summary>
    public bool MatchesAllFiles => _extensions.Contains("*");

    // Wildcards, separators and characters that a Windows filter specification or a glob would interpret.
    private static readonly System.Buffers.SearchValues<char> PatternCharacters = System.Buffers.SearchValues.Create("*?[]{}/\\;,:\0");

    private static string ValidName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('\0')) throw new ArgumentException("A filter name cannot contain NUL.", nameof(name));
        return name;
    }

    // RFC 6838 restricted names: type "/" subtype, each 1 to 127 characters; the subtype may be "*".
    private static ImmutableArray<string> ValidMimeTypes(ImmutableArray<string> mimeTypes)
    {
        if (mimeTypes.IsDefaultOrEmpty) return [];
        foreach (var mimeType in mimeTypes)
        {
            int slash = mimeType?.IndexOf('/') ?? -1;
            if (slash < 0 || !IsMimeName(mimeType.AsSpan(0, slash)) || mimeType.AsSpan(slash + 1) is var subtype && !(subtype is "*" || IsMimeName(subtype)))
                throw new ArgumentException($"Use a MIME type such as \"image/png\" or \"image/*\": \"{mimeType}\".", nameof(mimeTypes));
        }
        return mimeTypes;
    }

    private static bool IsMimeName(ReadOnlySpan<char> name)
    {
        if (name.Length is 0 or > 127 || !char.IsAsciiLetterOrDigit(name[0])) return false;
        foreach (char character in name)
            if (!char.IsAsciiLetterOrDigit(character) && "!#$&^_.+-".IndexOf(character) < 0) return false;
        return true;
    }

    private static ImmutableArray<string> ValidExtensions(ImmutableArray<string> extensions)
    {
        if (extensions.IsDefaultOrEmpty) throw new ArgumentException("A filter needs at least one extension.", nameof(extensions));
        foreach (var extension in extensions)
        {
            if (extension == "*") continue;
            if (string.IsNullOrWhiteSpace(extension) || extension[0] == '.' || extension[^1] == '.'
                || extension.AsSpan().IndexOfAny(PatternCharacters) >= 0 || extension.Any(char.IsControl) || extension.Any(char.IsWhiteSpace))
                throw new ArgumentException($"Use an extension without its dot, such as \"png\", or \"*\" for every file: \"{extension}\".", nameof(extensions));
        }
        return extensions;
    }
}

/// <summary>Options for selecting existing files.</summary>
/// <param name="OwnerPolicy">The required dialog ownership.</param>
public sealed record OpenFileOptions(OwnerPolicy OwnerPolicy = OwnerPolicy.RequireOwner)
{
    private readonly ImmutableArray<FileDialogFilter> _filters = [];
    private readonly string? _initialDirectory;

    /// <summary>The file types the dialog offers, the first selected initially; empty offers every file.</summary>
    /// <remarks>Filters are hints that Runic does not enforce on the selection; see <see cref="FileDialogFilter"/>.</remarks>
    /// <exception cref="ArgumentException">The array contains <see langword="null"/>.</exception>
    public ImmutableArray<FileDialogFilter> Filters { get => _filters; init => _filters = FileDialogOptions.ValidFilters(value); }

    /// <summary>The fully qualified directory the dialog opens in, or <see langword="null"/> for the platform's choice.</summary>
    /// <remarks>A directory that does not exist is ignored by the platform. The xdg-desktop-portal FileChooser before
    /// version 4 ignores it when opening files.</remarks>
    /// <exception cref="ArgumentException">The path is not fully qualified or contains NUL.</exception>
    public string? InitialDirectory { get => _initialDirectory; init => _initialDirectory = FileDialogOptions.ValidDirectory(value); }

    /// <summary>Whether macOS adds a pop-up to the panel for choosing one of <see cref="Filters"/>; <see langword="false"/>
    /// by default.</summary>
    /// <remarks>
    /// Without it a macOS panel offers the files of every filter at once and reports no
    /// <see cref="PickerResult{T}.Selected.SelectedFilter"/>. With it, the panel shows the first filter's files and the
    /// pop-up switches between filters. Windows and Linux always show their own filter choice and ignore this value.
    /// </remarks>
    public bool ShowsFilterChoice { get; init; }
}

/// <summary>Options for selecting existing local directories.</summary>
/// <param name="OwnerPolicy">The required dialog ownership.</param>
public sealed record OpenDirectoryOptions(OwnerPolicy OwnerPolicy = OwnerPolicy.RequireOwner)
{
    private readonly string? _initialDirectory;

    /// <summary>The fully qualified directory the dialog opens in, or <see langword="null"/> for the platform's choice.</summary>
    /// <remarks>A directory that does not exist is ignored by the platform. The xdg-desktop-portal FileChooser before
    /// version 4 ignores it.</remarks>
    /// <exception cref="ArgumentException">The path is not fully qualified or contains NUL.</exception>
    public string? InitialDirectory { get => _initialDirectory; init => _initialDirectory = FileDialogOptions.ValidDirectory(value); }
}

/// <summary>Options for selecting one save destination.</summary>
/// <param name="SuggestedName">A filename without path separators.</param>
/// <param name="OwnerPolicy">The required dialog ownership.</param>
public sealed record SaveFileOptions(string SuggestedName, OwnerPolicy OwnerPolicy = OwnerPolicy.RequireOwner)
{
    private readonly ImmutableArray<FileDialogFilter> _filters = [];
    private readonly string? _initialDirectory;

    /// <summary>The file types the dialog offers, the first selected initially; empty offers every file.</summary>
    /// <remarks>Filters are hints: Runic does not add an extension to the chosen name or reject a name that matches no
    /// filter; see <see cref="FileDialogFilter"/>.</remarks>
    /// <exception cref="ArgumentException">The array contains <see langword="null"/>.</exception>
    public ImmutableArray<FileDialogFilter> Filters { get => _filters; init => _filters = FileDialogOptions.ValidFilters(value); }

    /// <summary>The fully qualified directory the dialog opens in, or <see langword="null"/> for the platform's choice.</summary>
    /// <remarks>A directory that does not exist is ignored by the platform.</remarks>
    /// <exception cref="ArgumentException">The path is not fully qualified or contains NUL.</exception>
    public string? InitialDirectory { get => _initialDirectory; init => _initialDirectory = FileDialogOptions.ValidDirectory(value); }

    /// <summary>Whether macOS adds a pop-up to the panel for choosing one of <see cref="Filters"/>; <see langword="false"/>
    /// by default.</summary>
    /// <remarks>
    /// Without it a macOS panel offers the files of every filter at once and reports no
    /// <see cref="PickerResult{T}.Selected.SelectedFilter"/>. With it, the panel shows the first filter's files and the
    /// pop-up switches between filters. Windows and Linux always show their own filter choice and ignore this value.
    /// </remarks>
    public bool ShowsFilterChoice { get; init; }
}

internal static class FileDialogOptions
{
    internal static ImmutableArray<FileDialogFilter> ValidFilters(ImmutableArray<FileDialogFilter> filters)
    {
        if (filters.IsDefaultOrEmpty) return [];
        if (filters.Contains(null!)) throw new ArgumentException("A filter cannot be null.", nameof(filters));
        return filters;
    }

    internal static string? ValidDirectory(string? directory)
    {
        if (directory is not null && (directory.Contains('\0') || !Path.IsPathFullyQualified(directory)))
            throw new ArgumentException("Use a fully qualified directory path.", nameof(directory));
        return directory;
    }
}

/// <summary>Presentation-scoped selection of file access. Leases, streams and native access remain in C#.</summary>
public interface IFileDialogs
{
    /// <summary>Selects directory access for C# filesystem operations. A provider without directory selection reports unavailable.</summary>
    /// <remarks>
    /// The default implementation returns <see cref="PickerResult{T}.Unavailable"/> with
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>. A provider that implements directory selection
    /// overrides this method and also reports that support through its capability declaration, such as
    /// <c>IPickerBackend.SupportsDirectorySelection</c> in <c>Runic.Platform.Runtime</c>.
    /// </remarks>
    ValueTask<PickerResult<IDirectoryLease>> OpenDirectoryAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.OwnerPolicy)) throw new ArgumentOutOfRangeException(nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PickerResult<IDirectoryLease>>(new PickerResult<IDirectoryLease>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
    }
    /// <summary>Selects and acquires a single-use read lease.</summary>
    ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default);
    /// <summary>Selects one or more files and acquires a single-use read lease for each.</summary>
    /// <remarks>
    /// The selection is all or nothing: if any selected file cannot be opened, every acquired lease is released and
    /// the result is <see cref="PickerResult{T}.Failed"/>. Each lease holds an open file until it or the list is
    /// disposed. The default implementation returns <see cref="PickerResult{T}.Unavailable"/> with
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>; a provider that implements multiple selection
    /// overrides it and reports the <c>platform.files.openMultiple</c> capability.
    /// </remarks>
    ValueTask<PickerResult<IReadFileLeaseList>> OpenFilesAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.OwnerPolicy)) throw new ArgumentOutOfRangeException(nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PickerResult<IReadFileLeaseList>>(new PickerResult<IReadFileLeaseList>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
    }
    /// <summary>Selects and acquires a save destination.</summary>
    ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default);
    /// <summary>Selects one or more directories and acquires access to each for C# filesystem operations.</summary>
    /// <remarks>
    /// Each lease keeps its directory's access, as <see cref="OpenDirectoryAsync"/> does, until it, the list or the
    /// presentation is disposed. The default implementation returns <see cref="PickerResult{T}.Unavailable"/> with
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>; a provider that implements multiple directory
    /// selection overrides it and reports the <c>platform.directories.openMultiple</c> capability.
    /// </remarks>
    ValueTask<PickerResult<IDirectoryLeaseList>> OpenDirectoriesAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.OwnerPolicy)) throw new ArgumentOutOfRangeException(nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PickerResult<IDirectoryLeaseList>>(new PickerResult<IDirectoryLeaseList>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
    }
}

/// <summary>Owns access to one local directory until disposed or its presentation closes.</summary>
/// <remarks>
/// Keep this lease alive while C# filesystem operations use its path. Neither the lease nor native access grants are
/// bridge payloads. Presentation close releases access even while application work still uses the path; work that
/// can outlive the presentation, such as accepted recovery, must finish before the presentation's lifetime is stopped.
/// </remarks>
public interface IDirectoryLease : IAsyncDisposable
{
    /// <summary>The directory's display name, without its parent path; a filesystem root, such as <c>/</c> or <c>C:\</c>, is named by the root itself. Throws after access is released.</summary>
    string DisplayName { get; }
    /// <summary>The exact local path for C# filesystem operations. Throws after access is released; a saved copy does not retain the access grant.</summary>
    string LocalPath { get; }
}

/// <summary>The directory leases of one multiple selection, in the order the dialog returned them.</summary>
/// <remarks>Disposing the list releases every lease in it; a lease disposed on its own stays released.</remarks>
public interface IDirectoryLeaseList : IReadOnlyList<IDirectoryLease>, IAsyncDisposable;

/// <summary>Owns acquired read access until disposed or its presentation closes.</summary>
public interface IReadFileLease : IAsyncDisposable
{
    /// <summary>A display filename, without a filesystem path.</summary>
    string DisplayName { get; }
    /// <summary>Consumes the lease once and returns its acquired stream. The lease retains ownership of the stream.</summary>
    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>The read leases of one multiple selection, in the order the dialog returned them.</summary>
/// <remarks>Disposing the list releases every lease in it; a lease disposed on its own stays released.</remarks>
public interface IReadFileLeaseList : IReadOnlyList<IReadFileLease>, IAsyncDisposable;

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

/// <summary>One capability's current availability.</summary>
/// <remarks>This set is closed: it gains cases only in a major release.</remarks>
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
