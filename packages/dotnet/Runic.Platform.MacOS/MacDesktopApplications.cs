using System.Collections.Immutable;
using System.Runtime.InteropServices;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// Launch Services through NSWorkspace, with uniform type identifiers (UTType) for content types: the handlers and the
// default for a type or URL scheme, launches of an application by bundle identifier, the user's default (which macOS
// may confirm with the user), and icons rendered to PNG. The application list scans the Applications folders, as
// Launchpad does. NSWorkspace and UTType are thread-safe, so requests run on the thread pool without the AppKit event
// loop.
internal sealed partial class MacDesktopApplications : IDesktopApplications
{
    // The largest icon macOS stores (1024 points for an application at scale 1).
    internal const int MaximumIconPixels = 1024;
    internal const string SchemePrefix = "x-scheme-handler/", Data = "public.data";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    private static readonly Lazy<bool> Loaded = new(static () => MacAppKit.IsAvailable
        && NativeLibrary.TryLoad("/System/Library/Frameworks/UniformTypeIdentifiers.framework/UniformTypeIdentifiers", out _)
        && Class("UTType") != 0 && Class("NSWorkspaceOpenConfiguration") != 0);

    // The folders Launchpad shows, in order: an application found again later is the same bundle identifier.
    internal static ImmutableArray<string> ApplicationFolders => ["/Applications", "/System/Applications",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications")];

    public ValueTask<PlatformResult<DesktopContentType>> GetContentTypeAsync(string path, DesktopContentDetection detection = DesktopContentDetection.NameAndContent,
        CancellationToken cancellationToken = default)
    {
        DesktopApplicationNames.ContentPath(path, detection, nameof(path));
        return Run(() => ContentType(path, detection), cancellationToken);
    }

    public ValueTask<PlatformResult<ImmutableArray<DesktopApplicationInfo>>> GetApplicationsAsync(CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            var found = new List<DesktopApplicationInfo>();
            foreach (string bundle in BundlePaths(ApplicationFolders))
            {
                using var pool = new Pool();
                if (Info(Arg(Class("NSBundle"), Sel("bundleWithPath:"), String(bundle))) is { } info && !found.Exists(existing => existing.Id == info.Id))
                    found.Add(info);
            }
            return Success(found.OrderBy(info => info.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(info => info.Id, StringComparer.Ordinal).ToImmutableArray());
        }, cancellationToken);

    public ValueTask<PlatformResult<ImmutableArray<DesktopApplicationInfo>>> GetApplicationsForAsync(string contentType, CancellationToken cancellationToken = default)
    {
        DesktopApplicationNames.ContentType(contentType, nameof(contentType));
        return Run(() => Handlers(contentType) is { } handlers ? Success(handlers.List) : InvalidData<ImmutableArray<DesktopApplicationInfo>>(), cancellationToken);
    }

    public ValueTask<PlatformResult<DesktopApplicationInfo?>> GetDefaultApplicationAsync(string contentType, CancellationToken cancellationToken = default)
    {
        DesktopApplicationNames.ContentType(contentType, nameof(contentType));
        return Run(() => Handlers(contentType) is { } handlers ? Success(handlers.Default) : InvalidData<DesktopApplicationInfo?>(), cancellationToken);
    }

    public async ValueTask<PlatformResult<PlatformUnit>> SetDefaultApplicationAsync(string contentType, string applicationId, CancellationToken cancellationToken = default)
    {
        DesktopApplicationNames.ContentType(contentType, nameof(contentType));
        DesktopApplicationNames.ApplicationId(applicationId, nameof(applicationId));
        var completion = new TaskCompletionSource<PlatformResult<PlatformUnit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = await Run(() =>
        {
            var kind = Classify(contentType);
            if (kind is null || Handlers(contentType) is not { } handlers || !handlers.List.Any(handler => handler.Id == applicationId)) return InvalidData<PlatformUnit>();
            nint workspace = Workspace(), application = ApplicationUrl(applicationId);
            if (application == 0) return InvalidData<PlatformUnit>();
            // macOS may ask the user to confirm, for a web browser for example; the completion waits for the answer.
            var block = MacDesktopBlock.Create(error => completion.TrySetResult(error == 0 ? Success(default(PlatformUnit)) : MapDefaultError(ErrorDiagnostic(error))));
            try
            {
                if (kind.Value.Kind == ContentKind.Scheme)
                    Args3(workspace, Sel("setDefaultApplicationAtURL:toOpenURLsWithScheme:completionHandler:"), application, String(kind.Value.Value), block);
                else
                    Args3(workspace, Sel("setDefaultApplicationAtURL:toOpenContentType:completionHandler:"), application, UniformType(kind.Value), block);
            }
            finally { MacDesktopBlock.Release(block); }
            return Success(default(PlatformUnit));
        }, cancellationToken).ConfigureAwait(false);
        return started is PlatformResult<PlatformUnit>.Success ? await completion.Task.ConfigureAwait(false) : started;
    }

    public async ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string applicationId, ImmutableArray<string> pathsOrUris, CancellationToken cancellationToken = default)
    {
        DesktopApplicationNames.ApplicationId(applicationId, nameof(applicationId));
        DesktopApplicationNames.PathsOrUris(pathsOrUris, nameof(pathsOrUris));
        var completion = new TaskCompletionSource<PlatformResult<PlatformUnit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = await Run(() =>
        {
            // Only an application Launch Services knows by its bundle identifier, never a path.
            nint workspace = Workspace(), application = ApplicationUrl(applicationId);
            if (application == 0) return InvalidData<PlatformUnit>();
            nint urls = Send(Class("NSMutableArray"), Sel("array"));
            foreach (string item in pathsOrUris)
            {
                nint url = DesktopApplicationNames.IsPath(item) ? Url(item) : Arg(Class("NSURL"), Sel("URLWithString:"), String(item));
                if (url == 0) return InvalidData<PlatformUnit>();
                // A URI other than a file reaches only an application that opens its scheme.
                if (!DesktopApplicationNames.IsPath(item) && Bool(url, Sel("isFileURL")) == 0
                    && !Applications(Arg(workspace, Sel("URLsForApplicationsToOpenURL:"), url)).Exists(handler => handler.Id == applicationId))
                    return InvalidData<PlatformUnit>();
                Arg(urls, Sel("addObject:"), url);
            }
            nint configuration = Send(Class("NSWorkspaceOpenConfiguration"), Sel("configuration"));
            var block = MacDesktopBlock.Create((_, error) => completion.TrySetResult(error == 0 ? Success(default(PlatformUnit)) : MapLaunchError(ErrorDiagnostic(error))));
            try
            {
                if (pathsOrUris.IsEmpty) Args3(workspace, Sel("openApplicationAtURL:configuration:completionHandler:"), application, configuration, block);
                else Args4(workspace, Sel("openURLs:withApplicationAtURL:configuration:completionHandler:"), urls, application, configuration, block);
            }
            finally { MacDesktopBlock.Release(block); }
            return Success(default(PlatformUnit));
        }, cancellationToken).ConfigureAwait(false);
        return started is PlatformResult<PlatformUnit>.Success ? await completion.Task.ConfigureAwait(false) : started;
    }

    public ValueTask<PlatformResult<DesktopIconData?>> GetIconAsync(DesktopIconRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // macOS has no icon themes: a themed name has no icon.
        if (request.IconName is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<DesktopIconData?>>(Success<DesktopIconData?>(null));
        }
        return Run(() =>
        {
            int pixels = Math.Min(request.Size * request.Scale, MaximumIconPixels);
            nint workspace = Workspace(), image = 0;
            if (request.ApplicationId is { } id)
            {
                nint application = ApplicationUrl(id);
                if (application != 0) image = Arg(workspace, Sel("iconForFile:"), Send(application, Sel("path")));
            }
            else if (request.ContentType is { } contentType)
            {
                var kind = Classify(contentType);
                if (kind is null) return InvalidData<DesktopIconData?>();
                nint type = kind.Value.Kind == ContentKind.Scheme ? 0 : UniformType(kind.Value);
                if (type != 0) image = Arg(workspace, Sel("iconForContentType:"), type);
            }
            byte[]? png = image == 0 ? null : Png(image, pixels);
            return Success(png is null ? null : new DesktopIconData("image/png", ImmutableArray.Create(png), request.Size));
        }, cancellationToken);
    }

    internal enum ContentKind { Identifier, Mime, Extension, Scheme }

    // The forms a content type may take on macOS: a uniform type identifier (public.plain-text), a MIME type
    // (text/plain), a file name extension (.txt) or a URL scheme (x-scheme-handler/https). null: malformed.
    internal static (ContentKind Kind, string Value)? Classify(string id)
    {
        if (id.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string scheme = id[SchemePrefix.Length..];
            if (scheme.Length == 0 || !char.IsAsciiLetter(scheme[0])) return null;
            foreach (char c in scheme) if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.')) return null;
            return (ContentKind.Scheme, scheme.ToLowerInvariant());
        }
        if (id.StartsWith('.'))
        {
            string extension = id[1..];
            return extension.Length == 0 || extension.AsSpan().ContainsAny('/', ':') || extension.Any(char.IsWhiteSpace) ? null : (ContentKind.Extension, extension);
        }
        if (id.Contains('/'))
        {
            int slash = id.IndexOf('/');
            return slash == 0 || slash == id.Length - 1 || id.IndexOf('/', slash + 1) >= 0 || id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? null : (ContentKind.Mime, id);
        }
        // Reverse-DNS: letters, digits, '.' and '-' (Unicode letters are allowed).
        foreach (char c in id) if (!char.IsLetterOrDigit(c) && c is not ('.' or '-')) return null;
        return (ContentKind.Identifier, id);
    }

    // Supertypes nearest first: a nearer supertype conforms to more of the others.
    internal static ImmutableArray<string> OrderParents(IEnumerable<(string Id, int Depth)> supertypes) =>
        [.. supertypes.OrderByDescending(type => type.Depth).ThenBy(type => type.Id, StringComparer.Ordinal).Select(type => type.Id)];

    // The application bundles in the folders and their subfolders (Utilities), not inside other bundles.
    internal static IEnumerable<string> BundlePaths(IEnumerable<string> folders)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.Hidden };
        var pending = new Queue<(string Path, int Depth)>(folders.Where(Directory.Exists).Select(folder => (folder, 0)));
        while (pending.TryDequeue(out var folder))
        {
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(folder.Path, "*", options).Order(StringComparer.Ordinal).ToList(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (string child in children)
            {
                if (child.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) yield return child;
                else if (folder.Depth < 2) pending.Enqueue((child, folder.Depth + 1));
            }
        }
    }

    internal static PlatformResult<PlatformUnit> MapDefaultError(PlatformDiagnostic diagnostic) => (diagnostic.Domain, diagnostic.Code) switch
    {
        // NSUserCancelledError; userCanceledErr: the user declined the change.
        ("NSCocoaErrorDomain", 3072) or ("NSOSStatusErrorDomain", -128) => Failed(PlatformFailureCode.UserDismissed, diagnostic),
        // permErr, kLSNotAnApplicationErr is not a permission problem; NSFileWriteNoPermissionError; EPERM, EACCES.
        ("NSOSStatusErrorDomain", -54) or ("NSCocoaErrorDomain", 513) or ("NSPOSIXErrorDomain", 1 or 13) => Failed(PlatformFailureCode.PermissionDenied, diagnostic),
        _ => Failed(PlatformFailureCode.Other, diagnostic),
    };

    internal static PlatformResult<PlatformUnit> MapLaunchError(PlatformDiagnostic diagnostic) => (diagnostic.Domain, diagnostic.Code) switch
    {
        ("NSCocoaErrorDomain", 3072) or ("NSOSStatusErrorDomain", -128) => Failed(PlatformFailureCode.UserDismissed, diagnostic),
        // NSFileReadNoPermissionError; EPERM, EACCES; permErr.
        ("NSCocoaErrorDomain", 257) or ("NSPOSIXErrorDomain", 1 or 13) or ("NSOSStatusErrorDomain", -54) => Failed(PlatformFailureCode.PermissionDenied, diagnostic),
        _ => Failed(PlatformFailureCode.Other, diagnostic),
    };

    private static PlatformResult<DesktopContentType> ContentType(string path, DesktopContentDetection detection)
    {
        nint type;
        if (detection == DesktopContentDetection.NameOnly)
        {
            string extension = Path.GetExtension(path);
            type = extension.Length > 1 ? Arg(Class("UTType"), Sel("typeWithFilenameExtension:"), String(extension[1..])) : 0;
        }
        else
        {
            nint url = Send(Url(path), Sel("URLByResolvingSymlinksInPath"));
            if (ResourceValue(url, Sel("getResourceValue:forKey:error:"), out type, ContentTypeKey.Value, out nint error) == 0)
            {
                var diagnostic = error == 0 ? new PlatformDiagnostic("NSCocoaErrorDomain", 0) : ErrorDiagnostic(error);
                return (diagnostic.Domain, diagnostic.Code) switch
                {
                    ("NSCocoaErrorDomain", 4 or 260) or ("NSPOSIXErrorDomain", 2 or 20) =>
                        new PlatformResult<DesktopContentType>.Failed(PlatformFailureCode.IOError, PlatformDiagnostic.FromErrno(2)),
                    ("NSCocoaErrorDomain", 257) or ("NSPOSIXErrorDomain", 1 or 13) => new PlatformResult<DesktopContentType>.Failed(PlatformFailureCode.PermissionDenied, diagnostic),
                    _ => new PlatformResult<DesktopContentType>.Failed(PlatformFailureCode.Other, diagnostic),
                };
            }
        }
        // A type the system declares nowhere (a dynamic type) is the most generic one.
        if (type == 0 || Bool(type, Sel("isDynamic")) != 0) type = Arg(Class("UTType"), Sel("typeWithIdentifier:"), String(Data));
        return Success(Describe(type));
    }

    private static DesktopContentType Describe(nint type)
    {
        nint supertypes = Send(Send(type, Sel("supertypes")), Sel("allObjects"));
        var parents = new List<(string, int)>();
        for (nint i = 0, count = Send(supertypes, Sel("count")); i < count; i++)
        {
            nint parent = Arg(supertypes, Sel("objectAtIndex:"), i);
            parents.Add((Text(Send(parent, Sel("identifier"))), (int)Send(Send(parent, Sel("supertypes")), Sel("count"))));
        }
        var extensions = new List<string>();
        nint preferred = Send(type, Sel("preferredFilenameExtension"));
        if (preferred != 0) extensions.Add(Text(preferred));
        foreach (string extension in Strings(Arg(Send(type, Sel("tags")), Sel("objectForKey:"), String("public.filename-extension"))))
            if (!extensions.Contains(extension)) extensions.Add(extension);
        nint description = Send(type, Sel("localizedDescription")), mime = Send(type, Sel("preferredMIMEType"));
        return new DesktopContentType(Text(Send(type, Sel("identifier"))))
        {
            Description = description == 0 ? null : Text(description),
            MimeType = mime == 0 ? null : Text(mime),
            Parents = OrderParents(parents),
            Extensions = [.. extensions],
        };
    }

    // The applications for a content type or URL scheme, the default first; null for a malformed type.
    private static (ImmutableArray<DesktopApplicationInfo> List, DesktopApplicationInfo? Default)? Handlers(string contentType)
    {
        var kind = Classify(contentType);
        if (kind is null) return null;
        nint workspace = Workspace(), all, preferred;
        if (kind.Value.Kind == ContentKind.Scheme)
        {
            nint url = Arg(Class("NSURL"), Sel("URLWithString:"), String(kind.Value.Value + ":"));
            if (url == 0) return null;
            all = Arg(workspace, Sel("URLsForApplicationsToOpenURL:"), url);
            preferred = Arg(workspace, Sel("URLForApplicationToOpenURL:"), url);
        }
        else
        {
            nint type = UniformType(kind.Value);
            if (type == 0) return ([], null);
            all = Arg(workspace, Sel("URLsForApplicationsToOpenContentType:"), type);
            preferred = Arg(workspace, Sel("URLForApplicationToOpenContentType:"), type);
        }
        var chosen = preferred == 0 ? null : Info(Arg(Class("NSBundle"), Sel("bundleWithURL:"), preferred));
        var list = new List<DesktopApplicationInfo>();
        if (chosen is not null) list.Add(chosen);
        foreach (var handler in Applications(all)) if (!list.Exists(existing => existing.Id == handler.Id)) list.Add(handler);
        return ([.. list], chosen);
    }

    private static List<DesktopApplicationInfo> Applications(nint urls)
    {
        var list = new List<DesktopApplicationInfo>();
        for (nint i = 0, count = urls == 0 ? 0 : Send(urls, Sel("count")); i < count; i++)
        {
            if (Info(Arg(Class("NSBundle"), Sel("bundleWithURL:"), Arg(urls, Sel("objectAtIndex:"), i))) is { } info && !list.Exists(existing => existing.Id == info.Id))
                list.Add(info);
        }
        return list;
    }

    private static nint UniformType((ContentKind Kind, string Value) kind) => kind.Kind switch
    {
        ContentKind.Identifier => Arg(Class("UTType"), Sel("typeWithIdentifier:"), String(kind.Value)),
        ContentKind.Mime => Arg(Class("UTType"), Sel("typeWithMIMEType:"), String(kind.Value)),
        ContentKind.Extension => Arg(Class("UTType"), Sel("typeWithFilenameExtension:"), String(kind.Value)),
        _ => 0,
    };

    // An application bundle as the contract describes it: its identifier, localized name and declared types.
    private static DesktopApplicationInfo? Info(nint bundle)
    {
        if (bundle == 0) return null;
        string id = Text(Send(bundle, Sel("bundleIdentifier")));
        if (id.Length == 0 || id.Contains('\0')) return null;
        string name = InfoString(bundle, "CFBundleDisplayName") ?? InfoString(bundle, "CFBundleName")
            ?? Path.GetFileNameWithoutExtension(Text(Send(bundle, Sel("bundlePath"))));
        if (name.Length == 0 || name.Contains('\0')) name = id;
        nint info = Send(bundle, Sel("infoDictionary"));
        var types = new List<string>();
        foreach (nint document in Objects(Arg(info, Sel("objectForKey:"), String("CFBundleDocumentTypes"))))
            foreach (string type in Strings(Arg(document, Sel("objectForKey:"), String("LSItemContentTypes"))))
                if (!types.Contains(type)) types.Add(type);
        foreach (nint urlType in Objects(Arg(info, Sel("objectForKey:"), String("CFBundleURLTypes"))))
            foreach (string scheme in Strings(Arg(urlType, Sel("objectForKey:"), String("CFBundleURLSchemes"))))
                if (!types.Contains(SchemePrefix + scheme.ToLowerInvariant())) types.Add(SchemePrefix + scheme.ToLowerInvariant());
        return new DesktopApplicationInfo(id, name) { ContentTypes = [.. types] };
    }

    // A localized Info.plist string, such as CFBundleDisplayName.
    private static string? InfoString(nint bundle, string key)
    {
        nint value = Arg(bundle, Sel("objectForInfoDictionaryKey:"), String(key));
        if (value == 0 || BoolArg(value, Sel("isKindOfClass:"), Class("NSString")) == 0) return null;
        string text = Text(value);
        return text.Length == 0 ? null : text;
    }

    private static IEnumerable<nint> Objects(nint array)
    {
        if (array == 0 || BoolArg(array, Sel("isKindOfClass:"), Class("NSArray")) == 0) yield break;
        for (nint i = 0, count = Send(array, Sel("count")); i < count; i++)
        {
            nint item = Arg(array, Sel("objectAtIndex:"), i);
            if (BoolArg(item, Sel("isKindOfClass:"), Class("NSDictionary")) != 0) yield return item;
        }
    }

    private static List<string> Strings(nint array)
    {
        var list = new List<string>();
        if (array == 0 || BoolArg(array, Sel("isKindOfClass:"), Class("NSArray")) == 0) return list;
        for (nint i = 0, count = Send(array, Sel("count")); i < count; i++)
        {
            nint item = Arg(array, Sel("objectAtIndex:"), i);
            if (BoolArg(item, Sel("isKindOfClass:"), Class("NSString")) == 0) continue;
            string text = Text(item);
            if (text.Length != 0 && !text.Contains('\0')) list.Add(text);
        }
        return list;
    }

    private static nint Workspace() => Send(Class("NSWorkspace"), Sel("sharedWorkspace"));

    private static nint ApplicationUrl(string bundleIdentifier) => Arg(Workspace(), Sel("URLForApplicationWithBundleIdentifier:"), String(bundleIdentifier));

    // Draws the image at the size into an sRGB bitmap and encodes it as PNG.
    private static unsafe byte[]? Png(nint image, int pixels)
    {
        var rect = new Rect(0, 0, pixels, pixels);
        nint source = ProposedImage(image, Sel("CGImageForProposedRect:context:hints:"), &rect, 0, 0);
        if (source == 0) return null;
        nint space = CGColorSpaceCreateWithName(Marshal.ReadIntPtr(NativeLibrary.GetExport(CoreGraphicsLibrary.Value, "kCGColorSpaceSRGB")));
        nint context = 0, output = 0, representation = 0;
        try
        {
            // kCGImageAlphaPremultipliedLast: RGBA, the format Core Graphics draws into.
            context = CGBitmapContextCreate(0, (nuint)pixels, (nuint)pixels, 8, 0, space, 1);
            if (context == 0) return null;
            CGContextSetInterpolationQuality(context, 3); // kCGInterpolationHigh
            CGContextDrawImage(context, rect, source);
            output = CGBitmapContextCreateImage(context);
            if (output == 0) return null;
            representation = Arg(Send(Class("NSBitmapImageRep"), Sel("alloc")), Sel("initWithCGImage:"), output);
            // NSBitmapImageFileTypePNG.
            nint data = representation == 0 ? 0 : Args(representation, Sel("representationUsingType:properties:"), 4, Send(Class("NSDictionary"), Sel("dictionary")));
            if (data == 0) return null;
            nint length = Send(data, Sel("length"));
            if (length <= 0 || length > 4 * 1024 * 1024) return null;
            return new ReadOnlySpan<byte>((void*)Send(data, Sel("bytes")), (int)length).ToArray();
        }
        finally
        {
            Release(representation);
            if (output != 0) CGImageRelease(output);
            if (context != 0) CGContextRelease(context);
            if (space != 0) CGColorSpaceRelease(space);
        }
    }

    private static readonly Lazy<nint> CoreGraphicsLibrary = new(static () => NativeLibrary.Load(CoreGraphics));
    private static readonly Lazy<nint> ContentTypeKey = new(static () =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation"), "NSURLContentTypeKey")));

    private static PlatformResult<T> Success<T>(T value) => new PlatformResult<T>.Success(value);
    private static PlatformResult<T> InvalidData<T>() => new PlatformResult<T>.Failed(PlatformFailureCode.InvalidData);
    private static PlatformResult<PlatformUnit> Failed(PlatformFailureCode code, PlatformDiagnostic diagnostic) => new PlatformResult<PlatformUnit>.Failed(code, diagnostic);

    private static async ValueTask<PlatformResult<T>> Run<T>(Func<PlatformResult<T>> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (!Loaded.Value) return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        return await Task.Run(() =>
        {
            using var pool = new Pool();
            return work();
        }, cancellationToken).ConfigureAwait(false);
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Rect(double X, double Y, double Width, double Height);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static unsafe partial nint ProposedImage(nint target, nint selector, Rect* rect, nint context, nint hints);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte ResourceValue(nint target, nint selector, out nint value, nint key, out nint error);
    [LibraryImport(CoreGraphics)] private static partial nint CGColorSpaceCreateWithName(nint name);
    [LibraryImport(CoreGraphics)] private static partial nint CGBitmapContextCreate(nint data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint space, uint bitmapInfo);
    [LibraryImport(CoreGraphics)] private static partial void CGContextSetInterpolationQuality(nint context, int quality);
    [LibraryImport(CoreGraphics)] private static partial void CGContextDrawImage(nint context, Rect rect, nint image);
    [LibraryImport(CoreGraphics)] private static partial nint CGBitmapContextCreateImage(nint context);
    [LibraryImport(CoreGraphics)] private static partial void CGImageRelease(nint image);
    [LibraryImport(CoreGraphics)] private static partial void CGContextRelease(nint context);
    [LibraryImport(CoreGraphics)] private static partial void CGColorSpaceRelease(nint space);
}
