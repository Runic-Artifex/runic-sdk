using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

#pragma warning disable CA1416 // The portable cases call pure helpers; the native case checks for Windows first.
internal sealed partial class ApplicationsTests
{
    private sealed class FakeClasses(Dictionary<string, Dictionary<string, string>> keys, Dictionary<string, List<(string, string?)>>? explorer = null) : IWindowsClasses
    {
        public bool Exists(string key) => keys.ContainsKey(key.ToLowerInvariant());
        public string? Value(string key, string? name) =>
            keys.TryGetValue(key.ToLowerInvariant(), out var values) && values.TryGetValue(name ?? "", out var value) ? value : null;
        public IReadOnlyList<string> ValueNames(string key) =>
            keys.TryGetValue(key.ToLowerInvariant(), out var values) ? [.. values.Keys.Where(name => name.Length != 0)] : [];
        public IReadOnlyList<string> SubKeyNames(string key) =>
            [.. keys.Keys.Where(name => name.StartsWith(key.ToLowerInvariant() + "\\", StringComparison.Ordinal) && name.IndexOf('\\', key.Length + 1) < 0)
                .Select(name => name[(key.Length + 1)..])];
        public IReadOnlyList<(string Name, string? Value)> ExplorerList(string extension, string list) =>
            explorer is not null && explorer.TryGetValue(extension + "\\" + list, out var values) ? values : [];
    }

    private static FakeClasses Classes() => new(new()
    {
        [".txt"] = new() { [""] = "txtfile", ["Content Type"] = "text/plain", ["PerceivedType"] = "text" },
        [".htm"] = new() { [""] = "htmlfile", ["Content Type"] = "text/html", ["PerceivedType"] = "text" },
        [".html"] = new() { [""] = "htmlfile", ["Content Type"] = "text/html", ["PerceivedType"] = "text" },
        [".log"] = new() { [""] = "txtfile", ["PerceivedType"] = "text" },
        [".odd"] = new() { ["Content Type"] = "not a mime type" },
        [@"mime\database\content type\text/plain"] = new() { ["Extension"] = ".txt" },
        [@"mime\database\content type\text/html"] = new() { ["Extension"] = ".htm" },
        [@"mime\database\content type\image/png"] = new() { ["Extension"] = ".png" },
    });

    // A MIME type is the ID only when the MIME database maps it back to the same extension, so an ID always stands for
    // one extension's associations.
    [Test]
    public void ContentTypesFollowTheRegistry()
    {
        var classes = Classes();
        var text = WindowsContentTypes.FromExtension(".TXT", classes)!;
        Check(text.Id == "text/plain" && text.MimeType == "text/plain" && text.Extensions.SequenceEqual(["txt"]) && text.Parents.IsEmpty, $"text: {text}");
        var htm = WindowsContentTypes.FromExtension(".htm", classes)!;
        Check(htm.Id == "text/html" && htm.Parents.SequenceEqual(["text/plain"]), $"htm: {htm}");
        var html = WindowsContentTypes.FromExtension(".html", classes)!;
        Check(html.Id == ".html" && html.MimeType == "text/html" && html.Extensions.SequenceEqual(["html"]), $"html maps to .htm in the MIME database: {html}");
        var log = WindowsContentTypes.FromExtension(".log", classes)!;
        Check(log.Id == ".log" && log.MimeType is null && log.Parents.SequenceEqual(["text/plain"]), $"perceived text: {log}");
        Check(WindowsContentTypes.FromExtension(".odd", classes) is { Id: ".odd", MimeType: null }, "a malformed Content Type is ignored");
        Check(WindowsContentTypes.FromExtension(".unknown", classes) is null && WindowsContentTypes.FromExtension("", classes) is null
            && WindowsContentTypes.FromExtension(".a b", classes) is null, "unknown extensions");
        var png = WindowsContentTypes.FromMime("IMAGE/PNG", classes);
        Check(png.Id == "image/png" && png.Extensions.SequenceEqual(["png"]), $"sniffed: {png}");
        Check(WindowsContentTypes.FromMime(WindowsContentTypes.Generic, classes) is { Id: "application/octet-stream", Extensions.IsEmpty: true }, "generic");

        Check(Resolve(".TXT") == new Association(AssociationKind.Extension, ".txt"), "extension");
        Check(Resolve("text/plain") == new Association(AssociationKind.Extension, ".txt"), "MIME type");
        Check(Resolve("x-scheme-handler/HTTPS") == new Association(AssociationKind.Protocol, "https"), "scheme");
        Check(Resolve("inode/directory") == new Association(AssociationKind.Class, "Directory"), "directory");
        Check(WindowsContentTypes.TryResolve("application/x-unregistered", classes, out var none) && none is null, "a MIME type without an extension");
        foreach (string malformed in new[] { "plain", "text/", "/plain", "a/b/c", "text/pl ain", ".", ".a/b", ".a.b", ".a\\b", "x-scheme-handler/", "x-scheme-handler/1x", "x-scheme-handler/a b" })
            Check(!WindowsContentTypes.TryResolve(malformed, classes, out _), $"malformed: {malformed}");

        Association? Resolve(string id) => WindowsContentTypes.TryResolve(id, classes, out var association) ? association : throw new InvalidOperationException(id);
    }

    // The classes that may hold an application's registration for an extension, each once, as Open With gathers them.
    [Test]
    public void RegistrationsAreGatheredLikeOpenWith()
    {
        var classes = new FakeClasses(new()
        {
            [".log"] = new() { [""] = "txtfile", ["PerceivedType"] = "text" },
            [@".log\openwithprogids"] = new() { ["txtfile"] = "", ["Editor.Log"] = "" },
            [@".log\openwithlist\editor.exe"] = new(),
            [@"systemfileassociations\.log\openwithlist\viewer.exe"] = new(),
            [@"systemfileassociations\text\openwithlist\notepad.exe"] = new(),
        }, new()
        {
            [@".log\OpenWithProgids"] = [("AppX123", null)],
            [@".log\OpenWithList"] = [("a", "user.exe"), ("MRUList", "a"), ("b", @"bad\path.exe")],
        });
        var found = WindowsContentTypes.Registrations(".log", classes);
        Check(found.Select(entry => entry.Class).SequenceEqual(["txtfile", "Editor.Log", "AppX123", @"Applications\editor.exe", @"Applications\user.exe",
            @"Applications\viewer.exe", @"Applications\notepad.exe"]), string.Join(", ", found));
        Check(found.Single(entry => entry.Class == @"Applications\editor.exe").Query == "editor.exe", "an application is queried by its executable's name");
        Check(WindowsContentTypes.Registrations(".none", classes).Count == 0, "an unknown extension has no registrations");
    }

    [Test]
    public void IconLocationsParse()
    {
        Check(WindowsContentTypes.ParseIconLocation(@"C:\Windows\system32\imageres.dll,-102") == (@"C:\Windows\system32\imageres.dll", -102), "negative resource ID");
        Check(WindowsContentTypes.ParseIconLocation("\"C:\\Program Files\\App\\app.exe\",0") == (@"C:\Program Files\App\app.exe", 0), "quoted");
        Check(WindowsContentTypes.ParseIconLocation(@"C:\icons\a,b.ico") == (@"C:\icons\a,b.ico", 0), "a comma in the name");
        Check(WindowsContentTypes.ParseIconLocation(@"C:\icons\plain.ico") == (@"C:\icons\plain.ico", 0), "no index");
        Check(WindowsContentTypes.ParseIconLocation("%1") is null && WindowsContentTypes.ParseIconLocation("") is null && WindowsContentTypes.ParseIconLocation(null) is null,
            "per-file and empty icons");
    }

    // Launch items group by what opens them; every group must name the application before anything starts.
    [Test]
    public void LaunchItemsGroupByAssociation()
    {
        // The host's own fully qualified paths, as DesktopApplicationNames accepts them.
        string root = OperatingSystem.IsWindows() ? @"C:\a\" : "/a/";
        var groups = WindowsDesktopApplications.Group([root + "one.TXT", "https://example.com/x", root + "folder", new Uri(root + "two.txt").AbsoluteUri, root + "pic.png", "HTTPS://example.com/y"],
            path => path.EndsWith("folder", StringComparison.Ordinal))!;
        Check(groups.Count == 4, $"groups: {groups.Count}");
        Check(groups[0].Association == new Association(AssociationKind.Extension, ".txt") && groups[0].Items.SequenceEqual([root + "one.TXT", root + "two.txt"]),
            $"file: URIs open as paths: {string.Join(", ", groups[0].Items)}");
        Check(groups[1].Association == new Association(AssociationKind.Protocol, "https") && groups[1].Items.SequenceEqual(["https://example.com/x", "HTTPS://example.com/y"]), "URIs by scheme");
        Check(groups[2].Association == new Association(AssociationKind.Class, "Directory"), "directories");
        Check(groups[3].Association == new Association(AssociationKind.Extension, ".png"), "png");
        Check(WindowsDesktopApplications.Group([root + "README"], _ => false) is null, "a file without an extension has no association");
    }

    [Test]
    public void AppsFolderEntriesAreProgramsOnly()
    {
        // The host's own fully qualified paths.
        string folder = OperatingSystem.IsWindows() ? @"C:\Program Files\App\" : "/opt/App/";
        Check(WindowsShellAssociations.Identify(folder + "app.exe", "{6D809377}\\App\\app.exe") == folder + "app.exe", "a desktop program is its executable");
        Check(WindowsShellAssociations.Identify(null, "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App") == "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "a packaged application is its AppUserModelID");
        Check(WindowsShellAssociations.Identify(folder + "readme.txt", "readme") is null && WindowsShellAssociations.Identify("https://example.com", "site") is null,
            "shortcuts to documents and web pages are not applications");
        var entries = new List<WindowsShellAssociations.AppsFolderEntry> { new(folder + "app.exe", "App", "{6D809377}\\App\\app.exe") };
        Check(WindowsShellAssociations.FindEntry(entries, (folder + "APP.exe").ToUpperInvariant()) is { Name: "App" }, "IDs match without case");
        Check(WindowsShellAssociations.FindEntry(entries, folder + "other.exe") is null && WindowsShellAssociations.FindEntry(null, "x") is null,
            "an unknown ID has no entry, not an empty one");
        Check(WindowsDesktopApplications.FromHResult(unchecked((int)0x80070005)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }, "access denied");
        Check(WindowsDesktopApplications.FromHResult(unchecked((int)0x80004005)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.IoError, Diagnostic.Domain: "HRESULT" }, "other errors");
        Check(WindowsDesktopApplications.DefaultsPolicy.Message!.Contains("ms-settings:defaultapps", StringComparison.Ordinal), "the policy names Settings");
    }

    // The provider checks arguments as every provider does, before any native call; this runs on every OS.
    [Test]
    public async Task ArgumentsAreCheckedFirstAsync()
    {
        var applications = new WindowsDesktopApplications(Classes());
        Check(await ThrowsAsync<ArgumentException>(() => applications.GetApplicationsForAsync("").AsTask()), "empty type");
        Check(await ThrowsAsync<ArgumentException>(() => applications.GetDefaultApplicationAsync("a\0b").AsTask()), "NUL");
        Check(await ThrowsAsync<ArgumentException>(() => applications.LaunchAsync("app", ["relative.txt"]).AsTask()), "relative item");
        Check(await ThrowsAsync<ArgumentException>(() => applications.LaunchAsync("app", default).AsTask()), "default items");
        Check(await ThrowsAsync<ArgumentException>(() => applications.SetDefaultApplicationAsync(".txt", " ").AsTask()), "blank application");
        Check(await ThrowsAsync<ArgumentOutOfRangeException>(() => applications.GetContentTypeAsync(@"C:\a.txt", (DesktopContentDetection)7).AsTask()), "detection");
        Check(await ThrowsAsync<ArgumentNullException>(() => applications.GetIconAsync(null!).AsTask()), "request");
    }

    // The runner's real association database, read-only, then a test-owned extension, MIME type and URL protocol in
    // HKEY_CURRENT_USER\Software\Classes whose handler is this test executable, which records its arguments
    // (--record-launch). Nothing else in the registry changes, and the keys are removed afterwards. CI only.
    [Test, Explicit, Category("NativeApplications")]
    public async Task ShellAssociationsAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8)) throw new PlatformNotSupportedException("The shell's associations require Windows.");
        string unique = Guid.NewGuid().ToString("N")[..8];
        string extension = ".runic" + unique, plainExtension = ".runicplain" + unique, mime = "application/x-runic-test-" + unique;
        string progId = "Runic.Test." + unique, scheme = "runic-test-" + unique;
        string root = Path.Combine(Path.GetTempPath(), "runic-apps-" + unique);
        string log = Path.Combine(root, "launches.log");
        // The apphost, also when the tests run through dotnet; the NativeAOT executable is the same file name.
        string probe = Path.Combine(AppContext.BaseDirectory, "Runic.Platform.Windows.Tests.exe");
        Directory.CreateDirectory(root);
        var applications = WindowsPlatformProvider.CreateApplications();
        try
        {
            Check(File.Exists(probe), $"the probe executable exists: {probe}");
            // The runner's own database, read-only.
            var text = Success(await applications.GetContentTypeAsync(@"C:\any\name.txt", DesktopContentDetection.NameOnly));
            Console.WriteLine($"name.txt: {text}");
            Check(text.Id == "text/plain" && text.Extensions.Contains("txt"), "the runner's .txt is text/plain");
            var textApplications = Success(await applications.GetApplicationsForAsync(".txt"));
            Console.WriteLine($".txt handlers: {string.Join(" | ", textApplications.Select(app => $"{app.Name} = {app.Id}"))}");
            Check(!textApplications.IsEmpty, "something opens .txt");
            Check(SameApplications(textApplications, Success(await applications.GetApplicationsForAsync("text/plain"))), "the MIME type and the extension list the same handlers");
            var textDefault = Success(await applications.GetDefaultApplicationAsync(".txt"));
            Console.WriteLine($".txt default: {textDefault}");
            Check(textDefault is not null && string.Equals(textApplications[0].Id, textDefault.Id, StringComparison.OrdinalIgnoreCase), "the default comes first");
            var https = Success(await applications.GetApplicationsForAsync("x-scheme-handler/https"));
            Console.WriteLine($"https handlers: {string.Join(" | ", https.Select(app => $"{app.Name} = {app.Id}"))}");
            Check(Success(await applications.GetContentTypeAsync(@"C:\any\name.unknown" + unique, DesktopContentDetection.NameOnly)).Id == "application/octet-stream", "an unknown name");
            Check(await applications.GetApplicationsForAsync("not a type") is PlatformResult<ImmutableArray<DesktopApplicationInfo>>.Failed { Code: PlatformFailureCode.InvalidData }, "malformed");
            Check(Success(await applications.GetApplicationsForAsync("application/x-runic-unregistered-" + unique)).IsEmpty, "an unregistered MIME type has no handlers");

            var all = Success(await applications.GetApplicationsAsync());
            Console.WriteLine($"{all.Length} Start menu applications, such as: {string.Join(" | ", all.Take(8).Select(app => $"{app.Name} = {app.Id}"))}");
            Check(!all.IsEmpty, "shell:AppsFolder lists applications");
            var packaged = all.FirstOrDefault(app => !Path.IsPathFullyQualified(app.Id));
            if (packaged is not null)
            {
                var icon = Success(await applications.GetIconAsync(DesktopIconRequest.ForApplication(packaged.Id, 32)));
                Console.WriteLine($"Icon of {packaged.Id}: {(icon is null ? "none" : $"{icon.Data.Length} bytes")}");
                if (icon is not null) CheckPng(icon, 32);
            }
            // A desktop default (an executable) or a packaged one (an AppUserModelID in shell:AppsFolder).
            if (textDefault is not null && (Path.IsPathFullyQualified(textDefault.Id) || all.Any(app => app.Id == textDefault.Id)))
                CheckPng(Success(await applications.GetIconAsync(DesktopIconRequest.ForApplication(textDefault.Id, 32, 2))) ?? throw new InvalidOperationException("no icon for the .txt default"), 64);
            CheckPng(Success(await applications.GetIconAsync(DesktopIconRequest.ForContentType("text/plain", 48))) ?? throw new InvalidOperationException("no text/plain icon"), 48);
            Check(Success(await applications.GetIconAsync(DesktopIconRequest.ForIconName("folder", 32))) is null, "Windows has no icon themes");

            // The test-owned registrations.
            using (var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
            {
                using (var key = classes.CreateSubKey(extension)) { key.SetValue("", progId); key.SetValue("Content Type", mime); key.SetValue("PerceivedType", "text"); }
                using (var key = classes.CreateSubKey(plainExtension)) { key.SetValue("", progId); key.SetValue("Content Type", "application/x-runic-unmapped-" + unique); }
                using (var key = classes.CreateSubKey(@"MIME\Database\Content Type\" + mime)) key.SetValue("Extension", extension);
                using (var key = classes.CreateSubKey(progId)) key.SetValue("", "Runic test document " + unique);
                using (var key = classes.CreateSubKey(progId + @"\DefaultIcon")) key.SetValue("", @"%SystemRoot%\System32\shell32.dll,1", RegistryValueKind.ExpandString);
                using (var key = classes.CreateSubKey(progId + @"\shell\open\command")) key.SetValue("", $"\"{probe}\" --record-launch \"{log}\" \"%1\"");
                using (var key = classes.CreateSubKey(scheme)) { key.SetValue("", "URL:Runic test " + unique); key.SetValue("URL Protocol", ""); }
                using (var key = classes.CreateSubKey(scheme + @"\shell\open\command")) key.SetValue("", $"\"{probe}\" --record-launch \"{log}\" \"%1\"");
            }
            AssociationsChanged();

            string first = Path.Combine(root, "first document " + unique + extension), second = Path.Combine(root, "second" + extension);
            File.WriteAllText(first, "one");
            File.WriteAllText(second, "two");
            var type = Success(await applications.GetContentTypeAsync(first));
            Console.WriteLine($"Test type: {type}");
            Check(type.Id == mime && type.MimeType == mime && type.Extensions.SequenceEqual([extension[1..]]) && type.Parents.SequenceEqual(["text/plain"])
                && type.Description == "Runic test document " + unique, "the test extension's type");
            Check(Success(await applications.GetContentTypeAsync(Path.Combine(root, "x" + plainExtension), DesktopContentDetection.NameOnly)).Id == plainExtension,
                "a MIME type the database does not map back leaves the extension as the ID");
            Check(Success(await applications.GetContentTypeAsync(root)).Id == "inode/directory", "a directory");
            Check(await applications.GetContentTypeAsync(Path.Combine(root, "missing" + extension)) is PlatformResult<DesktopContentType>.Failed { Code: PlatformFailureCode.IoError },
                "a missing file");
            string sniffed = Path.Combine(root, "image.unknown" + unique);
            File.WriteAllBytes(sniffed, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
            var sniffedType = Success(await applications.GetContentTypeAsync(sniffed));
            Console.WriteLine($"Sniffed: {sniffedType}");
            Check(sniffedType.Id == "image/png", "an unknown name is sniffed");

            var handlers = Success(await applications.GetApplicationsForAsync(mime));
            Console.WriteLine($"Test handlers: {string.Join(" | ", handlers.Select(app => $"{app.Name} = {app.Id}"))}");
            Check(handlers.Length > 0 && string.Equals(handlers[0].Id, probe, StringComparison.OrdinalIgnoreCase), "the probe is the default handler");
            Check(SameApplications(handlers, Success(await applications.GetApplicationsForAsync(extension))), "MIME type and extension agree");
            Check(Success(await applications.GetDefaultApplicationAsync(extension)) is { } chosen && string.Equals(chosen.Id, probe, StringComparison.OrdinalIgnoreCase), "the default");
            string probeId = handlers[0].Id;

            var refused = await applications.SetDefaultApplicationAsync(extension, probeId);
            Console.WriteLine($"SetDefault: {refused}");
            Check(refused is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied, Diagnostic.Message: { } message } && message.Contains("ms-settings:defaultapps", StringComparison.Ordinal),
                "Windows leaves the default to the user");
            Check(await applications.SetDefaultApplicationAsync(extension, @"C:\Windows\System32\cmd.exe") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData },
                "an application that does not open the type");

            CheckPng(Success(await applications.GetIconAsync(DesktopIconRequest.ForContentType(extension, 32))) ?? throw new InvalidOperationException("no test type icon"), 32);

            // Launches: only a handler of every item's type starts, and each item reaches it as one argument.
            Check(await applications.LaunchAsync(@"C:\Windows\System32\cmd.exe", [first]) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData },
                "a program that is not a handler of the type does not start");
            Console.WriteLine($"The probe is {(all.Any(app => string.Equals(app.Id, probeId, StringComparison.OrdinalIgnoreCase)) ? "" : "not ")}a Start menu application.");
            var bare = await applications.LaunchAsync(@"C:\Runic\missing-" + unique + ".exe", []);
            Console.WriteLine($"Launch without items: {bare}");
            Check(bare is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData }, "without items, only a Start menu application starts");
            Check(await applications.LaunchAsync(probeId, [first, Path.Combine(root, "README")]) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData },
                "an item without a type");
            string stray = Path.Combine(root, "stray.log");
            Environment.SetEnvironmentVariable(StrayLaunchVariable, stray);
            // A type only the probe opens, one document and then two of the test type, which Notepad also opens
            // (PerceivedType text), and a URI.
            string single = Path.Combine(root, "single" + plainExtension), alone = Path.Combine(root, "alone" + extension);
            File.WriteAllText(single, "single");
            File.WriteAllText(alone, "alone");
            int choosers = 0;
            async Task<PlatformResult<PlatformUnit>> LaunchLogged(string label, ImmutableArray<string> items)
            {
                var started = DateTime.UtcNow;
                var result = await applications.LaunchAsync(probeId, items);
                await Task.Delay(1000);
                int shown = Choosers();
                choosers += shown;
                Console.WriteLine($"{label}: {result} in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms; {shown} Open With choosers");
                return result;
            }
            var results = new[]
            {
                await LaunchLogged("one document of a type with one handler", [single]),
                await LaunchLogged("one document of a type with two handlers", [alone]),
                await LaunchLogged("two documents of a type with two handlers", [first, second]),
                await LaunchLogged("a URI", [scheme + ":open/a%20b?c=d"]),
            };
            var lines = await RecordedAsync(log, 5);
            Console.WriteLine("Recorded:\n" + string.Join('\n', lines));
            if (File.Exists(stray)) Console.WriteLine("Other launches:\n" + File.ReadAllText(stray));
            Check(results.All(result => result is PlatformResult<PlatformUnit>.Success), "every launch succeeded");
            Check(choosers == 0, "no launch asked the user to choose an application");
            Check(lines.Contains(single) && lines.Contains(alone), "single documents reached the handler");
            Check(lines.Contains(first) && lines.Contains(second), "each document reached the handler as one argument");
            Check(lines.Any(line => line.StartsWith(scheme + ":", StringComparison.Ordinal)), "the URI reached the protocol handler");
            Console.WriteLine("PASS Windows desktop applications: content types, handlers, defaults, launches, icons and the Start menu list.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(StrayLaunchVariable, null);
            using (var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true))
            {
                if (classes is not null)
                    foreach (string key in new[] { extension, plainExtension, @"MIME\Database\Content Type\" + mime, progId, scheme })
                        classes.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            }
            AssociationsChanged();
            for (int attempt = 0; attempt < 10 && Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { await Task.Delay(500); } // A probe may still hold the log.
            }
        }
    }

    // The probe mode: the registered handler command line starts this executable with --record-launch <log> <items...>.
    internal static int RecordLaunch(string[] args)
    {
        if (args.Length < 2) return 2;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.AppendAllLines(args[1], args[2..]);
                return 0;
            }
            catch (IOException) when (attempt < 50) { Thread.Sleep(100); }
        }
    }

    // A launch with another command line than the registered one, such as an Open With entry's "exe" "%1", while the
    // case runs: recorded, rather than starting a test run.
    internal const string StrayLaunchVariable = "RUNIC_TEST_STRAY_LAUNCH_LOG";

    internal static bool RecordStrayLaunch(string[] args)
    {
        if (Environment.GetEnvironmentVariable(StrayLaunchVariable) is not { Length: > 0 } log || args is not [var first, ..] || first.StartsWith('-')) return false;
        File.AppendAllText(log, "stray: " + string.Join(" | ", args) + Environment.NewLine);
        return true;
    }

    // Open With choosers the shell started, which the case closes.
    private static int Choosers()
    {
        var choosers = System.Diagnostics.Process.GetProcessesByName("OpenWith");
        foreach (var chooser in choosers) { try { chooser.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } chooser.Dispose(); }
        return choosers.Length;
    }

    private static async Task<List<string>> RecordedAsync(string log, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        List<string> lines = [];
        while (DateTime.UtcNow < deadline)
        {
            try { lines = File.Exists(log) ? [.. File.ReadAllLines(log)] : []; } catch (IOException) { }
            if (lines.Count >= count) break;
            await Task.Delay(250);
        }
        return lines;
    }

    private static bool SameApplications(ImmutableArray<DesktopApplicationInfo> first, ImmutableArray<DesktopApplicationInfo> second) =>
        first.Select(app => app.Id.ToLowerInvariant()).SequenceEqual(second.Select(app => app.Id.ToLowerInvariant()));

    private static void CheckPng(DesktopIconData icon, int pixels)
    {
        Check(icon.MediaType == "image/png", "PNG");
        var (width, height, image) = DecodePng([.. icon.Data]);
        Console.WriteLine($"Icon {width}x{height}, {icon.Data.Length} bytes, nominal {icon.Size}");
        Check(width == pixels && height == pixels, $"the icon is {pixels} pixels: {width}x{height}");
        int visible = 0;
        for (int i = 3; i < image.Length; i += 4) if (image[i] != 0) visible++;
        Console.WriteLine($"  {visible} of {width * height} pixels visible");
        Check(visible > width * height / 10, "the icon is not blank");
    }

    // Inflates and unfilters the image data of an 8-bit RGBA PNG, as WIC writes it.
    internal static (int Width, int Height, byte[] Pixels) DecodePng(byte[] png)
    {
        Check(png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        int offset = 8, width = 0, height = 0;
        using var data = new MemoryStream();
        while (offset + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = png.AsSpan(offset + 4, 4);
            var body = png.AsSpan(offset + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(body);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                Check(body[8] == 8 && body[9] == 6 && body[12] == 0, $"8-bit RGBA without interlacing: depth {body[8]}, colour type {body[9]}");
            }
            else if (type.SequenceEqual("IDAT"u8)) data.Write(body);
            offset += 12 + length;
        }
        data.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(data, CompressionMode.Decompress)) zlib.CopyTo(inflated);
        byte[] raw = inflated.ToArray();
        int stride = width * 4;
        var pixels = new byte[height * stride];
        for (int y = 0; y < height; y++)
        {
            byte filter = raw[y * (stride + 1)];
            var line = raw.AsSpan(y * (stride + 1) + 1, stride);
            for (int x = 0; x < stride; x++)
            {
                int left = x >= 4 ? pixels[y * stride + x - 4] : 0, up = y > 0 ? pixels[(y - 1) * stride + x] : 0,
                    corner = x >= 4 && y > 0 ? pixels[(y - 1) * stride + x - 4] : 0;
                int predicted = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, corner),
                    _ => throw new InvalidOperationException($"PNG filter {filter}"),
                };
                pixels[y * stride + x] = (byte)(line[x] + predicted);
            }
        }
        return (width, height, pixels);

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }

    private static T Success<T>(PlatformResult<T> result) =>
        result is PlatformResult<T>.Success(var value) ? value : throw new InvalidOperationException($"Expected success: {result}");

    private static async Task<bool> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (T error) when (error.GetType() == typeof(T) || typeof(T) == typeof(ArgumentException) && error is ArgumentException and not ArgumentOutOfRangeException and not ArgumentNullException) { return true; }
    }

    internal static void AssociationsChanged() => SHChangeNotify(0x08000000, 0, 0, 0); // SHCNE_ASSOCCHANGED, SHCNF_IDLIST

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Applications check failed: " + message);
    }
}
#pragma warning restore CA1416
