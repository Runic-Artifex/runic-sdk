using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>A content type the desktop knows, such as a MIME type, with its description and relations.</summary>
/// <remarks>
/// On Linux <see cref="Id"/> is the canonical MIME type from the shared MIME-info database, such as
/// <c>text/plain</c>, <c>inode/directory</c> or <c>x-scheme-handler/https</c>, and equals <see cref="MimeType"/>.
/// Windows identifies a type by its MIME type when the registry maps that type back to the same extension, else by its
/// extension, such as <c>.txt</c>; macOS by its uniform type identifier, such as <c>public.plain-text</c>.
/// <c>x-scheme-handler/&lt;scheme&gt;</c> names a URL scheme on every platform.
/// </remarks>
/// <param name="Id">The platform's identifier of the type.</param>
/// <exception cref="ArgumentException">An identifier is empty or contains NUL.</exception>
public sealed record DesktopContentType(string Id)
{
    private readonly string _id = DesktopApplicationNames.Required(Id, nameof(Id));
    private readonly ImmutableArray<string> _parents = [];
    private readonly ImmutableArray<string> _extensions = [];

    /// <summary>The platform's identifier of the type.</summary>
    public string Id { get => _id; init => _id = DesktopApplicationNames.Required(value, nameof(Id)); }
    /// <summary>A description of the type in the user's language, such as "PNG image", when the desktop has one.</summary>
    public string? Description { get; init; }
    /// <summary>The MIME type, when the type has one; on Linux the same as <see cref="Id"/>.</summary>
    public string? MimeType { get; init; }
    /// <summary>The name of the type's icon in the desktop's icon theme, when the desktop names one.</summary>
    public string? IconName { get; init; }
    /// <summary>The identifiers of the types this type is also an instance of, nearest first, such as <c>text/plain</c>
    /// and <c>application/octet-stream</c> for <c>text/x-csrc</c>.</summary>
    public ImmutableArray<string> Parents { get => _parents; init => _parents = value.IsDefault ? [] : value; }
    /// <summary>File name extensions without the dot, such as <c>png</c>, main extension first, when the desktop knows them.</summary>
    public ImmutableArray<string> Extensions { get => _extensions; init => _extensions = value.IsDefault ? [] : value; }
}

/// <summary>An installed application that can open content.</summary>
/// <remarks>
/// <see cref="Id"/> identifies the application to <see cref="IDesktopApplications"/>. On Linux it is the desktop file
/// ID, such as <c>org.gnome.TextEditor.desktop</c>; on Windows the path of its executable or, for a packaged
/// application, its AppUserModelID; on macOS its bundle identifier, such as <c>com.apple.TextEdit</c>.
/// </remarks>
/// <param name="Id">The platform's identifier of the application.</param>
/// <param name="Name">The application's name in the user's language.</param>
/// <exception cref="ArgumentException">An identifier or name is empty or contains NUL.</exception>
public sealed record DesktopApplicationInfo(string Id, string Name)
{
    private readonly string _id = DesktopApplicationNames.Required(Id, nameof(Id));
    private readonly string _name = DesktopApplicationNames.Required(Name, nameof(Name));
    private readonly ImmutableArray<string> _contentTypes = [];

    /// <summary>The platform's identifier of the application.</summary>
    public string Id { get => _id; init => _id = DesktopApplicationNames.Required(value, nameof(Id)); }
    /// <summary>The application's name in the user's language.</summary>
    public string Name { get => _name; init => _name = DesktopApplicationNames.Required(value, nameof(Name)); }
    /// <summary>A short description in the user's language, when the application has one.</summary>
    public string? Description { get; init; }
    /// <summary>The name of the application's icon in the desktop's icon theme, or an absolute icon path on Linux.</summary>
    public string? IconName { get; init; }
    /// <summary>The content types the application declares it opens.</summary>
    public ImmutableArray<string> ContentTypes { get => _contentTypes; init => _contentTypes = value.IsDefault ? [] : value; }
    /// <summary>Whether the desktop hides the application from application lists, for example a handler that only opens
    /// what other applications pass it. A hidden application can still be a content type's handler.</summary>
    public bool IsHidden { get; init; }
}

/// <summary>The image data of an icon.</summary>
/// <param name="MediaType">The image format: <c>image/png</c> or <c>image/svg+xml</c>.</param>
/// <param name="Data">The image file's bytes.</param>
/// <param name="Size">The icon's nominal size in pixels at scale 1: the size its theme designed it for, or the
/// requested size for a scalable icon.</param>
/// <exception cref="ArgumentException">The media type is empty, the data is default or the size is not positive.</exception>
public sealed record DesktopIconData(string MediaType, ImmutableArray<byte> Data, int Size)
{
    private readonly string _mediaType = DesktopApplicationNames.Required(MediaType, nameof(MediaType));
    private readonly ImmutableArray<byte> _data = Data.IsDefault ? throw new ArgumentException("Supply the icon's data.", nameof(Data)) : Data;
    private readonly int _size = Size > 0 ? Size : throw new ArgumentOutOfRangeException(nameof(Size));

    /// <summary>The image format: <c>image/png</c> or <c>image/svg+xml</c>.</summary>
    public string MediaType { get => _mediaType; init => _mediaType = DesktopApplicationNames.Required(value, nameof(MediaType)); }
    /// <summary>The image file's bytes.</summary>
    public ImmutableArray<byte> Data { get => _data; init => _data = value.IsDefault ? throw new ArgumentException("Supply the icon's data.", nameof(Data)) : value; }
    /// <summary>The icon's nominal size in pixels at scale 1.</summary>
    public int Size { get => _size; init => _size = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(Size)); }
}

/// <summary>The icon to look up: by icon name, for an application, or for a content type, at a size and scale.</summary>
/// <remarks>Create a request with <see cref="ForIconName"/>, <see cref="ForApplication"/> or
/// <see cref="ForContentType"/>; exactly one of <see cref="IconName"/>, <see cref="ApplicationId"/> and
/// <see cref="ContentType"/> is set.</remarks>
public sealed record DesktopIconRequest
{
    /// <summary>The largest size a request accepts, in pixels. A later release may raise it.</summary>
    public static int MaximumSize => 4096;
    /// <summary>The largest scale a request accepts. A later release may raise it.</summary>
    public static int MaximumScale => 16;
    private readonly int _size = 1, _scale = 1;

    private DesktopIconRequest() { }

    /// <summary>A themed icon name, such as <c>text-x-generic</c>.</summary>
    public string? IconName { get; private init; }
    /// <summary>The application whose icon to look up, an <see cref="DesktopApplicationInfo.Id"/>.</summary>
    public string? ApplicationId { get; private init; }
    /// <summary>The content type whose icon to look up, a <see cref="DesktopContentType.Id"/>.</summary>
    public string? ContentType { get; private init; }
    /// <summary>The nominal size in pixels at scale 1, 1 to <see cref="MaximumSize"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is outside its range.</exception>
    public int Size { get => _size; init => _size = value >= 1 && value <= MaximumSize ? value : throw new ArgumentOutOfRangeException(nameof(Size)); }
    /// <summary>The display scale the icon is for, 1 to <see cref="MaximumScale"/>; 2 asks for an icon designed for twice the pixel density.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The scale is outside its range.</exception>
    public int Scale { get => _scale; init => _scale = value >= 1 && value <= MaximumScale ? value : throw new ArgumentOutOfRangeException(nameof(Scale)); }

    /// <summary>Requests the themed icon <paramref name="iconName"/>, such as <c>folder</c>.</summary>
    /// <exception cref="ArgumentException">The name is empty or contains NUL or a path separator.</exception>
    public static DesktopIconRequest ForIconName(string iconName, int size, int scale = 1)
    {
        DesktopApplicationNames.Required(iconName, nameof(iconName));
        if (iconName.Contains('/') || iconName.Contains('\\')) throw new ArgumentException("Supply an icon name, not a path.", nameof(iconName));
        return new() { IconName = iconName, Size = size, Scale = scale };
    }

    /// <summary>Requests the icon of the application <paramref name="applicationId"/>.</summary>
    /// <exception cref="ArgumentException">The identifier is empty or contains NUL.</exception>
    public static DesktopIconRequest ForApplication(string applicationId, int size, int scale = 1) =>
        new() { ApplicationId = DesktopApplicationNames.ApplicationId(applicationId, nameof(applicationId)), Size = size, Scale = scale };

    /// <summary>Requests the icon of the content type <paramref name="contentType"/>.</summary>
    /// <exception cref="ArgumentException">The identifier is empty or contains NUL.</exception>
    public static DesktopIconRequest ForContentType(string contentType, int size, int scale = 1) =>
        new() { ContentType = DesktopApplicationNames.ContentType(contentType, nameof(contentType)), Size = size, Scale = scale };
}

/// <summary>How <see cref="IDesktopApplications.GetContentTypeAsync"/> determines a file's type.</summary>
/// <remarks>This set is closed: it gains values only in a major release.</remarks>
public enum DesktopContentDetection
{
    /// <summary>From the name alone, without touching the file; it need not exist.</summary>
    NameOnly,
    /// <summary>From the name and, when the name is not conclusive, the file's first bytes; the file must exist.</summary>
    NameAndContent
}

/// <summary>
/// Content types, the installed applications that open them, the user's default application for each, and their
/// icons: the desktop's application database. It needs no window.
/// </summary>
/// <remarks>
/// <para>
/// Identifiers are the platform's own. On Linux an application is a desktop file ID, such as
/// <c>org.gnome.TextEditor.desktop</c>, and a content type a MIME type, such as <c>text/plain</c> or
/// <c>x-scheme-handler/mailto</c>, following the freedesktop.org Desktop Entry, Shared MIME-info, MIME Apps and Icon
/// Theme specifications. On Windows an application is the path of its executable (a packaged application's
/// AppUserModelID) and a content type a MIME type or an extension, such as <c>.txt</c>; on macOS an application is a
/// bundle identifier and a content type a uniform type identifier, such as <c>public.plain-text</c>.
/// </para>
/// <para>
/// Inside Flatpak or Snap the host's applications and defaults are out of reach: the application methods report
/// <see cref="PlatformUnavailableReason.BackendUnavailable"/>, while content types and icons come from the sandbox's
/// own data. Without a provider every method reports <see cref="PlatformResult{T}.Unavailable"/>; see
/// <see cref="DesktopApplications.Unavailable"/>.
/// </para>
/// </remarks>
public interface IDesktopApplications
{
    /// <summary>Determines the content type of the file at <paramref name="path"/>.</summary>
    /// <remarks><see cref="DesktopContentDetection.NameOnly"/> never touches the file and returns the most generic type,
    /// <c>application/octet-stream</c> on Linux, for a name it does not recognize. With
    /// <see cref="DesktopContentDetection.NameAndContent"/> a missing file is <see cref="PlatformFailureCode.IOError"/>
    /// and an unreadable one <see cref="PlatformFailureCode.PermissionDenied"/>.</remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains NUL.</exception>
    ValueTask<PlatformResult<DesktopContentType>> GetContentTypeAsync(string path, DesktopContentDetection detection = DesktopContentDetection.NameAndContent,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the installed applications the desktop shows in application lists, by name.</summary>
    ValueTask<PlatformResult<ImmutableArray<DesktopApplicationInfo>>> GetApplicationsAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the applications that open <paramref name="contentType"/>: the default first, then the others in the
    /// desktop's order of preference, including applications for the types it is an instance of.</summary>
    /// <remarks>A content type the platform cannot parse is <see cref="PlatformFailureCode.InvalidData"/>.</remarks>
    /// <exception cref="ArgumentException"><paramref name="contentType"/> is empty or contains NUL.</exception>
    ValueTask<PlatformResult<ImmutableArray<DesktopApplicationInfo>>> GetApplicationsForAsync(string contentType, CancellationToken cancellationToken = default);

    /// <summary>Returns the application that opens <paramref name="contentType"/> by default.</summary>
    /// <returns>The application, or a successful <see langword="null"/> when no installed application opens the type.</returns>
    /// <exception cref="ArgumentException"><paramref name="contentType"/> is empty or contains NUL.</exception>
    ValueTask<PlatformResult<DesktopApplicationInfo?>> GetDefaultApplicationAsync(string contentType, CancellationToken cancellationToken = default);

    /// <summary>Makes <paramref name="applicationId"/> the user's default application for <paramref name="contentType"/>.</summary>
    /// <remarks>An application that is not installed or does not open the type is
    /// <see cref="PlatformFailureCode.InvalidData"/>, and nothing changes. macOS may ask the user to confirm the change,
    /// and the request waits for the answer; a refusal is <see cref="PlatformFailureCode.UserDismissed"/>. Windows lets
    /// only the user choose defaults: the request is <see cref="PlatformFailureCode.PermissionDenied"/>, with a
    /// diagnostic that points to Settings (<c>ms-settings:defaultapps</c>).</remarks>
    /// <exception cref="ArgumentException">An argument is empty or contains NUL.</exception>
    ValueTask<PlatformResult<PlatformUnit>> SetDefaultApplicationAsync(string contentType, string applicationId, CancellationToken cancellationToken = default);

    /// <summary>Starts the installed application <paramref name="applicationId"/>, passing it <paramref name="pathsOrUris"/>.</summary>
    /// <remarks>
    /// <para>Success means the application was started or asked to open the items, not that it opened them. The
    /// application is never a command line: only an installed application the desktop knows can be started, and each
    /// item reaches it as one argument, never through a shell. An application that cannot take what it is given, such
    /// as a web URI for an application that opens only local files, is <see cref="PlatformFailureCode.InvalidData"/>.</para>
    /// <para>On Linux an application that runs in a terminal (<c>Terminal=true</c>) is
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>.</para>
    /// </remarks>
    /// <param name="applicationId">An installed application's identifier.</param>
    /// <param name="pathsOrUris">Fully qualified local paths and absolute URIs to open; may be empty. At most 1024.</param>
    /// <param name="cancellationToken">Cancels before the application starts.</param>
    /// <exception cref="ArgumentException">The identifier is empty or contains NUL, or an item is neither a fully
    /// qualified path nor an absolute URI.</exception>
    ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string applicationId, ImmutableArray<string> pathsOrUris, CancellationToken cancellationToken = default);

    /// <summary>Looks up an icon in the desktop's icon theme and reads its image.</summary>
    /// <returns>The icon, or a successful <see langword="null"/> when the theme has none for the request.</returns>
    /// <remarks>An icon file larger than 4 MiB is <see cref="PlatformFailureCode.TooLarge"/>.</remarks>
    ValueTask<PlatformResult<DesktopIconData?>> GetIconAsync(DesktopIconRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The desktop application database for hosts and platforms that cannot provide it.</summary>
public static class DesktopApplications
{
    /// <summary>Creates an application database that reports every request unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>Requests validate their arguments as a provider does; otherwise nothing throws except caller cancellation.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not defined.</exception>
    public static IDesktopApplications Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableApplications(reason);
    }

    private sealed class UnavailableApplications(PlatformUnavailableReason reason) : IDesktopApplications, IDesktopServiceStatus
    {
        public CapabilityStatus Status { get; } = DesktopServiceStatus.Unavailable(reason);
        public ValueTask<PlatformResult<DesktopContentType>> GetContentTypeAsync(string path, DesktopContentDetection detection = DesktopContentDetection.NameAndContent,
            CancellationToken cancellationToken = default)
        {
            DesktopApplicationNames.ContentPath(path, detection, nameof(path));
            return Result<DesktopContentType>(cancellationToken);
        }

        public ValueTask<PlatformResult<ImmutableArray<DesktopApplicationInfo>>> GetApplicationsAsync(CancellationToken cancellationToken = default) =>
            Result<ImmutableArray<DesktopApplicationInfo>>(cancellationToken);

        public ValueTask<PlatformResult<ImmutableArray<DesktopApplicationInfo>>> GetApplicationsForAsync(string contentType, CancellationToken cancellationToken = default)
        {
            DesktopApplicationNames.ContentType(contentType, nameof(contentType));
            return Result<ImmutableArray<DesktopApplicationInfo>>(cancellationToken);
        }

        public ValueTask<PlatformResult<DesktopApplicationInfo?>> GetDefaultApplicationAsync(string contentType, CancellationToken cancellationToken = default)
        {
            DesktopApplicationNames.ContentType(contentType, nameof(contentType));
            return Result<DesktopApplicationInfo?>(cancellationToken);
        }

        public ValueTask<PlatformResult<PlatformUnit>> SetDefaultApplicationAsync(string contentType, string applicationId, CancellationToken cancellationToken = default)
        {
            DesktopApplicationNames.ContentType(contentType, nameof(contentType));
            DesktopApplicationNames.ApplicationId(applicationId, nameof(applicationId));
            return Result<PlatformUnit>(cancellationToken);
        }

        public ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string applicationId, ImmutableArray<string> pathsOrUris, CancellationToken cancellationToken = default)
        {
            DesktopApplicationNames.ApplicationId(applicationId, nameof(applicationId));
            DesktopApplicationNames.PathsOrUris(pathsOrUris, nameof(pathsOrUris));
            return Result<PlatformUnit>(cancellationToken);
        }

        public ValueTask<PlatformResult<DesktopIconData?>> GetIconAsync(DesktopIconRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            return Result<DesktopIconData?>(cancellationToken);
        }

        private ValueTask<PlatformResult<T>> Result<T>(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<T>>(new PlatformResult<T>.Unavailable(reason));
        }
    }
}
