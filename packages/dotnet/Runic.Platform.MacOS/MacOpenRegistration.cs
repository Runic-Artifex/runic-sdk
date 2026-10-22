using System.Runtime.InteropServices;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// Registration of the running bundle with Launch Services (LSRegisterURL). Launch Services takes URL schemes and
// document types only from the bundle's Info.plist, so a registration checks that the bundle declares each item and
// registers the bundle; it adds nothing. A bundle Launch Services knows stays registered until it is deleted, so
// there is nothing to unregister.
internal sealed partial class MacOpenRegistration : IDesktopOpenRegistration
{
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private static readonly Lazy<bool> Loaded = new(static () => MacAppKit.IsAvailable
        && NativeLibrary.TryLoad(CoreServices, out _)
        && NativeLibrary.TryLoad("/System/Library/Frameworks/UniformTypeIdentifiers.framework/UniformTypeIdentifiers", out _)
        && Class("UTType") != 0);

    public ValueTask<PlatformResult<bool>> IsRegisteredAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();
        if (Bundle() is not { } bundle) return Result<bool>(new PlatformResult<bool>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        using var pool = new Pool();
        if (MacOpenRegistrationModel.Undeclared(registration, Declarations(bundle.Handle)).Count > 0) return Result<bool>(new PlatformResult<bool>.Success(false));
        nint workspace = Send(Class("NSWorkspace"), Sel("sharedWorkspace"));
        foreach (string scheme in registration.UrlSchemes)
        {
            nint url = Arg(Class("NSURL"), Sel("URLWithString:"), String(scheme.ToLowerInvariant() + ":"));
            if (url == 0 || !Lists(Arg(workspace, Sel("URLsForApplicationsToOpenURL:"), url), bundle.Path)) return Result<bool>(new PlatformResult<bool>.Success(false));
        }
        foreach (var fileType in registration.FileTypes)
        {
            nint type = Arg(Class("UTType"), Sel("typeWithFilenameExtension:"), String(fileType.Extension[1..]));
            if (type == 0 || !Lists(Arg(workspace, Sel("URLsForApplicationsToOpenContentType:"), type), bundle.Path))
                return Result<bool>(new PlatformResult<bool>.Success(false));
        }
        return Result<bool>(new PlatformResult<bool>.Success(true));
    }

    public ValueTask<PlatformResult<PlatformUnit>> RegisterAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (registration.UrlSchemes.IsEmpty && registration.FileTypes.IsEmpty)
            throw new ArgumentException("Supply at least one URL scheme or file type.", nameof(registration));
        cancellationToken.ThrowIfCancellationRequested();
        if (Bundle() is not { } bundle) return Result<PlatformUnit>(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        using var pool = new Pool();
        var undeclared = MacOpenRegistrationModel.Undeclared(registration, Declarations(bundle.Handle));
        if (undeclared.Count > 0)
            return Result<PlatformUnit>(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData, MacOpenRegistrationModel.UndeclaredDiagnostic(undeclared)));
        int status = RegisterUrl(Send(bundle.Handle, Sel("bundleURL")), 1);
        return Result<PlatformUnit>(status == 0
            ? new PlatformResult<PlatformUnit>.Success(new PlatformUnit())
            : new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IOError, new PlatformDiagnostic("OSStatus", status, "LSRegisterURL failed.")));
    }

    public ValueTask<PlatformResult<bool>> UnregisterAsync(DesktopOpenRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();
        return Result<bool>(new PlatformResult<bool>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
    }

    // The main bundle, when the process runs from an application bundle.
    private static (nint Handle, string Path)? Bundle()
    {
        if (!OperatingSystem.IsMacOS() || !Loaded.Value) return null;
        using var pool = new Pool();
        nint bundle = Send(Class("NSBundle"), Sel("mainBundle"));
        string path = Text(Send(bundle, Sel("bundlePath")));
        return MacLoginStartupModel.IsApplicationBundle(Text(Send(bundle, Sel("bundleIdentifier"))), path) ? (bundle, path) : null;
    }

    // What the bundle's Info.plist declares: URL schemes, document types and the exported or imported type extensions.
    private static MacOpenRegistrationModel.Declared Declarations(nint bundle)
    {
        nint info = Send(bundle, Sel("infoDictionary"));
        var schemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (nint urlType in Dictionaries(Arg(info, Sel("objectForKey:"), String("CFBundleURLTypes"))))
            schemes.UnionWith(Strings(Arg(urlType, Sel("objectForKey:"), String("CFBundleURLSchemes"))));
        var contentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (nint document in Dictionaries(Arg(info, Sel("objectForKey:"), String("CFBundleDocumentTypes"))))
        {
            contentTypes.UnionWith(Strings(Arg(document, Sel("objectForKey:"), String("LSItemContentTypes"))));
            extensions.UnionWith(Strings(Arg(document, Sel("objectForKey:"), String("CFBundleTypeExtensions"))));
        }
        // A type the bundle declares itself is unknown to UTType until Launch Services registers the bundle.
        foreach (string key in (ReadOnlySpan<string>)["UTExportedTypeDeclarations", "UTImportedTypeDeclarations"])
            foreach (nint declaration in Dictionaries(Arg(info, Sel("objectForKey:"), String(key))))
            {
                var identifiers = Strings(Arg(declaration, Sel("objectForKey:"), String("UTTypeIdentifier")));
                if (identifiers.Count != 1 || !contentTypes.Contains(identifiers[0])) continue;
                nint tags = Arg(declaration, Sel("objectForKey:"), String("UTTypeTagSpecification"));
                if (tags != 0 && BoolArg(tags, Sel("isKindOfClass:"), Class("NSDictionary")) != 0)
                    extensions.UnionWith(Strings(Arg(tags, Sel("objectForKey:"), String("public.filename-extension"))));
            }
        return new(schemes, contentTypes, extensions, extension => ConformsToAny(extension, contentTypes));
    }

    // Whether the system's type for the extension is, or conforms to, one of the declared content types.
    private static bool ConformsToAny(string extension, HashSet<string> contentTypes)
    {
        nint type = Arg(Class("UTType"), Sel("typeWithFilenameExtension:"), String(extension));
        if (type == 0) return false;
        foreach (string identifier in contentTypes)
        {
            nint declared = Arg(Class("UTType"), Sel("typeWithIdentifier:"), String(identifier));
            if (declared != 0 && BoolArg(type, Sel("conformsToType:"), declared) != 0) return true;
        }
        return false;
    }

    // Whether Launch Services lists the bundle among the handlers.
    private static bool Lists(nint urls, string bundlePath)
    {
        string expected = Path.TrimEndingDirectorySeparator(bundlePath);
        for (nint i = 0, count = urls == 0 ? 0 : Send(urls, Sel("count")); i < count; i++)
        {
            nint url = Arg(urls, Sel("objectAtIndex:"), i);
            string path = Path.TrimEndingDirectorySeparator(Text(Send(Send(url, Sel("URLByResolvingSymlinksInPath")), Sel("path"))));
            if (path == expected || path == Path.TrimEndingDirectorySeparator(Text(Send(Send(Url(expected), Sel("URLByResolvingSymlinksInPath")), Sel("path")))))
                return true;
        }
        return false;
    }

    private static IEnumerable<nint> Dictionaries(nint array)
    {
        if (array == 0 || BoolArg(array, Sel("isKindOfClass:"), Class("NSArray")) == 0) yield break;
        for (nint i = 0, count = Send(array, Sel("count")); i < count; i++)
        {
            nint item = Arg(array, Sel("objectAtIndex:"), i);
            if (BoolArg(item, Sel("isKindOfClass:"), Class("NSDictionary")) != 0) yield return item;
        }
    }

    // A string, or an array of strings, as Info.plist keys hold them.
    private static List<string> Strings(nint value)
    {
        var list = new List<string>();
        if (value == 0) return list;
        if (BoolArg(value, Sel("isKindOfClass:"), Class("NSString")) != 0) { list.Add(Text(value)); return list; }
        if (BoolArg(value, Sel("isKindOfClass:"), Class("NSArray")) == 0) return list;
        for (nint i = 0, count = Send(value, Sel("count")); i < count; i++)
        {
            nint item = Arg(value, Sel("objectAtIndex:"), i);
            if (BoolArg(item, Sel("isKindOfClass:"), Class("NSString")) != 0) list.Add(Text(item));
        }
        return list;
    }

    private static ValueTask<PlatformResult<T>> Result<T>(PlatformResult<T> result) => ValueTask.FromResult(result);

    [LibraryImport(CoreServices, EntryPoint = "LSRegisterURL")] private static partial int RegisterUrl(nint bundleUrl, byte update);
}

// The checks of a registration against the bundle's declarations, apart from the native reads.
internal static class MacOpenRegistrationModel
{
    internal sealed record Declared(IReadOnlySet<string> Schemes, IReadOnlySet<string> ContentTypes, IReadOnlySet<string> Extensions,
        Func<string, bool> SystemTypeConforms);

    // The schemes ("org.example.notes:") and extensions (".notes") the bundle does not declare.
    internal static List<string> Undeclared(DesktopOpenRegistration registration, Declared declared)
    {
        var missing = new List<string>();
        foreach (string scheme in registration.UrlSchemes)
            if (!declared.Schemes.Contains(scheme)) missing.Add(scheme + ":");
        foreach (var fileType in registration.FileTypes)
        {
            string extension = fileType.Extension[1..];
            if (!declared.Extensions.Contains(extension) && !declared.SystemTypeConforms(extension)) missing.Add(fileType.Extension);
        }
        return missing;
    }

    internal static PlatformDiagnostic UndeclaredDiagnostic(List<string> missing) => new("Runic.OpenRegistration", missing.Count,
        $"The bundle's Info.plist does not declare {string.Join(", ", missing)}: add CFBundleURLTypes or CFBundleDocumentTypes entries.");
}
