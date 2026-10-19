using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TUnit.Core;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS.Tests;

internal sealed partial class ApplicationsTests
{
    [Test, DisplayName("applications: content type forms are classified")]
    public void ContentTypeForms()
    {
        Check(MacDesktopApplications.Classify("public.plain-text") == (MacDesktopApplications.ContentKind.Identifier, "public.plain-text"), "UTI");
        Check(MacDesktopApplications.Classify("text/plain") == (MacDesktopApplications.ContentKind.Mime, "text/plain"), "MIME type");
        Check(MacDesktopApplications.Classify(".txt") == (MacDesktopApplications.ContentKind.Extension, "txt"), "extension");
        Check(MacDesktopApplications.Classify("x-scheme-handler/HTTPS") == (MacDesktopApplications.ContentKind.Scheme, "https"), "scheme");
        Check(MacDesktopApplications.Classify("com.example.ünïcode-type") is { Kind: MacDesktopApplications.ContentKind.Identifier }, "Unicode identifiers");
        foreach (string malformed in new[] { "not a type", "text/", "/plain", "a/b/c", "text/pl ain", ".", "./x", ".a b", "x-scheme-handler/", "x-scheme-handler/1x", "public:text" })
            Check(MacDesktopApplications.Classify(malformed) is null, $"malformed: {malformed}");
    }

    [Test, DisplayName("applications: supertypes are ordered nearest first")]
    public void ParentsNearestFirst()
    {
        // public.plain-text's supertypes with the number of supertypes each has in turn.
        var parents = MacDesktopApplications.OrderParents([("public.item", 0), ("public.data", 1), ("public.content", 0), ("public.text", 3)]);
        Check(parents.SequenceEqual(["public.text", "public.data", "public.content", "public.item"]), string.Join(", ", parents));
    }

    [Test, DisplayName("applications: the Applications folders are scanned for bundles, not inside them")]
    public void BundleScan()
    {
        string root = Path.Combine(Path.GetTempPath(), "runic-bundles-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            foreach (string folder in new[] { "One.app/Contents/MacOS", "Utilities/Two.app/Contents/Plugins/Inner.app", "Utilities/Deeper/Deepest/Too Deep.app", "Notes" })
                Directory.CreateDirectory(Path.Combine(root, folder));
            var bundles = MacDesktopApplications.BundlePaths([root, Path.Combine(root, "missing")]).Select(path => Path.GetRelativePath(root, path)).ToList();
            Check(bundles.SequenceEqual(["One.app", Path.Combine("Utilities", "Two.app")]), string.Join(", ", bundles));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test, DisplayName("applications: Launch Services errors map to typed outcomes")]
    public void Errors()
    {
        Check(MacDesktopApplications.MapDefaultError(new("NSCocoaErrorDomain", 3072)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.UserDismissed }, "declined");
        Check(MacDesktopApplications.MapDefaultError(new("NSOSStatusErrorDomain", -128)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.UserDismissed }, "userCanceledErr");
        Check(MacDesktopApplications.MapDefaultError(new("NSOSStatusErrorDomain", -54)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }, "permErr");
        Check(MacDesktopApplications.MapDefaultError(new("NSOSStatusErrorDomain", -10814)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.IoError, Diagnostic.Code: -10814 }, "other");
        Check(MacDesktopApplications.MapLaunchError(new("NSCocoaErrorDomain", 257)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }, "no permission");
        Check(MacDesktopApplications.MapLaunchError(new("NSCocoaErrorDomain", 260)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.IoError }, "no such file");
    }

    [Test, DisplayName("applications: arguments are checked before any native call")]
    public async Task ArgumentsAsync()
    {
        var applications = new MacDesktopApplications();
        Check(await ThrowsAsync<ArgumentException>(() => applications.GetApplicationsForAsync("").AsTask()), "empty type");
        Check(await ThrowsAsync<ArgumentException>(() => applications.LaunchAsync("com.example.app", ["relative.txt"]).AsTask()), "relative item");
        Check(await ThrowsAsync<ArgumentException>(() => applications.SetDefaultApplicationAsync("public.text", " ").AsTask()), "blank application");
        Check(await ThrowsAsync<ArgumentOutOfRangeException>(() => applications.GetContentTypeAsync("/a.txt", (DesktopContentDetection)7).AsTask()), "detection");
        Check(await ThrowsAsync<ArgumentNullException>(() => applications.GetIconAsync(null!).AsTask()), "request");
    }

    // The runner's Launch Services database, read-only, then two test-owned application bundles that declare a type,
    // an extension and a URL scheme unique to the run. They are registered with Launch Services, launched (their
    // executable is a script that records each start), and unregistered and deleted afterwards. The only default
    // changed is the test type's. CI only.
    [Test, Explicit, Category("NativeApplications"), DisplayName("applications: Launch Services, UTType and NSWorkspace")]
    public async Task LaunchServicesAsync()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Launch Services requires macOS.");
        string unique = Guid.NewGuid().ToString("N")[..8];
        string root = Path.Combine(Path.GetTempPath(), "runic-apps-" + unique);
        string uti = "eu.runic.tests.document-" + unique, extension = "runic" + unique, mime = "application/x-runic-test-" + unique, scheme = "runic-test-" + unique;
        string first = "eu.runic.tests.apps.first-" + unique, second = "eu.runic.tests.apps.second-" + unique;
        string marker = Path.Combine(root, "starts.log");
        Directory.CreateDirectory(root);
        var applications = MacOSPlatformProvider.CreateApplications();
        var bundles = new List<string>();
        try
        {
            var text = Success(await applications.GetContentTypeAsync("/any/name.txt", DesktopContentDetection.NameOnly));
            Console.WriteLine($"name.txt: {text}");
            Check(text.Id == "public.plain-text" && text.MimeType == "text/plain" && text.Extensions.Contains("txt") && text.Parents.FirstOrDefault() == "public.text"
                && text.Parents.Contains("public.data") && !string.IsNullOrEmpty(text.Description), "public.plain-text");
            Check(Success(await applications.GetContentTypeAsync(root)).Id == "public.folder", "a folder");
            Check(await applications.GetContentTypeAsync(Path.Combine(root, "missing.txt")) is PlatformResult<DesktopContentType>.Failed { Code: PlatformFailureCode.IoError, Diagnostic.Code: 2 },
                "a missing file");
            Check(Success(await applications.GetContentTypeAsync("/any/name." + unique, DesktopContentDetection.NameOnly)).Id == "public.data", "an unknown extension");

            var editors = Success(await applications.GetApplicationsForAsync("public.plain-text"));
            Console.WriteLine($"public.plain-text: {string.Join(" | ", editors.Select(app => $"{app.Name} = {app.Id}"))}");
            Check(editors.Any(app => app.Id == "com.apple.TextEdit"), "TextEdit opens plain text");
            Check(SameIds(editors, Success(await applications.GetApplicationsForAsync("text/plain"))) && SameIds(editors, Success(await applications.GetApplicationsForAsync(".txt"))),
                "the UTI, MIME type and extension list the same applications");
            Check(Success(await applications.GetDefaultApplicationAsync("public.plain-text")) is { } textDefault && textDefault.Id == editors[0].Id, "the default comes first");
            var browsers = Success(await applications.GetApplicationsForAsync("x-scheme-handler/https"));
            Console.WriteLine($"https: {string.Join(" | ", browsers.Select(app => $"{app.Name} = {app.Id}"))}");
            Check(browsers.Any(app => app.Id == "com.apple.Safari"), "Safari opens https");
            Check(await applications.GetApplicationsForAsync("not a type") is PlatformResult<ImmutableArray<DesktopApplicationInfo>>.Failed { Code: PlatformFailureCode.InvalidData }, "malformed");
            Check(Success(await applications.GetApplicationsForAsync("eu.runic.tests.undeclared-" + unique)).IsEmpty, "an undeclared type has no applications");

            var all = Success(await applications.GetApplicationsAsync());
            Console.WriteLine($"{all.Length} applications, such as: {string.Join(" | ", all.Take(8).Select(app => $"{app.Name} = {app.Id}"))}");
            Check(all.FirstOrDefault(app => app.Id == "com.apple.TextEdit") is { } textEdit && textEdit.ContentTypes.Contains("public.plain-text"), "TextEdit is listed with its types");

            CheckPng(Success(await applications.GetIconAsync(DesktopIconRequest.ForApplication("com.apple.TextEdit", 32, 2))), 64);
            CheckPng(Success(await applications.GetIconAsync(DesktopIconRequest.ForContentType("public.plain-text", 16))), 16);
            Check(Success(await applications.GetIconAsync(DesktopIconRequest.ForIconName("folder", 32))) is null, "macOS has no icon themes");
            Check(Success(await applications.GetIconAsync(DesktopIconRequest.ForApplication("eu.runic.tests.missing-" + unique, 32))) is null, "an unknown application has no icon");

            // The test-owned bundles.
            bundles.Add(CreateBundle(root, "Runic First " + unique, first, uti, extension, mime, scheme, marker, exports: true));
            bundles.Add(CreateBundle(root, "Runic Second " + unique, second, uti, extension, mime, scheme, marker, exports: false));
            foreach (string bundle in bundles) Check(Register(bundle) == 0, $"Launch Services registered {bundle}");

            var handlers = ImmutableArray<DesktopApplicationInfo>.Empty;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                handlers = Success(await applications.GetApplicationsForAsync(uti));
                if (handlers.Any(app => app.Id == first) && handlers.Any(app => app.Id == second)) break;
                await Task.Delay(500);
            }
            Console.WriteLine($"{uti}: {string.Join(" | ", handlers.Select(app => $"{app.Name} = {app.Id}"))}");
            Check(handlers.Any(app => app.Id == first) && handlers.Any(app => app.Id == second), "both test bundles open the test type");
            var type = Success(await applications.GetContentTypeAsync("/any/name." + extension, DesktopContentDetection.NameOnly));
            Console.WriteLine($"Test type: {type}");
            Check(type.Id == uti && type.MimeType == mime && type.Extensions.SequenceEqual([extension]) && type.Parents.Contains("public.data"), "the exported type");
            Check(Success(await applications.GetApplicationsForAsync("x-scheme-handler/" + scheme)).Any(app => app.Id == first), "the test scheme");

            // Launches: only an installed application by bundle identifier, with items it can take.
            string document = Path.Combine(root, "document." + extension);
            File.WriteAllText(document, "content");
            Check(await applications.LaunchAsync("eu.runic.tests.missing-" + unique, [document]) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData },
                "an unknown bundle identifier");
            Check(await applications.LaunchAsync(first, ["https://example.com/"]) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData },
                "a URI whose scheme the application does not open");
            var launched = await applications.LaunchAsync(first, [document]);
            Console.WriteLine($"Launch with a document: {launched}");
            Check(launched is PlatformResult<PlatformUnit>.Success, "the first bundle started with the document");
            Check(await StartsAsync(marker, 1), "the bundle's executable ran");
            await Task.Delay(4000); // Let the first start exit, so the next launch starts it again.
            var bare = await applications.LaunchAsync(first, []);
            Console.WriteLine($"Launch without items: {bare}");
            Check(bare is PlatformResult<PlatformUnit>.Success && await StartsAsync(marker, 2), "the first bundle started without items");

            Check(await applications.SetDefaultApplicationAsync(uti, "com.apple.TextEdit") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData },
                "an application that does not open the type");
            // macOS may ask the user before changing a default; the runner has no user to answer, so a prompt is reported
            // rather than failed.
            var change = applications.SetDefaultApplicationAsync(uti, second).AsTask();
            if (await Task.WhenAny(change, Task.Delay(TimeSpan.FromSeconds(60))) == change)
            {
                Console.WriteLine($"SetDefault: {change.Result}");
                Check(change.Result is PlatformResult<PlatformUnit>.Success or PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.UserDismissed }, "the default change completed");
                if (change.Result is PlatformResult<PlatformUnit>.Success)
                    Check(Success(await applications.GetDefaultApplicationAsync(uti))?.Id == second, "the second bundle is the test type's default");
            }
            else Console.WriteLine("SetDefault is waiting for the user's confirmation; skipped its check.");
            Console.WriteLine("PASS macOS desktop applications: content types, handlers, defaults, launches, icons and the application list.");
        }
        finally
        {
            foreach (string bundle in bundles) Unregister(bundle);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string CreateBundle(string root, string name, string id, string uti, string extension, string mime, string scheme, string marker, bool exports)
    {
        string bundle = Path.Combine(root, name + ".app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        string exported = exports ? $"""
            <key>UTExportedTypeDeclarations</key><array><dict>
              <key>UTTypeIdentifier</key><string>{uti}</string>
              <key>UTTypeDescription</key><string>Runic test document</string>
              <key>UTTypeConformsTo</key><array><string>public.data</string><string>public.content</string></array>
              <key>UTTypeTagSpecification</key><dict>
                <key>public.filename-extension</key><array><string>{extension}</string></array>
                <key>public.mime-type</key><array><string>{mime}</string></array>
              </dict>
            </dict></array>
            """ : "";
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
            <key>CFBundleIdentifier</key><string>{id}</string>
            <key>CFBundleName</key><string>{name}</string>
            <key>CFBundleExecutable</key><string>probe</string>
            <key>CFBundlePackageType</key><string>APPL</string>
            <key>CFBundleVersion</key><string>1</string>
            <key>CFBundleShortVersionString</key><string>1.0</string>
            <key>LSUIElement</key><true/>
            {exported}
            <key>CFBundleDocumentTypes</key><array><dict>
              <key>CFBundleTypeName</key><string>Runic test document</string>
              <key>CFBundleTypeRole</key><string>Editor</string>
              <key>LSHandlerRank</key><string>{(exports ? "Owner" : "Alternate")}</string>
              <key>LSItemContentTypes</key><array><string>{uti}</string></array>
            </dict></array>
            <key>CFBundleURLTypes</key><array><dict>
              <key>CFBundleURLName</key><string>{id}</string>
              <key>CFBundleURLSchemes</key><array><string>{scheme}</string></array>
            </dict></array>
            </dict></plist>
            """);
        string executable = Path.Combine(bundle, "Contents", "MacOS", "probe");
        File.WriteAllText(executable, $"#!/bin/sh\necho \"started {id}\" >> '{marker}'\nsleep 3\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        return bundle;
    }

    private static int Register(string bundle)
    {
        using var pool = new Pool();
        return LSRegisterURL(Url(bundle), 1);
    }

    private static void Unregister(string bundle)
    {
        try
        {
            using var process = Process.Start("/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister", ["-u", bundle]);
            process?.WaitForExit(30_000);
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { Console.WriteLine($"Could not unregister {bundle}: {error.Message}"); }
    }

    private static async Task<bool> StartsAsync(string marker, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(marker) && File.ReadAllLines(marker).Length >= count) return true;
            await Task.Delay(250);
        }
        return false;
    }

    private static bool SameIds(ImmutableArray<DesktopApplicationInfo> first, ImmutableArray<DesktopApplicationInfo> second) =>
        first.Select(app => app.Id).Order(StringComparer.Ordinal).SequenceEqual(second.Select(app => app.Id).Order(StringComparer.Ordinal));

    private static void CheckPng(DesktopIconData? icon, int pixels)
    {
        Check(icon is { MediaType: "image/png" }, "a PNG icon");
        var data = icon!.Data.AsSpan();
        Check(data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) && data.Slice(12, 4).SequenceEqual("IHDR"u8), "PNG signature and header");
        int width = BinaryPrimitives.ReadInt32BigEndian(data[16..]), height = BinaryPrimitives.ReadInt32BigEndian(data[20..]);
        Console.WriteLine($"Icon {width}x{height}, {data.Length} bytes, nominal {icon.Size}");
        Check(width == pixels && height == pixels, $"the icon is {pixels} pixels: {width}x{height}");
    }

    private static T Success<T>(PlatformResult<T> result) =>
        result is PlatformResult<T>.Success(var value) ? value : throw new InvalidOperationException($"Expected success: {result}");

    private static async Task<bool> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (Exception error) { return error.GetType() == typeof(T); }
    }

    [LibraryImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    private static partial int LSRegisterURL(nint url, byte update);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Applications check failed: " + message);
    }
}
