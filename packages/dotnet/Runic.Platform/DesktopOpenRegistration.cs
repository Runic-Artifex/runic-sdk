using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>A file type the application opens, which <see cref="IDesktopOpenRegistration"/> registers.</summary>
/// <param name="Extension">The file name extension with its dot, such as <c>.notes</c>; compared without regard to case.</param>
/// <exception cref="ArgumentException">The extension does not start with one dot, ends with a dot, or contains a path
/// separator, a wildcard, white space or a control character.</exception>
public sealed record DesktopFileType(string Extension)
{
    // Wildcards, separators and characters that a registry key, a glob or a desktop entry list would interpret.
    private static readonly System.Buffers.SearchValues<char> Reserved = System.Buffers.SearchValues.Create("*?[]{}/\\;,:=\"<>|\0");

    private readonly string _extension = ValidExtension(Extension);
    private readonly string? _mimeType, _description, _iconPath;

    /// <summary>The file name extension with its dot, such as <c>.notes</c>.</summary>
    public string Extension { get => _extension; init => _extension = ValidExtension(value); }

    /// <summary>The MIME type of the files, such as <c>application/x-example-notes</c>; <see langword="null"/> by default.</summary>
    /// <remarks>
    /// Linux associates the desktop entry with this type and, for a type the shared MIME-info database does not know,
    /// writes its definition with the extension's glob. Without it, Linux uses the type the database gives the
    /// extension and fails with <see cref="PlatformFailureCode.InvalidData"/> when it gives none. Windows and macOS
    /// ignore it.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is not <c>type/subtype</c> made of MIME token characters.</exception>
    public string? MimeType { get => _mimeType; init => _mimeType = ValidMimeType(value); }

    /// <summary>The name the shell shows for the type, such as <c>Notes document</c>; <see langword="null"/> by default.</summary>
    /// <exception cref="ArgumentException">The value is empty or contains a control character.</exception>
    public string? Description { get => _description; init => _description = DesktopOpenRegistration.ValidText(value, nameof(Description)); }

    /// <summary>
    /// The icon for files of the type: on Windows a fully qualified <c>.ico</c> or executable path, optionally with
    /// <c>,index</c> (<c>DefaultIcon</c>); on Linux an icon name or fully qualified path. <see langword="null"/> by
    /// default, for the application's icon. macOS takes icons from the bundle and ignores it.
    /// </summary>
    /// <exception cref="ArgumentException">The value is empty or contains a quote or a control character.</exception>
    public string? IconPath { get => _iconPath; init => _iconPath = DesktopOpenRegistration.ValidText(value, nameof(IconPath), quotes: false); }

    private static string ValidExtension(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        if (extension.Length is < 2 or > 64 || extension[0] != '.' || extension[1] == '.' || extension[^1] == '.'
            || extension.AsSpan(1).IndexOfAny(Reserved) >= 0 || extension.Any(char.IsControl) || extension.Any(char.IsWhiteSpace))
            throw new ArgumentException($"Use a file name extension with its dot, such as \".notes\": \"{extension}\".", nameof(extension));
        return extension;
    }

    // RFC 6838 restricted names: type "/" subtype, each 1 to 127 characters.
    private static string? ValidMimeType(string? mimeType)
    {
        if (mimeType is null) return null;
        int slash = mimeType.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || !IsMimeName(mimeType.AsSpan(0, slash)) || !IsMimeName(mimeType.AsSpan(slash + 1)))
            throw new ArgumentException($"Use a MIME type such as \"application/x-example-notes\": \"{mimeType}\".", nameof(mimeType));
        return mimeType;
    }

    private static bool IsMimeName(ReadOnlySpan<char> name)
    {
        if (name.Length is 0 or > 127 || !char.IsAsciiLetterOrDigit(name[0])) return false;
        foreach (char character in name)
            if (!char.IsAsciiLetterOrDigit(character) && "!#$&^_.+-".IndexOf(character) < 0) return false;
        return true;
    }
}

/// <summary>The URL schemes and file types an application registers with the desktop, for the current user.</summary>
/// <param name="DisplayName">The application's name, which the desktop shows, such as in its list of applications for
/// a type.</param>
/// <remarks>
/// A registration makes the application a handler that the desktop offers; it never makes it the default, which
/// stays the user's choice (<see cref="IDesktopApplications.SetDefaultApplicationAsync"/>). The desktop then starts
/// the application with the file or URL as one argument, which <see cref="DesktopOpenRequests.FromLaunch"/> reads.
/// </remarks>
/// <exception cref="ArgumentException">The display name is empty or contains a control character.</exception>
public sealed record DesktopOpenRegistration(string DisplayName)
{
    private static readonly System.Buffers.SearchValues<char> SchemeCharacters =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+-.");

    private readonly string _displayName = ValidText(DisplayName, nameof(DisplayName)) ?? throw new ArgumentNullException(nameof(DisplayName));
    private readonly ImmutableArray<string> _urlSchemes = [];
    private readonly ImmutableArray<DesktopFileType> _fileTypes = [];
    private readonly string? _executable;

    /// <summary>The application's name, which the desktop shows.</summary>
    public string DisplayName { get => _displayName; init => _displayName = ValidText(value, nameof(DisplayName)) ?? throw new ArgumentNullException(nameof(DisplayName)); }

    /// <summary>URL schemes, such as <c>org.example.notes</c>, by the rules of <see cref="DesktopOpenOptions.UrlSchemes"/>;
    /// empty by default.</summary>
    /// <exception cref="ArgumentException">A value is not a URL scheme, is <c>file</c>, <c>http</c> or <c>https</c>, or
    /// appears twice.</exception>
    public ImmutableArray<string> UrlSchemes { get => _urlSchemes; init => _urlSchemes = ValidSchemes(value); }

    /// <summary>File types; empty by default.</summary>
    /// <exception cref="ArgumentException">A value is <see langword="null"/>, or an extension appears twice.</exception>
    public ImmutableArray<DesktopFileType> FileTypes { get => _fileTypes; init => _fileTypes = ValidFileTypes(value); }

    /// <summary>
    /// The fully qualified path of the executable the desktop starts; <see langword="null"/> by default, for
    /// <see cref="Environment.ProcessPath"/>. The desktop passes the file or URL as one quoted argument
    /// (<c>"%1"</c> on Windows, <c>%U</c> on Linux). macOS registers the running bundle and ignores it.
    /// </summary>
    /// <exception cref="ArgumentException">The path is not fully qualified, or contains a quote or a control character.</exception>
    public string? Executable { get => _executable; init => _executable = ValidExecutable(value); }

    internal static string? ValidText(string? value, string name, bool quotes = true)
    {
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl) || (!quotes && value.Contains('"')))
            throw new ArgumentException($"{name} must be non-empty text of at most 1024 characters without control characters{(quotes ? "" : " or quotes")}.", name);
        return value;
    }

    private static ImmutableArray<string> ValidSchemes(ImmutableArray<string> urlSchemes)
    {
        if (urlSchemes.IsDefaultOrEmpty) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string scheme in urlSchemes)
        {
            // RFC 3986: ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ); http and https belong to browsers, file to files.
            if (scheme is null || scheme.Length is 0 or > 64 || !char.IsAsciiLetter(scheme[0]) || scheme.AsSpan(1).IndexOfAnyExcept(SchemeCharacters) >= 0
                || scheme.Equals("file", StringComparison.OrdinalIgnoreCase) || scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{scheme}' is not a URL scheme an application registers.", nameof(urlSchemes));
            if (!seen.Add(scheme)) throw new ArgumentException($"The URL scheme '{scheme}' appears twice.", nameof(urlSchemes));
        }
        return urlSchemes;
    }

    private static ImmutableArray<DesktopFileType> ValidFileTypes(ImmutableArray<DesktopFileType> fileTypes)
    {
        if (fileTypes.IsDefaultOrEmpty) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileType in fileTypes)
        {
            if (fileType is null) throw new ArgumentException("A file type is null.", nameof(fileTypes));
            if (!seen.Add(fileType.Extension)) throw new ArgumentException($"The extension '{fileType.Extension}' appears twice.", nameof(fileTypes));
        }
        return fileTypes;
    }

    private static string? ValidExecutable(string? path)
    {
        if (path is null) return null;
        if (path.Length > 4096 || !Path.IsPathFullyQualified(path) || path.Contains('"') || path.Any(char.IsControl))
            throw new ArgumentException($"Use the fully qualified path of the executable, without quotes: \"{path}\".", nameof(path));
        return path;
    }
}

/// <summary>Registers the application with the desktop for URL schemes and file types, for the current user.</summary>
/// <remarks>
/// <para>For an application that installs itself, such as a zip, an AppImage or <c>dotnet run</c> during development.
/// A package registers the application itself (an MSIX manifest, a Flatpak or Snap export, a bundle's
/// <c>Info.plist</c>): a packaged or sandboxed process reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>.</para>
/// <para>Nothing is written until <see cref="RegisterAsync"/>, which an application calls when the user agrees. A
/// registration never replaces one that names another application: every item is checked before anything is written,
/// and such an item is <see cref="PlatformFailureCode.Conflict"/> with nothing changed.</para>
/// </remarks>
public interface IDesktopOpenRegistration
{
    /// <summary>Whether every scheme and file type of <paramref name="registration"/> is registered for this application.</summary>
    /// <returns><see langword="false"/> when any item is missing or names another application.</returns>
    ValueTask<PlatformResult<bool>> IsRegisteredAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default);

    /// <summary>Registers every scheme and file type of <paramref name="registration"/> for this application, updating an
    /// earlier registration of its own.</summary>
    /// <remarks>
    /// <see cref="PlatformFailureCode.Conflict"/>, with a diagnostic and nothing written, when an item is registered
    /// by another application or by the application's package. It does not make the application the default.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="registration"/> has neither a URL scheme nor a file type.</exception>
    ValueTask<PlatformResult<PlatformUnit>> RegisterAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default);

    /// <summary>Removes what <see cref="RegisterAsync"/> wrote for <paramref name="registration"/>; entries of other
    /// applications stay.</summary>
    /// <returns>Whether anything was removed.</returns>
    ValueTask<PlatformResult<bool>> UnregisterAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default);
}

/// <summary>Open registration for hosts and platforms that cannot provide it.</summary>
public static class DesktopOpenRegistrations
{
    /// <summary>Creates open registration that reports every request unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>Nothing throws except for a missing argument and caller cancellation.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not defined.</exception>
    public static IDesktopOpenRegistration Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableOpenRegistration(reason);
    }

    private sealed class UnavailableOpenRegistration(PlatformUnavailableReason reason) : IDesktopOpenRegistration, IDesktopServiceStatus
    {
        public CapabilityStatus Status { get; } = DesktopServiceStatus.Unavailable(reason);

        public ValueTask<PlatformResult<bool>> IsRegisteredAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default) =>
            Answer<bool>(registration, cancellationToken);

        public ValueTask<PlatformResult<PlatformUnit>> RegisterAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default) =>
            Answer<PlatformUnit>(registration, cancellationToken);

        public ValueTask<PlatformResult<bool>> UnregisterAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default) =>
            Answer<bool>(registration, cancellationToken);

        private ValueTask<PlatformResult<T>> Answer<T>(DesktopOpenRegistration registration, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(registration);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<T>>(new PlatformResult<T>.Unavailable(reason));
        }
    }
}

/// <summary>Keeps access to files across launches: a sandboxed macOS application keeps the files the user opened or
/// chose, as a recent-files list needs.</summary>
/// <remarks>
/// On macOS a bookmark is a security-scoped bookmark inside the App Sandbox and an ordinary bookmark otherwise, which
/// also follows a file the user moves or renames. Elsewhere a bookmark is the file's path. Store the bytes as they
/// are; they are not portable between computers or users.
/// </remarks>
public interface IDesktopFileBookmarks
{
    /// <summary>Creates a bookmark for the file or directory at <paramref name="path"/>, which the application can
    /// access now, such as one the user opened or chose.</summary>
    /// <returns>The bookmark; <see cref="PlatformFailureCode.NotFound"/> for a missing item, and a failure such as
    /// <see cref="PlatformFailureCode.PermissionDenied"/> or <see cref="PlatformFailureCode.IOError"/> (macOS reports
    /// <c>NSFileReadUnknownError</c>) for one the sandbox does not let the application read.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not fully qualified or contains NUL.</exception>
    ValueTask<PlatformResult<ImmutableArray<byte>>> CreateAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Resolves a bookmark from <see cref="CreateAsync"/> and starts accessing its file until the access is
    /// disposed.</summary>
    /// <returns>The access; <see cref="PlatformFailureCode.NotFound"/> when the item no longer exists (inside the macOS
    /// App Sandbox a security-scoped bookmark of a deleted item reports <see cref="PlatformFailureCode.InvalidData"/>),
    /// <see cref="PlatformFailureCode.InvalidData"/> for bytes that are not a bookmark, and
    /// <see cref="PlatformFailureCode.PermissionDenied"/> when the system refuses access. Treat any failure as a
    /// bookmark to forget.</returns>
    /// <exception cref="ArgumentException"><paramref name="bookmark"/> is default or empty.</exception>
    ValueTask<PlatformResult<IDesktopBookmarkAccess>> ResolveAsync(ImmutableArray<byte> bookmark, CancellationToken cancellationToken = default);
}

/// <summary>Access to a bookmarked file, until disposed.</summary>
public interface IDesktopBookmarkAccess : IAsyncDisposable
{
    /// <summary>The file's current path.</summary>
    string LocalPath { get; }

    /// <summary>Whether the bookmark is out of date, such as after the file moved: create a new one with
    /// <see cref="IDesktopFileBookmarks.CreateAsync"/> while the access lasts and store it instead.</summary>
    bool IsStale { get; }
}
