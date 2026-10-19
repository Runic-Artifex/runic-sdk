using System.Collections.Immutable;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace Runic.Platform.Windows;

// The shell's association database: content types from the registry's extension keys and MIME database, handlers
// from SHAssocEnumHandlers, defaults from AssocQueryString, launches through the application's own registration for
// the type (ShellExecuteEx with its class) or the handler (IAssocHandler), the Start menu's
// applications from shell:AppsFolder, and icons from the shell. Each request runs on a single-threaded apartment of
// its own, as the Recycle Bin does. Windows lets only the user choose default applications, so
// SetDefaultApplicationAsync reports PermissionDenied and points to Settings.
[SupportedOSPlatform("windows8.0")]
internal sealed unsafe class WindowsDesktopApplications(IWindowsClasses classes) : IDesktopApplications
{
    // The largest icon Windows stores; larger requests get this size rather than a blurred enlargement.
    internal const int MaximumIconPixels = 256;
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    // The diagnostic of SetDefaultApplicationAsync: Windows protects the user's choice from programs.
    internal static readonly PlatformDiagnostic DefaultsPolicy = new("HRESULT", unchecked((uint)E_ACCESSDENIED),
        "Windows lets only the user choose default applications. Open Settings at ms-settings:defaultapps to let the user choose.");

    public ValueTask<PlatformResult<DesktopContentType>> GetContentTypeAsync(string path, DesktopContentDetection detection = DesktopContentDetection.NameAndContent,
        CancellationToken cancellationToken = default)
    {
        DesktopApplicationNames.ContentPath(path, detection, nameof(path));
        return Run(() => ContentType(path, detection), cancellationToken);
    }

    public ValueTask<PlatformResult<ImmutableArray<DesktopApplicationInfo>>> GetApplicationsAsync(CancellationToken cancellationToken = default) =>
        Run<ImmutableArray<DesktopApplicationInfo>>(() =>
        {
            var entries = WindowsShellAssociations.AppsFolder();
            if (entries is null) return new PlatformResult<ImmutableArray<DesktopApplicationInfo>>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            return Success(entries.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new DesktopApplicationInfo(entry.Id, entry.Name)).ToImmutableArray());
        }, cancellationToken);

    public ValueTask<PlatformResult<ImmutableArray<DesktopApplicationInfo>>> GetApplicationsForAsync(string contentType, CancellationToken cancellationToken = default)
    {
        DesktopApplicationNames.ContentType(contentType, nameof(contentType));
        return Run<ImmutableArray<DesktopApplicationInfo>>(() =>
        {
            if (!WindowsContentTypes.TryResolve(contentType, classes, out var association)) return InvalidData<ImmutableArray<DesktopApplicationInfo>>();
            return Success(association is null ? [] : Applications(association.Value).List);
        }, cancellationToken);
    }

    public ValueTask<PlatformResult<DesktopApplicationInfo?>> GetDefaultApplicationAsync(string contentType, CancellationToken cancellationToken = default)
    {
        DesktopApplicationNames.ContentType(contentType, nameof(contentType));
        return Run<DesktopApplicationInfo?>(() =>
        {
            if (!WindowsContentTypes.TryResolve(contentType, classes, out var association)) return InvalidData<DesktopApplicationInfo?>();
            return Success(association is null ? null : Applications(association.Value).Default);
        }, cancellationToken);
    }

    public ValueTask<PlatformResult<PlatformUnit>> SetDefaultApplicationAsync(string contentType, string applicationId, CancellationToken cancellationToken = default)
    {
        DesktopApplicationNames.ContentType(contentType, nameof(contentType));
        DesktopApplicationNames.ApplicationId(applicationId, nameof(applicationId));
        return Run<PlatformUnit>(() =>
        {
            if (!WindowsContentTypes.TryResolve(contentType, classes, out var association) || association is null) return InvalidData<PlatformUnit>();
            if (!Applications(association.Value).List.Any(application => WindowsShellAssociations.SameId(application.Id, applicationId)))
                return InvalidData<PlatformUnit>();
            // The choice is the user's: Windows ignores and resets defaults that programs write (UserChoice hashes).
            return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, DefaultsPolicy);
        }, cancellationToken);
    }

    public ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string applicationId, ImmutableArray<string> pathsOrUris, CancellationToken cancellationToken = default)
    {
        DesktopApplicationNames.ApplicationId(applicationId, nameof(applicationId));
        DesktopApplicationNames.PathsOrUris(pathsOrUris, nameof(pathsOrUris));
        return Run(() => Launch(applicationId, pathsOrUris), cancellationToken);
    }

    public ValueTask<PlatformResult<DesktopIconData?>> GetIconAsync(DesktopIconRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Windows has no icon themes: a themed name has no icon.
        if (request.IconName is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<DesktopIconData?>>(new PlatformResult<DesktopIconData?>.Success(null));
        }
        return Run(() => Icon(request), cancellationToken);
    }

    private PlatformResult<DesktopContentType> ContentType(string path, DesktopContentDetection detection)
    {
        DesktopContentType? type;
        if (detection == DesktopContentDetection.NameOnly)
            type = WindowsContentTypes.FromExtension(Path.GetExtension(path), classes) ?? WindowsContentTypes.FromMime(WindowsContentTypes.Generic, classes);
        else
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            { return new PlatformResult<DesktopContentType>.Failed(PlatformFailureCode.IoError, PlatformDiagnostic.FromWin32Error(2)); }
            catch (UnauthorizedAccessException) { return new PlatformResult<DesktopContentType>.Failed(PlatformFailureCode.PermissionDenied); }
            catch (IOException error) { return new PlatformResult<DesktopContentType>.Failed(PlatformFailureCode.IoError, PlatformDiagnostic.FromHResult(error.HResult)); }
            if ((attributes & FileAttributes.Directory) != 0) type = WindowsContentTypes.Folder();
            else if ((type = WindowsContentTypes.FromExtension(Path.GetExtension(path), classes)) is null)
            {
                // The name does not decide: urlmon sniffs the first bytes.
                byte[] head = new byte[256];
                int read;
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                { return new PlatformResult<DesktopContentType>.Failed(PlatformFailureCode.IoError, PlatformDiagnostic.FromWin32Error(2)); }
                catch (UnauthorizedAccessException) { return new PlatformResult<DesktopContentType>.Failed(PlatformFailureCode.PermissionDenied); }
                catch (IOException error) { return new PlatformResult<DesktopContentType>.Failed(PlatformFailureCode.IoError, PlatformDiagnostic.FromHResult(error.HResult)); }
                string? mime = WindowsShellAssociations.Sniff(head.AsSpan(0, read));
                type = WindowsContentTypes.FromMime(mime is not null && WindowsContentTypes.IsMime(mime) ? mime : WindowsContentTypes.Generic, classes);
            }
        }
        if (WindowsContentTypes.TryResolve(type.Id, classes, out var association) && association is not null)
            type = type with { Description = WindowsShellAssociations.Query(association.Value, ASSOCSTR.ASSOCSTR_FRIENDLYDOCNAME) };
        return new PlatformResult<DesktopContentType>.Success(type);
    }

    // The handlers of an association, the default first.
    private static (ImmutableArray<DesktopApplicationInfo> List, DesktopApplicationInfo? Default) Applications(Association association)
    {
        var handlers = WindowsShellAssociations.Handlers(association, recommended: true, match: null, out _);
        var fallback = WindowsShellAssociations.Default(association);
        DesktopApplicationInfo? preferred = null;
        if (fallback is { } chosen)
        {
            int index = handlers.FindIndex(handler => WindowsShellAssociations.SameId(handler.Id, chosen.Id));
            // The handler's own ID and name, so the default's ID is the one the list and LaunchAsync use.
            var handler = index >= 0 ? handlers[index] : chosen;
            if (index >= 0) handlers.RemoveAt(index);
            preferred = Info(handler);
        }
        var list = ImmutableArray.CreateBuilder<DesktopApplicationInfo>(handlers.Count + 1);
        if (preferred is not null) list.Add(preferred);
        foreach (var handler in handlers) list.Add(Info(handler));
        return (list.ToImmutable(), preferred);
    }

    private static DesktopApplicationInfo Info(WindowsShellAssociations.Handler handler) => new(handler.Id, handler.Name);

    private PlatformResult<PlatformUnit> Launch(string applicationId, ImmutableArray<string> items)
    {
        if (items.IsEmpty)
        {
            // Without items the application starts as the Start menu starts it.
            if (WindowsShellAssociations.FindEntry(WindowsShellAssociations.AppsFolder(), applicationId) is not { } found) return InvalidData<PlatformUnit>();
            return WindowsShellAssociations.StartAppsFolderEntry(found.ParsingName, out int status) ? Success(default(PlatformUnit))
                : new PlatformResult<PlatformUnit>.Failed(status is 5 ? PlatformFailureCode.PermissionDenied : PlatformFailureCode.IoError,
                    new PlatformDiagnostic("ShellExecute", status));
        }
        var groups = Group(items, Directory.Exists);
        if (groups is null) return InvalidData<PlatformUnit>();
        // Every group needs the application among its handlers before anything starts.
        var handlers = new List<(nint Handler, Association Association, List<string> Items)>();
        try
        {
            foreach (var (association, members) in groups)
            {
                // The type's own registration of the application first: an Open With entry for the same executable runs
                // its generic command rather than the verb the application registered for the type.
                WindowsShellAssociations.Handlers(association, recommended: true, match: applicationId, out IAssocHandler* handler);
                if (handler is null) WindowsShellAssociations.Handlers(association, recommended: false, match: applicationId, out handler);
                if (handler is null) return InvalidData<PlatformUnit>();
                handlers.Add(((nint)handler, association, members));
            }
            foreach (var (handler, association, members) in handlers)
            {
                // A file goes to the class of the application's registration for its extension, run directly: the
                // handler's own Invoke takes the extension's default path, which opens the Open With chooser when the
                // user has not chosen a default and several applications open the type. URIs, folders and registrations
                // the class lookup cannot find go to the handler.
                string? registration = association.Kind == AssociationKind.Extension
                    ? WindowsShellAssociations.RegistrationFor(association.Name, applicationId, classes) : null;
                int hr = 0;
                if (registration is null) hr = WindowsShellAssociations.InvokeAll((IAssocHandler*)handler, members);
                else foreach (string item in members) if ((hr = WindowsShellAssociations.ExecuteRegistration(registration, item)) < 0) break;
                if (hr < 0) return FromHResult(hr);
            }
            return Success(default(PlatformUnit));
        }
        finally
        {
            foreach (var (handler, _, _) in handlers) ((IAssocHandler*)handler)->Release();
        }
    }

    // Items grouped by the association that opens them, in order: a path by its extension (a directory by the
    // Directory class), a file: URI as its path, any other URI by its scheme. null: an item has no type, such as a
    // file without an extension.
    internal static List<(Association Association, List<string> Items)>? Group(ImmutableArray<string> items, Func<string, bool> isDirectory)
    {
        var groups = new List<(Association Association, List<string> Items)>();
        foreach (string item in items)
        {
            string value = item;
            Association association;
            if (!DesktopApplicationNames.IsPath(item))
            {
                var uri = new Uri(item, UriKind.Absolute);
                if (uri.IsFile && Path.IsPathFullyQualified(uri.LocalPath)) value = uri.LocalPath;
                else
                {
                    association = new(AssociationKind.Protocol, uri.Scheme.ToLowerInvariant());
                    Add(association, value);
                    continue;
                }
            }
            if (isDirectory(value)) association = new(AssociationKind.Class, "Directory");
            else
            {
                string extension = Path.GetExtension(value);
                if (!WindowsContentTypes.IsExtension(extension)) return null;
                association = new(AssociationKind.Extension, extension.ToLowerInvariant());
            }
            Add(association, value);
        }
        return groups;

        void Add(Association association, string value)
        {
            int index = groups.FindIndex(group => group.Association == association);
            if (index >= 0) groups[index].Items.Add(value);
            else groups.Add((association, [value]));
        }
    }

    private PlatformResult<DesktopIconData?> Icon(DesktopIconRequest request)
    {
        int pixels = Math.Min(request.Size * request.Scale, MaximumIconPixels);
        byte[]? png = null;
        if (request.ApplicationId is { } id)
        {
            if (Path.IsPathFullyQualified(id)) png = File.Exists(id) ? WindowsShellAssociations.ExtractIcon(id, 0, pixels) : null;
            else if (WindowsShellAssociations.FindEntry(WindowsShellAssociations.AppsFolder(), id) is { } entry)
                png = WindowsShellAssociations.ShellItemIcon(@"shell:AppsFolder\" + entry.ParsingName, pixels);
        }
        else if (request.ContentType is { } contentType)
        {
            if (!WindowsContentTypes.TryResolve(contentType, classes, out var association)) return InvalidData<DesktopIconData?>();
            if (association is not null
                && WindowsContentTypes.ParseIconLocation(WindowsShellAssociations.Query(association.Value, ASSOCSTR.ASSOCSTR_DEFAULTICON)) is var (file, index))
                png = WindowsShellAssociations.ExtractIcon(file, index, pixels);
            if (png is null && association is { Kind: AssociationKind.Extension } extension)
                png = WindowsShellAssociations.ExtensionIcon(extension.Name, pixels);
        }
        return Success(png is null ? null : new DesktopIconData("image/png", ImmutableArray.Create(png), request.Size));
    }

    internal static PlatformResult<PlatformUnit> FromHResult(int hr) => new PlatformResult<PlatformUnit>.Failed(hr switch
    {
        E_ACCESSDENIED => PlatformFailureCode.PermissionDenied,
        // The shell reports a cancelled consent or elevation prompt this way.
        unchecked((int)0x800704C7) => PlatformFailureCode.UserDismissed,
        // ERROR_NO_ASSOCIATION: the registration has no command for the file.
        unchecked((int)0x80070483) => PlatformFailureCode.InvalidData,
        _ => PlatformFailureCode.IoError,
    }, PlatformDiagnostic.FromHResult(hr));

    private static PlatformResult<T> Success<T>(T value) => new PlatformResult<T>.Success(value);
    private static PlatformResult<T> InvalidData<T>() => new PlatformResult<T>.Failed(PlatformFailureCode.InvalidData);

    // Each request runs on a single-threaded apartment of its own: the shell's association and item objects expect one.
    private static ValueTask<PlatformResult<T>> Run<T>(Func<PlatformResult<T>> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WindowsSupport.IsAvailable) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<PlatformResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(WindowsSupport.IsAvailable ? Apartment(work) : throw new PlatformNotSupportedException()); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "Runic desktop applications" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return new(completion.Task);
    }

    private static PlatformResult<T> Apartment<T>(Func<PlatformResult<T>> work)
    {
        if (PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED).Value < 0)
            return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        try { return work(); }
        finally { PInvoke.CoUninitialize(); }
    }
}
