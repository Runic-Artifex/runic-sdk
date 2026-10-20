using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Runtime;
using Tmds.DBus.Protocol;

namespace Runic.Platform.Linux.Portal;

// The desktop's application database on Linux, read directly from the freedesktop.org files: desktop entries, the
// shared MIME-info database, mimeapps.list and icon themes. Nothing is cached between requests except the parsed
// MIME database, which is reloaded when one of its files changes, and the portal's icon theme for a few seconds.
internal sealed partial class LinuxDesktopApplications(DesktopApplicationsOptions options, PortalApplication? application = null) : IDesktopApplications
{
    private const string DesktopSuffix = ".desktop";
    private const int MaximumIconBytes = 4 << 20;
    private static readonly TimeSpan ThemeLifetime = TimeSpan.FromSeconds(5);
    private readonly PortalApplication _application = application ?? new PortalApplication();
    private readonly DesktopLocale _locale = new(options.Locale);
    private readonly object _gate = new();
    private (SharedMimeDatabase Database, string Stamp)? _mime;
    private (string? Theme, long Read)? _portalTheme;

    internal DesktopApplicationsOptions Options => options;

    public ValueTask<PlatformResult<DesktopContentType>> GetContentTypeAsync(string path, DesktopContentDetection detection = DesktopContentDetection.NameAndContent,
        CancellationToken cancellationToken = default)
    {
        DesktopApplicationNames.ContentPath(path, detection, nameof(path));
        return Offload(() => ContentTypeCoreAsync(path, detection, cancellationToken), cancellationToken);
    }

    private ValueTask<PlatformResult<DesktopContentType>> ContentTypeCoreAsync(string path, DesktopContentDetection detection, CancellationToken cancellationToken)
    {
        DesktopApplicationNames.ContentPath(path, detection, nameof(path));
        cancellationToken.ThrowIfCancellationRequested();
        var mime = Mime();
        string name = Path.GetFileName(path.TrimEnd('/'));
        var globs = mime.MatchName(name);
        if (detection == DesktopContentDetection.NameOnly)
            return Result(new PlatformResult<DesktopContentType>.Success(ContentType(mime, globs.IsEmpty ? SharedMimeDatabase.OctetStream : globs[0])));
        return Result(Detect(mime, path, name, globs));
    }

    // The specification's recommended checking order: the file's kind, an unambiguous glob, then magic, preferring a
    // glob candidate that equals or is a subclass of the magic result, else the strongest glob, else a text or binary guess.
    private PlatformResult<DesktopContentType> Detect(SharedMimeDatabase mime, string path, string name, ImmutableArray<string> globs)
    {
        var status = Stat(path, follow: true);
        if (status.Error == 2 /* ENOENT */ && Stat(path, follow: false) is { Error: 0, Kind: FileKind.Link })
            return new PlatformResult<DesktopContentType>.Success(ContentType(mime, "inode/symlink"));
        if (status.Error != 0) return Failure<DesktopContentType>(status.Error);
        string? kind = status.Kind switch
        {
            FileKind.Directory => "inode/directory",
            FileKind.CharacterDevice => "inode/chardevice",
            FileKind.BlockDevice => "inode/blockdevice",
            FileKind.Fifo => "inode/fifo",
            FileKind.Socket => "inode/socket",
            _ => null,
        };
        if (kind is not null) return new PlatformResult<DesktopContentType>.Success(ContentType(mime, kind));
        if (globs.Length == 1) return new PlatformResult<DesktopContentType>.Success(ContentType(mime, globs[0]));
        byte[] data;
        try
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            data = new byte[Math.Max(mime.MagicExtent, 128)];
            int read = 0;
            while (read < data.Length)
            {
                int count = RandomAccess.Read(file, data.AsSpan(read), read);
                if (count == 0) break;
                read += count;
            }
            Array.Resize(ref data, read);
        }
        catch (UnauthorizedAccessException) { return Failure<DesktopContentType>(13); }
        catch (FileNotFoundException) { return Failure<DesktopContentType>(2); }
        catch (DirectoryNotFoundException) { return Failure<DesktopContentType>(2); }
        catch (IOException error) { return new PlatformResult<DesktopContentType>.Failed(PlatformFailureCode.IOError, Errno(error)); }
        string? sniffed = mime.MatchContent(data);
        // As GLib does: content alone never makes a file a desktop entry, which a launcher would run.
        if (sniffed is not null && sniffed.Equals("application/x-desktop", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(DesktopSuffix, StringComparison.Ordinal))
            sniffed = SharedMimeDatabase.TextPlain;
        string type = globs.IsEmpty
            ? sniffed ?? (LooksLikeText(data) ? SharedMimeDatabase.TextPlain : SharedMimeDatabase.OctetStream)
            : globs.FirstOrDefault(glob => mime.IsA(glob, sniffed)) ?? globs[0];
        return new PlatformResult<DesktopContentType>.Success(ContentType(mime, type));
    }

    // The specification's hint: ASCII control characters in the first 128 bytes mean binary; high bytes can be UTF-8.
    private static bool LooksLikeText(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data[..Math.Min(data.Length, 128)])
            if (b < 0x20 && b is not (byte)'\t' and not (byte)'\n' and not (byte)'\r' and not 0x0c and not 0x08 || b == 0x7f) return false;
        return true;
    }

    private DesktopContentType ContentType(SharedMimeDatabase mime, string type)
    {
        type = mime.Unalias(type);
        return new DesktopContentType(type)
        {
            MimeType = type,
            Description = mime.Description(type, _locale),
            IconName = mime.IconName(type),
            Parents = mime.Parents(type),
            Extensions = mime.Extensions(type),
        };
    }

    public ValueTask<PlatformResult<ImmutableArray<DesktopApplicationInfo>>> GetApplicationsAsync(CancellationToken cancellationToken = default) =>
        Offload(() => ApplicationsCoreAsync(cancellationToken), cancellationToken);

    private ValueTask<PlatformResult<ImmutableArray<DesktopApplicationInfo>>> ApplicationsCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Sandboxed<ImmutableArray<DesktopApplicationInfo>>() is { } sandboxed) return Result(sandboxed);
        var mime = Mime();
        var applications = new DesktopEntryCatalog(options, _locale).All()
            .Where(entry => entry.Shown(options.CurrentDesktops))
            .Select(entry => entry.Info(options.CurrentDesktops, mime))
            .OrderBy(static info => info.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(static info => info.Id, StringComparer.Ordinal);
        return Result(new PlatformResult<ImmutableArray<DesktopApplicationInfo>>.Success([.. applications]));
    }

    public ValueTask<PlatformResult<ImmutableArray<DesktopApplicationInfo>>> GetApplicationsForAsync(string contentType, CancellationToken cancellationToken = default)
    {
        DesktopApplicationNames.ContentType(contentType, nameof(contentType));
        return Offload(() => ApplicationsForCoreAsync(contentType, cancellationToken), cancellationToken);
    }

    private ValueTask<PlatformResult<ImmutableArray<DesktopApplicationInfo>>> ApplicationsForCoreAsync(string contentType, CancellationToken cancellationToken)
    {
        DesktopApplicationNames.ContentType(contentType, nameof(contentType));
        cancellationToken.ThrowIfCancellationRequested();
        if (Sandboxed<ImmutableArray<DesktopApplicationInfo>>() is { } sandboxed) return Result(sandboxed);
        if (!DesktopMimeType.IsValid(contentType)) return Result(InvalidType<ImmutableArray<DesktopApplicationInfo>>());
        var (catalog, associations, mime) = Load();
        var ids = associations.Default(contentType) is { } preferred ? associations.Recommended(contentType).Remove(preferred).Insert(0, preferred) : associations.Recommended(contentType);
        return Result(new PlatformResult<ImmutableArray<DesktopApplicationInfo>>.Success(
            [.. ids.Select(catalog.Get).OfType<DesktopEntry>().Select(entry => entry.Info(options.CurrentDesktops, mime))]));
    }

    public ValueTask<PlatformResult<DesktopApplicationInfo?>> GetDefaultApplicationAsync(string contentType, CancellationToken cancellationToken = default)
    {
        DesktopApplicationNames.ContentType(contentType, nameof(contentType));
        return Offload(() => DefaultApplicationCoreAsync(contentType, cancellationToken), cancellationToken);
    }

    private ValueTask<PlatformResult<DesktopApplicationInfo?>> DefaultApplicationCoreAsync(string contentType, CancellationToken cancellationToken)
    {
        DesktopApplicationNames.ContentType(contentType, nameof(contentType));
        cancellationToken.ThrowIfCancellationRequested();
        if (Sandboxed<DesktopApplicationInfo?>() is { } sandboxed) return Result(sandboxed);
        if (!DesktopMimeType.IsValid(contentType)) return Result(InvalidType<DesktopApplicationInfo?>());
        var (catalog, associations, mime) = Load();
        var entry = associations.Default(contentType) is { } id ? catalog.Get(id) : null;
        return Result<DesktopApplicationInfo?>(new PlatformResult<DesktopApplicationInfo?>.Success(entry?.Info(options.CurrentDesktops, mime)));
    }

    public ValueTask<PlatformResult<PlatformUnit>> SetDefaultApplicationAsync(string contentType, string applicationId, CancellationToken cancellationToken = default)
    {
        DesktopApplicationNames.ContentType(contentType, nameof(contentType));
        DesktopApplicationNames.ApplicationId(applicationId, nameof(applicationId));
        return Offload(() => SetDefaultApplicationCoreAsync(contentType, applicationId, cancellationToken), cancellationToken);
    }

    private async ValueTask<PlatformResult<PlatformUnit>> SetDefaultApplicationCoreAsync(string contentType, string applicationId, CancellationToken cancellationToken)
    {
        DesktopApplicationNames.ContentType(contentType, nameof(contentType));
        DesktopApplicationNames.ApplicationId(applicationId, nameof(applicationId));
        cancellationToken.ThrowIfCancellationRequested();
        if (Sandboxed<PlatformUnit>() is { } sandboxed) return sandboxed;
        if (!DesktopMimeType.IsValid(contentType)) return InvalidType<PlatformUnit>();
        var (catalog, associations, mime) = Load();
        if (!IsDesktopId(applicationId) || catalog.Get(applicationId) is null) return Unknown<PlatformUnit>();
        string type = mime.Unalias(contentType);
        // The specification requires the default to be associated with the type; this provider changes only the default.
        if (!associations.Recommended(type).Contains(applicationId))
        {
            _application.Diagnose("desktop-application-not-associated", "The application does not open this content type.",
                "Choose one of the applications GetApplicationsForAsync returns for the type.");
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData);
        }
        try
        {
            await MimeAppsWriter.SetDefaultAsync(Path.Combine(options.ConfigHome, "mimeapps.list"), type, applicationId,
                key => mime.Unalias(key).Equals(type, StringComparison.OrdinalIgnoreCase), cancellationToken).ConfigureAwait(false);
            return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        }
        catch (UnauthorizedAccessException) { return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, PlatformDiagnostic.FromErrno(13)); }
        catch (IOException error)
        {
            _application.Diagnose("mimeapps-write-failed", "The user's mimeapps.list could not be written.", "Check that XDG_CONFIG_HOME is writable.");
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IOError, Errno(error));
        }
    }

    public ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string applicationId, ImmutableArray<string> pathsOrUris, CancellationToken cancellationToken = default)
    {
        DesktopApplicationNames.ApplicationId(applicationId, nameof(applicationId));
        DesktopApplicationNames.PathsOrUris(pathsOrUris, nameof(pathsOrUris));
        return Offload(() => LaunchCoreAsync(applicationId, pathsOrUris, cancellationToken), cancellationToken);
    }

    private async ValueTask<PlatformResult<PlatformUnit>> LaunchCoreAsync(string applicationId, ImmutableArray<string> pathsOrUris, CancellationToken cancellationToken)
    {
        DesktopApplicationNames.ApplicationId(applicationId, nameof(applicationId));
        DesktopApplicationNames.PathsOrUris(pathsOrUris, nameof(pathsOrUris));
        cancellationToken.ThrowIfCancellationRequested();
        if (Sandboxed<PlatformUnit>() is { } sandboxed) return sandboxed;
        // Only an installed entry starts; an ID is never a path or a command.
        if (!IsDesktopId(applicationId) || new DesktopEntryCatalog(options, _locale).Get(applicationId) is not { } entry) return Unknown<PlatformUnit>();
        if (entry.Terminal)
        {
            _application.Diagnose("desktop-application-terminal", "The application runs in a terminal, which this provider does not start.",
                "Start a terminal emulator with the program yourself, or choose an application without Terminal=true.");
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        if (entry.DBusActivatable && await ActivateAsync(entry, pathsOrUris, cancellationToken).ConfigureAwait(false) is { } activated) return activated;
        if (entry.Exec is null || DesktopExec.Split(entry.Exec) is not { IsEmpty: false } exec)
        {
            _application.Diagnose("desktop-application-exec-invalid", $"The Exec key of {entry.Id} cannot be parsed.", "Fix the quoting of the desktop entry's Exec key.");
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData);
        }
        if (DesktopExec.Expand(exec, entry, pathsOrUris) is not { } commands)
        {
            _application.Diagnose("desktop-application-local-files-only", $"{entry.Id} opens only local files.", "Pass local paths or file: URIs to this application.");
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData);
        }
        string? directory = entry.WorkingDirectory;
        if (directory is not null && !Path.IsPathFullyQualified(directory)) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData);
        if (directory is not null && !Directory.Exists(directory)) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IOError, PlatformDiagnostic.FromErrno(2));
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var command in commands)
        {
            if (command.IsEmpty || DesktopEntry.FindProgram(command[0], options.SearchPath) is not { } program)
                return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IOError, PlatformDiagnostic.FromErrno(2));
            var start = new ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = directory ?? "" };
            foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
            try { using var process = Process.Start(start); }
            catch (Win32Exception error)
            {
                return new PlatformResult<PlatformUnit>.Failed(error.NativeErrorCode is 1 or 13 ? PlatformFailureCode.PermissionDenied : PlatformFailureCode.Other,
                    PlatformDiagnostic.FromErrno(error.NativeErrorCode));
            }
        }
        return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    }

    // org.freedesktop.Application on the bus name of the desktop ID, at the path derived from it. Null when no session
    // bus is reachable and the Exec key can start the application instead.
    // https://specifications.freedesktop.org/desktop-entry/latest/dbus.html
    private async ValueTask<PlatformResult<PlatformUnit>?> ActivateAsync(DesktopEntry entry, ImmutableArray<string> items, CancellationToken cancellationToken)
    {
        string name = entry.Id[..^DesktopSuffix.Length];
        if (!IsBusName(name)) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData);
        var connected = options.BusAddress is { } address
            ? await Runic.Platform.Linux.DBus.DBusConnection.ConnectToAddressAsync(address, cancellationToken).ConfigureAwait(false)
            : await Runic.Platform.Linux.DBus.DBusConnection.ConnectAsync(DBusBus.Session, cancellationToken).ConfigureAwait(false);
        if (connected is not PlatformResult<Runic.Platform.Linux.DBus.DBusConnection>.Success(var bus))
            return entry.Exec is null ? new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable) : null;
        await using (bus.ConfigureAwait(false))
        {
            var proxy = new DBusProxy(bus, name, new DBusObjectPath("/" + name.Replace('.', '/').Replace('-', '_')));
            var platformData = DBusValue.VariantDictionary([]);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                if (items.IsEmpty) await proxy.CallAsync("org.freedesktop.Application", "Activate", [platformData], cancellationToken: deadline.Token).ConfigureAwait(false);
                else await proxy.CallAsync("org.freedesktop.Application", "Open", [DBusValue.StringArray(items.Select(DesktopExec.ToUri)), platformData],
                    cancellationToken: deadline.Token).ConfigureAwait(false);
                return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
            }
            catch (DBusException error)
            {
                _application.Diagnose("desktop-application-activation-failed", $"D-Bus activation of {entry.Id} failed with {error.ErrorName}.",
                    "Check that the application's D-Bus service file is installed and that it implements org.freedesktop.Application.");
                return new PlatformResult<PlatformUnit>.Failed(error.ErrorName == "org.freedesktop.DBus.Error.AccessDenied" ? PlatformFailureCode.PermissionDenied : PlatformFailureCode.Other,
                    new PlatformDiagnostic(error.ErrorName, 0));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other); }
        }
    }

    public ValueTask<PlatformResult<DesktopIconData?>> GetIconAsync(DesktopIconRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Offload(() => IconCoreAsync(request, cancellationToken), cancellationToken);
    }

    private async ValueTask<PlatformResult<DesktopIconData?>> IconCoreAsync(DesktopIconRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        List<string> names;
        if (request.IconName is { } iconName) names = [.. IconThemeLookup.DashFallbacks(iconName)];
        else if (request.ContentType is { } contentType)
        {
            if (!DesktopMimeType.IsValid(contentType)) return InvalidType<DesktopIconData?>();
            var mime = Mime();
            names = [.. new[] { mime.IconName(contentType), mime.GenericIconName(contentType) }.Distinct(StringComparer.Ordinal)];
        }
        else
        {
            string id = request.ApplicationId!;
            if (!IsDesktopId(id) || new DesktopEntryCatalog(options, _locale).Get(id) is not { } entry) return Unknown<DesktopIconData?>();
            if (entry.Icon is not { } icon) return new PlatformResult<DesktopIconData?>.Success(null);
            // An absolute Icon key names the file itself.
            if (Path.IsPathFullyQualified(icon)) return MediaType(icon) is null || !File.Exists(icon) ? new PlatformResult<DesktopIconData?>.Success(null) : Read(icon, null, request);
            foreach (var extension in (string[])[".png", ".svg", ".xpm"])
                if (icon.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) icon = icon[..^extension.Length];
            if (icon.Length == 0 || icon.Contains('/')) return new PlatformResult<DesktopIconData?>.Success(null);
            names = [.. IconThemeLookup.DashFallbacks(icon)];
        }
        string theme = await IconThemeAsync(cancellationToken).ConfigureAwait(false);
        var found = new IconThemeLookup(options, theme).Find(names, request.Size, request.Scale);
        return found is null ? new PlatformResult<DesktopIconData?>.Success(null) : Read(found.Path, found.Directory, request);
    }

    private static PlatformResult<DesktopIconData?> Read(string path, IconThemeLookup.ThemeDirectory? directory, DesktopIconRequest request)
    {
        string mediaType = MediaType(path)!;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaximumIconBytes) return new PlatformResult<DesktopIconData?>.Failed(PlatformFailureCode.TooLarge);
            byte[] data = File.ReadAllBytes(path);
            if (data.Length > MaximumIconBytes) return new PlatformResult<DesktopIconData?>.Failed(PlatformFailureCode.TooLarge);
            int size = directory is { Type: not IconThemeLookup.DirectoryType.Scalable } fixedDirectory ? fixedDirectory.Size
                : directory is null && mediaType == "image/png" && PngWidth(data) is { } width ? width : request.Size;
            return new PlatformResult<DesktopIconData?>.Success(new DesktopIconData(mediaType, ImmutableArray.Create(data), size));
        }
        catch (UnauthorizedAccessException) { return new PlatformResult<DesktopIconData?>.Failed(PlatformFailureCode.PermissionDenied, PlatformDiagnostic.FromErrno(13)); }
        catch (FileNotFoundException) { return new PlatformResult<DesktopIconData?>.Success(null); }
        catch (IOException error) { return new PlatformResult<DesktopIconData?>.Failed(PlatformFailureCode.IOError, Errno(error)); }
    }

    private static string? MediaType(string path) =>
        path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" : null;

    // The width in a PNG's IHDR chunk, for an unthemed icon whose directory names no size.
    private static int? PngWidth(ReadOnlySpan<byte> data) =>
        data.Length >= 24 && data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]) && data[12..16].SequenceEqual("IHDR"u8)
            && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data[16..20]) is > 0 and <= 65536 and var width ? width : null;

    // The configured theme, else the GNOME icon-theme setting through the Settings portal, else KDE's kdeglobals,
    // else hicolor.
    private async ValueTask<string> IconThemeAsync(CancellationToken cancellationToken)
    {
        if (options.IconTheme is { } configured && IconThemeLookup.IsThemeName(configured)) return configured;
        if (options.ReadPortalIconTheme && await PortalIconThemeAsync(cancellationToken).ConfigureAwait(false) is { } portal) return portal;
        foreach (var directory in (ImmutableArray<string>)[options.ConfigHome, .. options.ConfigDirectories])
            if (DesktopKeyFile.Load(Path.Combine(directory, "kdeglobals"))?.String("Icons", "Theme")?.Trim() is { } kde && IconThemeLookup.IsThemeName(kde)) return kde;
        return IconThemeLookup.DefaultTheme;
    }

    private async ValueTask<string?> PortalIconThemeAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            if (_portalTheme is { } cached && Stopwatch.GetElapsedTime(cached.Read) < ThemeLifetime) return cached.Theme;
        string? theme = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            using var session = await PortalConnection.OpenAsync(options.BusAddress, "org.freedesktop.portal.Desktop", _application, deadline.Token).ConfigureAwait(false);
            var values = await new Protocol.Settings(session.Connection, session.Destination, "/org/freedesktop/portal/desktop")
                .ReadAllAsync(["org.gnome.desktop.interface"]).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (values.TryGetValue("org.gnome.desktop.interface", out var settings) && settings.TryGetValue("icon-theme", out var value)
                && value.Type == VariantValueType.String && IconThemeLookup.IsThemeName(value.GetString()))
                theme = value.GetString();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is DBusExceptionBase or TimeoutException or NativeBackendUnavailableException or InvalidOperationException) { }
        lock (_gate) _portalTheme = (theme, Stopwatch.GetTimestamp());
        return theme;
    }

    // The directory scans, file reads and process starts block, so requests run on the thread pool, as the trash does.
    private static ValueTask<PlatformResult<T>> Offload<T>(Func<ValueTask<PlatformResult<T>>> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(Task.Run(() => work().AsTask(), cancellationToken));
    }

    private (DesktopEntryCatalog, DesktopAssociations, SharedMimeDatabase) Load()
    {
        var mime = Mime();
        var catalog = new DesktopEntryCatalog(options, _locale);
        return (catalog, new DesktopAssociations(options, catalog, mime), mime);
    }

    // The parsed database, reloaded when a source file's size or modification time changes.
    private SharedMimeDatabase Mime()
    {
        var stamp = new StringBuilder();
        foreach (var path in SharedMimeDatabase.SourceFiles(options.DataSearch))
        {
            var info = new FileInfo(path);
            stamp.Append(info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks};" : "-;");
        }
        string current = stamp.ToString();
        lock (_gate)
            if (_mime is { } cached && cached.Stamp == current) return cached.Database;
        var database = SharedMimeDatabase.Load(options.DataSearch);
        lock (_gate) _mime = (database, current);
        return database;
    }

    private PlatformResult<T>? Sandboxed<T>()
    {
        if (!options.Sandboxed) return null;
        _application.Diagnose("desktop-applications-sandboxed", "Inside Flatpak or Snap the host's applications and default applications are out of reach.",
            "Open files and URIs with IDesktopFileLauncher (LaunchAsync, LaunchUriAsync), which asks the desktop portal.");
        return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    }

    private PlatformResult<T> Unknown<T>()
    {
        _application.Diagnose("desktop-application-unknown", "No usable desktop entry has this ID.",
            "Pass a desktop file ID that GetApplicationsAsync or GetApplicationsForAsync returned, such as org.gnome.TextEditor.desktop.");
        return new PlatformResult<T>.Failed(PlatformFailureCode.InvalidData);
    }

    private static PlatformResult<T> InvalidType<T>() => new PlatformResult<T>.Failed(PlatformFailureCode.InvalidData);

    private static bool IsDesktopId(string id) =>
        id.Length > DesktopSuffix.Length && id.EndsWith(DesktopSuffix, StringComparison.Ordinal) && id.IndexOfAny(['/', ';', '\n', '\r', '\0']) < 0 && id[0] != '.';

    // A well-known D-Bus name: at least two elements of [A-Za-z0-9_-], none starting with a digit.
    private static bool IsBusName(string name) =>
        name.Length <= 255 && name.Split('.') is { Length: >= 2 } elements
        && elements.All(static element => element.Length > 0 && !char.IsAsciiDigit(element[0]) && element.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));

    private static PlatformResult<T> Failure<T>(int errno) =>
        new PlatformResult<T>.Failed(errno switch
        {
            1 or 13 => PlatformFailureCode.PermissionDenied,
            // A missing file stays IOError, as IDesktopApplications.GetContentTypeAsync documents.
            2 => PlatformFailureCode.IOError,
            _ => PlatformFailureCode.Other,
        }, PlatformDiagnostic.FromErrno(errno));

    // .NET reports most Unix errors with the errno in the HResult's low bits.
    internal static PlatformDiagnostic? Errno(IOException error) =>
        (error.HResult & 0xFFFF) is > 0 and < 4096 and var errno ? PlatformDiagnostic.FromErrno(errno) : null;

    private static ValueTask<PlatformResult<T>> Result<T>(PlatformResult<T> result) => ValueTask.FromResult(result);

    internal enum FileKind { None, Regular, Directory, Link, CharacterDevice, BlockDevice, Fifo, Socket, Other }

    // statx(2) reports a file's kind with one layout on every architecture; stat(2)'s layout differs between them.
    internal static (int Error, FileKind Kind) Stat(string path, bool follow)
    {
        Span<byte> buffer = stackalloc byte[256];
        int result;
        unsafe
        {
            fixed (byte* pointer = buffer)
                result = StatX(-100 /* AT_FDCWD */, path, follow ? 0 : 0x100 /* AT_SYMLINK_NOFOLLOW */, 0x1 /* STATX_TYPE */, pointer);
        }
        if (result != 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno is not (38 /* ENOSYS */ or 1 /* EPERM: a seccomp filter */)) return (errno, FileKind.None);
            return ManagedStat(path, follow);
        }
        int mode = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buffer[28..]);
        if (!BitConverter.IsLittleEndian) mode = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(buffer[28..]);
        return (0, (mode & 0xF000) switch
        {
            0x8000 => FileKind.Regular,
            0x4000 => FileKind.Directory,
            0xA000 => FileKind.Link,
            0x2000 => FileKind.CharacterDevice,
            0x6000 => FileKind.BlockDevice,
            0x1000 => FileKind.Fifo,
            0xC000 => FileKind.Socket,
            _ => FileKind.Other,
        });
    }

    private static (int, FileKind) ManagedStat(string path, bool follow)
    {
        var info = new FileInfo(path);
        if (!follow && info.LinkTarget is not null) return (0, FileKind.Link);
        if (Directory.Exists(path)) return (0, FileKind.Directory);
        return File.Exists(path) ? (0, FileKind.Regular) : (2, FileKind.None);
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int StatX(int directory, string path, int flags, uint mask, byte* buffer);
}

// Changes one key of [Default Applications] in the user's mimeapps.list and nothing else: every other byte of the file
// stays as it was. The new file replaces the old one by rename, so a reader never sees half of it.
internal static class MimeAppsWriter
{
    internal static async Task SetDefaultAsync(string path, string type, string applicationId, Func<string, bool> sameType, CancellationToken cancellationToken)
    {
        string target = Target(path);
        string text = File.Exists(target) ? await File.ReadAllTextAsync(target, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false) : "";
        string updated = Update(text, type, applicationId, sameType);
        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, "." + Path.GetFileName(target) + ".runic-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(temporary, updated, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows() && File.Exists(target)) File.SetUnixFileMode(temporary, File.GetUnixFileMode(target));
            File.Move(temporary, target, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    // A symbolic link, as dotfile managers create, keeps pointing at the file it names, even before that file exists.
    private static string Target(string path)
    {
        for (int hops = 0; hops < 40 && new FileInfo(path).LinkTarget is { } link; hops++)
            path = Path.GetFullPath(link, Path.GetDirectoryName(path)!);
        return path;
    }

    // Every key of the group that names the type, an alias or another spelling included, gets the new value, because a
    // reader merges them and the first would still win.
    internal static string Update(string text, string type, string applicationId, Func<string, bool>? sameType = null)
    {
        sameType ??= key => key.Equals(type, StringComparison.OrdinalIgnoreCase);
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string entry = type + "=" + applicationId + ";";
        var lines = new List<string>();
        for (int start = 0; start < text.Length;)
        {
            int end = text.IndexOf('\n', start);
            end = end < 0 ? text.Length : end + 1;
            lines.Add(text[start..end]);
            start = end;
        }
        int group = -1, lastKey = -1;
        bool replaced = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string content = lines[i].TrimEnd('\n', '\r').Trim();
            if (content.StartsWith('['))
            {
                if (group >= 0 && content != "[" + DesktopAssociations.DefaultGroup + "]") break;
                if (content == "[" + DesktopAssociations.DefaultGroup + "]") { group = i; lastKey = i; }
                continue;
            }
            if (group < 0 || content.Length == 0 || content[0] == '#') continue;
            lastKey = i;
            int equals = content.IndexOf('=');
            if (equals > 0 && sameType(content[..equals].TrimEnd()))
            {
                string ending = lines[i][lines[i].TrimEnd('\n', '\r').Length..];
                lines[i] = entry + (ending.Length > 0 ? ending : "");
                replaced = true;
            }
        }
        if (replaced) return string.Concat(lines);
        if (group >= 0)
        {
            // After the group's last key, so blank lines and comments that end the group stay where they are.
            if (!lines[lastKey].EndsWith('\n')) lines[lastKey] += newline;
            lines.Insert(lastKey + 1, entry + newline);
            return string.Concat(lines);
        }
        var result = new StringBuilder(text);
        if (text.Length > 0 && !text.EndsWith('\n')) result.Append(newline);
        if (text.Length > 0) result.Append(newline);
        return result.Append('[').Append(DesktopAssociations.DefaultGroup).Append(']').Append(newline).Append(entry).Append(newline).ToString();
    }
}
