using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Channels;
using Runic.Platform;
using Runic.Platform.Runtime;
using TUnit.Core;

// Files and URLs the desktop asks the application to open: the argument parser, delivery through the single
// instance, and, on CI runners only, real launches through the desktop's own registration.
internal sealed class OpenRequestsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DesktopOpenOptions Notes = new(["org.example.notes", "notes"]) { IncludeFiles = true };

    [Test, DisplayName("OPEN: registered URLs and files are taken from the arguments, everything else is left alone")]
    public void ParsesArguments()
    {
        string home = Path.Combine(Path.GetTempPath(), "runic-open-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(home);
        try { ParsesArguments(home); }
        finally { System.IO.Directory.Delete(home, recursive: true); }
    }

    private static void ParsesArguments(string home)
    {
        File.WriteAllText(Path.Combine(home, "report.txt"), "");
        var launch = new ApplicationLaunch(
            ["--verbose", "NOTES://open?id=1", "--profile", "work", "org.example.notes:draft", "https://example.com/", "report.txt", "missing.txt", "-", "--",
                "file:///tmp/a%20b.txt", "", "javascript:alert(1)"],
            home) { PlatformContext = new DesktopActivationContext("token") };
        var request = DesktopOpenRequests.FromLaunch(launch, Notes) ?? throw new InvalidOperationException("No request.");
        string[] items = [.. request.Items.Select(static item => item.ToString())];
        Check(items.Length == 4, $"four items: {string.Join(" | ", items)}");
        Check(request.Items[0].Scheme == "notes" && request.Items[1].Scheme == "org.example.notes", "registered schemes, in order, without regard to case");
        Check(request.Items[2].IsFile && request.Items[2].LocalPath == Path.GetFullPath("report.txt", home), "a relative path resolves against the working directory");
        Check(request.Items[3].IsFile && request.Items[3].LocalPath.EndsWith("a b.txt", StringComparison.Ordinal), "a file URI");
        Check(request.PlatformContext?.ActivationToken == "token", "the activation context travels with the request");

        Check(DesktopOpenRequests.FromLaunch(launch, new DesktopOpenOptions([])) is null, "nothing registered, nothing taken");
        Check(DesktopOpenRequests.FromLaunch(launch, new DesktopOpenOptions(["notes"])) is { Items.Length: 1 }, "without files, paths and file URIs are left alone");
        Check(DesktopOpenRequests.FromLaunch(new ApplicationLaunch(["notes:x"], home), new DesktopOpenOptions([]) { IncludeFiles = true }) is null,
            "an argument that looks like a URL is never read as a path");
        Check(DesktopOpenRequests.FromLaunch(new ApplicationLaunch([Path.Combine(home, "report.txt"), home], "/"), Notes) is { Items: [{ IsFile: true }, { IsFile: true }] },
            "an absolute path, such as a Windows drive path, and a directory are files");
        Check(DesktopOpenRequests.FromLaunch(new ApplicationLaunch([.. Enumerable.Repeat("notes:x", 1100)], home), Notes) is { Items.Length: 1024 }, "at most 1024 items");
        Check(DesktopOpenRequests.FromLaunch(new ApplicationLaunch(["notes:" + new string('x', 40_000)], home), Notes) is null, "an oversized argument is ignored");
        Check(DesktopOpenRequests.FromLaunch(new ApplicationLaunch(default, home), Notes) is null, "no arguments");

        foreach (var invalid in new[] { new DesktopOpenOptions(default), new DesktopOpenOptions(["1abc"]), new DesktopOpenOptions(["a b"]), new DesktopOpenOptions(["file"]), new DesktopOpenOptions([""]) })
        {
            try { DesktopOpenRequests.FromLaunch(launch, invalid); throw new InvalidOperationException($"{invalid} was accepted."); }
            catch (ArgumentException) { }
        }
    }

    [Test, DisplayName("OPEN: https links of the web link hosts are taken; every other https URL is left alone")]
    public void ParsesWebLinks()
    {
        var options = new DesktopOpenOptions(["notes"]) { WebLinkHosts = ["notes.example.org", "Links.Example.COM", "bücher.example"] };
        var launch = new ApplicationLaunch(
            ["https://notes.example.org/n/1?x=2", "https://LINKS.example.com/a", "https://other.example.org/", "http://notes.example.org/",
                "https://notes.example.org:8443/", "https://user@notes.example.org/", "https://notes.example.org.evil.example/",
                "https://sub.notes.example.org/", "https://xn--bcher-kva.example/b", "notes:x"], "/");
        var request = DesktopOpenRequests.FromLaunch(launch, options) ?? throw new InvalidOperationException("No request.");
        string[] items = [.. request.Items.Select(static item => item.ToString())];
        Check(items.SequenceEqual(["https://notes.example.org/n/1?x=2", "https://links.example.com/a", "https://xn--bcher-kva.example/b", "notes:x"]),
            $"only exact hosts, https, the default port and no user information: {string.Join(" | ", items)}");
        Check(DesktopOpenRequests.FromLaunch(new ApplicationLaunch(["https://notes.example.org:443/"], "/"), options) is { Items.Length: 1 },
            "an explicit default port is the default port");
        Check(DesktopOpenRequests.FromLaunch(new ApplicationLaunch(["https://notes.example.org/"], "/"), new DesktopOpenOptions(["notes"])) is null,
            "without web link hosts, https URLs stay with the application");
        foreach (string host in new[] { "", "*.example.org", "example.org.", "user@example.org", "example.org:443", "https://example.org", "a b", "127.0.0.1", "[::1]" })
        {
            try { DesktopOpenRequests.FromLaunch(launch, new DesktopOpenOptions([]) { WebLinkHosts = [host] }); throw new InvalidOperationException($"'{host}' was accepted."); }
            catch (ArgumentException) { }
        }
        try { DesktopOpenRequests.FromLaunch(launch, new DesktopOpenOptions([]) { WebLinkHosts = default }); throw new InvalidOperationException("default hosts were accepted."); }
        catch (ArgumentException) { }
    }

    [Test, DisplayName("OPEN: registrations validate schemes, file types and the executable when created")]
    public void ValidatesRegistrations()
    {
        var registration = new DesktopOpenRegistration("Notes")
        {
            UrlSchemes = ["org.example.notes"],
            FileTypes = [new(".notes") { MimeType = "application/x-example-notes", Description = "Notes document", IconPath = "/opt/notes/notes.png" }, new(".tar.gz")],
        };
        Check(registration.FileTypes.Length == 2 && registration.Executable is null, "a valid registration");
        Check(new DesktopOpenRegistration("Notes").UrlSchemes.IsEmpty && new DesktopOpenRegistration("Notes").FileTypes.IsEmpty, "empty by default");
        Check(new DesktopOpenRegistration("Notes") { UrlSchemes = default }.UrlSchemes.IsEmpty, "default arrays are empty");
        Action[] invalid =
        [
            () => _ = new DesktopOpenRegistration(""),
            () => _ = new DesktopOpenRegistration("Notes\nExec=evil"),
            () => _ = new DesktopOpenRegistration(null!),
            () => _ = new DesktopOpenRegistration("Notes") { UrlSchemes = ["https"] },
            () => _ = new DesktopOpenRegistration("Notes") { UrlSchemes = ["file"] },
            () => _ = new DesktopOpenRegistration("Notes") { UrlSchemes = ["1abc"] },
            () => _ = new DesktopOpenRegistration("Notes") { UrlSchemes = ["notes", "NOTES"] },
            () => _ = new DesktopOpenRegistration("Notes") { FileTypes = [new(".notes"), new(".NOTES")] },
            () => _ = new DesktopOpenRegistration("Notes") { FileTypes = [null!] },
            () => _ = new DesktopOpenRegistration("Notes") { Executable = "notes" },
            () => _ = new DesktopOpenRegistration("Notes") { Executable = OperatingSystem.IsWindows() ? "C:\\a\"b.exe" : "/opt/a\"b" },
            () => _ = new DesktopFileType("notes"),
            () => _ = new DesktopFileType("."),
            () => _ = new DesktopFileType("..notes"),
            () => _ = new DesktopFileType(".notes."),
            () => _ = new DesktopFileType(".no tes"),
            () => _ = new DesktopFileType(".a/b"),
            () => _ = new DesktopFileType(".a;b"),
            () => _ = new DesktopFileType(".*"),
            () => _ = new DesktopFileType(".notes") { MimeType = "text" },
            () => _ = new DesktopFileType(".notes") { MimeType = "text/*" },
            () => _ = new DesktopFileType(".notes") { Description = "" },
            () => _ = new DesktopFileType(".notes") { IconPath = "a\"b" },
        ];
        for (int index = 0; index < invalid.Length; index++)
        {
            try { invalid[index](); throw new InvalidOperationException($"Invalid registration {index} was accepted."); }
            catch (ArgumentException) { }
        }
    }

    [Test, DisplayName("OPEN: unavailable registration reports its reason and validates its argument")]
    public async Task UnavailableRegistrationAsync()
    {
        var registration = DesktopOpenRegistrations.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        Check(DesktopServiceStatus.Get(registration) is CapabilityStatus.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "status");
        var notes = new DesktopOpenRegistration("Notes") { UrlSchemes = ["notes"] };
        Check(await registration.RegisterAsync(notes) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "register");
        Check(await registration.IsRegisteredAsync(notes) is PlatformResult<bool>.Unavailable, "is registered");
        Check(await registration.UnregisterAsync(notes) is PlatformResult<bool>.Unavailable, "unregister");
        try { await registration.RegisterAsync(null!); throw new InvalidOperationException("null was accepted."); }
        catch (ArgumentNullException) { }
        try { DesktopOpenRegistrations.Unavailable((PlatformUnavailableReason)99); throw new InvalidOperationException("an undefined reason was accepted."); }
        catch (ArgumentOutOfRangeException) { }
    }

    [Test, DisplayName("OPEN: path bookmarks keep the path and resolve while the item exists")]
    public async Task PathBookmarksAsync()
    {
        var bookmarks = new PathFileBookmarks();
        string file = Path.Combine(Path.GetTempPath(), "runic bookmark " + Guid.NewGuid().ToString("N")[..8] + " é.txt");
        await File.WriteAllTextAsync(file, "probe");
        try
        {
            Check((await bookmarks.CreateAsync(file)).TryGetValue(out var bookmark) && !bookmark.IsEmpty, "a bookmark for an existing file");
            var resolved = await bookmarks.ResolveAsync(bookmark);
            Check(resolved.TryGetValue(out var access) && access.LocalPath == file && !access.IsStale, $"resolves to the file: {resolved}");
            await access!.DisposeAsync();
            File.Delete(file);
            Check(await bookmarks.ResolveAsync(bookmark) is PlatformResult<IDesktopBookmarkAccess>.Failed { Code: PlatformFailureCode.NotFound }, "a deleted file is not found");
            Check(await bookmarks.CreateAsync(file) is PlatformResult<ImmutableArray<byte>>.Failed { Code: PlatformFailureCode.NotFound }, "no bookmark for a missing file");
            Check(await bookmarks.ResolveAsync([0xFF, 0xFE]) is PlatformResult<IDesktopBookmarkAccess>.Failed { Code: PlatformFailureCode.InvalidData }, "invalid UTF-8");
            Check(await bookmarks.ResolveAsync([(byte)'a']) is PlatformResult<IDesktopBookmarkAccess>.Failed { Code: PlatformFailureCode.InvalidData }, "a relative path");
            foreach (var invalid in new Func<Task>[] { async () => await bookmarks.CreateAsync("relative.txt"), async () => await bookmarks.CreateAsync(""),
                async () => await bookmarks.ResolveAsync(default), async () => await bookmarks.ResolveAsync([]) })
            {
                try { await invalid(); throw new InvalidOperationException("An invalid argument was accepted."); }
                catch (ArgumentException) { }
            }
        }
        finally { File.Delete(file); }
    }

    [Test, DisplayName("OPEN: this launch and each later launch with files or URLs become requests, in order, waiting for the first handler")]
    public async Task DeliversThroughTheInstanceAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "runic-open-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var claim = await ApplicationInstance.ClaimAsync("org.example.Notes", new ApplicationLaunch([], "/"), directory, default);
            await using var instance = ((ApplicationInstanceClaim.Primary)((PlatformResult<ApplicationInstanceClaim>.Success)claim).Value).Instance;
            var launches = Channel.CreateUnbounded<ApplicationLaunch>();
            instance.Launched += (_, launch) => launches.Writer.TryWrite(launch);
            var opens = ApplicationInstance.CreateOpenRequests(Notes, instance, new ApplicationLaunch(["notes:first"], "/"));

            foreach (string argument in new[] { "--quiet", "notes:second", "notes:third" })
                Check(await ApplicationInstance.ClaimAsync("org.example.Notes", new ApplicationLaunch([argument], "/"), directory, default)
                    is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded }, "forwarded");
            for (int i = 0; i < 3; i++) await launches.Reader.ReadAsync().AsTask().WaitAsync(Wait);

            // Subscribed only now: the requests waited.
            var requests = Channel.CreateUnbounded<DesktopOpenRequest>();
            opens.Opened += (_, request) => requests.Writer.TryWrite(request);
            foreach (string expected in new[] { "notes:first", "notes:second", "notes:third" })
                Check((await requests.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Items.Single().ToString() == expected, $"{expected}, in order");
            Check(!requests.Reader.TryRead(out _), "a launch without files or URLs raises nothing");

            await opens.DisposeAsync();
            Check(await ApplicationInstance.ClaimAsync("org.example.Notes", new ApplicationLaunch(["notes:after"], "/"), directory, default)
                is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded }, "the instance still runs");
            Check((await launches.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Arguments.Single() == "notes:after", "the instance's own handler still receives launches");
            await Task.Delay(200);
            Check(!requests.Reader.TryRead(out _), "disposal stops the requests");

            await using var unavailable = DesktopOpenRequests.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
            unavailable.Opened += (_, _) => throw new InvalidOperationException("Unavailable open requests raised Opened.");
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

    // The desktop's own registration starts the application with a URL or file: CI runners only, in an isolated
    // session (Linux) or with a scheme unique to the run (Windows), so a developer's desktop is never touched.
    [Test, Explicit, Category("NativeOpen"), DisplayName("OPEN: a URL and a file opened through the desktop reach the running instance")]
    public async Task DesktopRegistrationAsync()
    {
        if (Environment.GetEnvironmentVariable("RUNIC_TEST_OPEN_REGISTRATION") != "1")
            throw new InvalidOperationException("Runs only on CI runners: it registers a URL scheme and file handler with the desktop.");
        if (OperatingSystem.IsLinux()) await LinuxAsync();
        else if (OperatingSystem.IsWindows()) await WindowsAsync();
        else throw new PlatformNotSupportedException("macOS registration is checked by the CI bundle script.");
    }

    // The registration API writes the desktop entry (and the MIME definition, refreshed with update-mime-database when
    // installed), refusing an ID the "package" installed; gio then starts this executable, which RUNIC_OPEN_PROBE
    // (passed to gio's environment) makes forward to the running instance. Then D-Bus activation: a service file in
    // XDG_DATA_HOME, an Open call that starts the activatable probe (cold), and Open, Activate and ActivateAction calls
    // that reach the same process (warm).
    private static async Task LinuxAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        // The CI step runs this under dbus-run-session with a temporary HOME and XDG directories and no display.
        string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? throw new InvalidOperationException("Set XDG_DATA_HOME.");
        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? throw new InvalidOperationException("Set XDG_CONFIG_HOME.");
        string systemData = Environment.GetEnvironmentVariable("XDG_DATA_DIRS")?.Split(':')[0] ?? throw new InvalidOperationException("Set XDG_DATA_DIRS.");
        string suffix = Guid.NewGuid().ToString("N")[..8], scheme = "runic-open-" + suffix, id = "org.runic.tests.Open" + suffix;
        Console.WriteLine($"update-desktop-database: {Which("update-desktop-database") ?? "not installed"}; update-mime-database: {Which("update-mime-database") ?? "not installed"}");
        await using var primary = await ProbeAsync(id, scheme);
        string desktopId = id + ".desktop";

        // An entry the package installed is never overwritten.
        string packaged = "org.runic.tests.Packaged" + suffix;
        System.IO.Directory.CreateDirectory(Path.Combine(systemData, "applications"));
        File.WriteAllText(Path.Combine(systemData, "applications", packaged + ".desktop"), "[Desktop Entry]\nType=Application\nName=Packaged\nExec=/usr/bin/packaged %U\n");
        var refused = await new Runic.Platform.Linux.Portal.PortalApplication(packaged).CreateOpenRegistration()
            .RegisterAsync(new DesktopOpenRegistration("Packaged") { UrlSchemes = [scheme + "-packaged"] });
        Check(refused is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.Conflict }, $"a packaged entry is a conflict: {refused}");
        Check(!File.Exists(Path.Combine(data, "applications", packaged + ".desktop")), "nothing is written for a packaged entry");

        var registration = new Runic.Platform.Linux.Portal.PortalApplication(id).CreateOpenRegistration();
        var probe = new DesktopOpenRegistration("Runic open probe") { UrlSchemes = [scheme], FileTypes = [new(".txt") { MimeType = "text/plain" }] };
        Check(await registration.RegisterAsync(probe) is PlatformResult<PlatformUnit>.Success, "the scheme and file type are registered");
        Check(await registration.IsRegisteredAsync(probe) is PlatformResult<bool>.Success { Value: true }, "the entry points to this executable");
        System.IO.Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "mimeapps.list"), $"[Default Applications]\nx-scheme-handler/{scheme}={desktopId}\ntext/plain={desktopId}\n");
        string file = Path.Combine(Path.GetTempPath(), "runic open " + suffix + ".txt");
        File.WriteAllText(file, "probe");
        try
        {
            var environment = new Dictionary<string, string> { ["RUNIC_OPEN_PROBE"] = id + " " + scheme };
            Run(environment, "gio", "open", scheme + "://item?id=1");
            string url = await primary.NextAsync();
            Check(new Uri(url) == new Uri(scheme + "://item?id=1"), $"gio opened the URL through the registered entry: {url}");
            Run(environment, "gio", "open", file);
            string opened = await primary.NextAsync();
            Check(new Uri(opened).LocalPath == file, $"gio opened the file through the registered entry: {opened}");
            Check(await registration.UnregisterAsync(probe) is PlatformResult<bool>.Success { Value: true }, "unregistered");
            Check(!File.Exists(Path.Combine(data, "applications", desktopId)), "the entry is removed");
            Console.WriteLine("PASS open: IDesktopOpenRegistration wrote a desktop entry through which gio delivered a URL and a file to the running instance; a packaged entry was refused.");
        }
        finally { File.Delete(file); }

        await DBusActivationAsync(data, suffix);
    }

    private static async Task DBusActivationAsync(string data, string suffix)
    {
        string id = "org.runic.tests.Activation" + suffix, scheme = "runic-activation-" + suffix, path = "/" + id.Replace('.', '/');
        string log = Path.Combine(Path.GetTempPath(), "runic-activation-" + suffix + ".log");
        System.IO.Directory.CreateDirectory(Path.Combine(data, "dbus-1", "services"));
        File.WriteAllText(Path.Combine(data, "dbus-1", "services", id + ".service"),
            $"[D-BUS Service]\nName={id}\nExec={string.Join(' ', new[] { Executable() }.Concat(ProbeArguments(id, scheme)).Select(static argument => "\"" + argument + "\""))} --dbus-probe \"{log}\"\n");
        System.IO.Directory.CreateDirectory(Path.Combine(data, "applications"));
        File.WriteAllText(Path.Combine(data, "applications", id + ".desktop"),
            $"[Desktop Entry]\nType=Application\nName=Activation\nExec=probe %U\nDBusActivatable=true\nActions=compose;\n\n[Desktop Action compose]\nName=Compose\nExec=probe --compose\n");
        Run(null, "gdbus", "call", "--session", "--dest", "org.freedesktop.DBus", "--object-path", "/org/freedesktop/DBus", "--method", "org.freedesktop.DBus.ReloadConfig");
        int? pid = null;
        try
        {
            // Cold: the bus starts the probe, which claims the instance and owns the name; the call waits for it.
            Run(null, "gdbus", "call", "--session", "--timeout", "30", "--dest", id, "--object-path", path, "--method", "org.freedesktop.Application.Open",
                $"['{scheme}://cold']", "{'activation-token': <'cold-token'>}");
            var lines = await LogAsync(log, static lines => lines.Any(static line => line.StartsWith("open ", StringComparison.Ordinal)));
            pid = lines.FirstOrDefault(static line => line.StartsWith("pid ", StringComparison.Ordinal)) is { } first ? int.Parse(first[4..], System.Globalization.CultureInfo.InvariantCulture) : null;
            Check(pid is not null && lines.Contains($"open {scheme}://cold/") && lines.Contains($"launch [{scheme}://cold] token=cold-token"),
                "the bus started the probe and delivered Open with the token: " + string.Join(" | ", lines));
            // Warm: the same process answers.
            Run(null, "gdbus", "call", "--session", "--dest", id, "--object-path", path, "--method", "org.freedesktop.Application.Open", $"['{scheme}://warm']", "{}");
            Run(null, "gdbus", "call", "--session", "--dest", id, "--object-path", path, "--method", "org.freedesktop.Application.Activate", "{'activation-token': <'warm-token'>}");
            Run(null, "gdbus", "call", "--session", "--dest", id, "--object-path", path, "--method", "org.freedesktop.Application.ActivateAction", "compose", "[]", "{}");
            lines = await LogAsync(log, static lines => lines.Any(static line => line == "launch [--compose] token="));
            Check(lines.Contains($"open {scheme}://warm/") && lines.Contains("launch [] token=warm-token") && lines.Count(static line => line.StartsWith("pid ", StringComparison.Ordinal)) == 1,
                "Open, Activate and ActivateAction reached the running probe: " + string.Join(" | ", lines));
            Console.WriteLine("PASS D-Bus activation: the bus started the activatable probe for Open (cold), and Open, Activate and ActivateAction reached it (warm).");
        }
        finally
        {
            if (pid is { } running) try { Process.GetProcessById(running).Kill(); } catch (ArgumentException) { } catch (InvalidOperationException) { }
            File.Delete(log);
        }
    }

    private static async Task<List<string>> LogAsync(string log, Func<List<string>, bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var lines = File.Exists(log) ? (await File.ReadAllLinesAsync(log)).ToList() : [];
            if (done(lines)) return lines;
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("The activation probe did not log in time: " + string.Join(" | ", lines));
            await Task.Delay(200);
        }
    }

    private static string? Which(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Select(directory => Path.Combine(directory, program)).FirstOrDefault(File.Exists);

    // The D-Bus-activatable probe the bus starts: claims the instance, exports its activation and logs each launch and
    // open request until it is killed or a minute passes.
    internal static async Task<int> RunDBusProbeAsync(string id, string scheme, string log)
    {
        if (!OperatingSystem.IsLinux()) return 1;
        var claim = await ApplicationInstance.ClaimAsync(id, ApplicationLaunch.FromProcess([]));
        if (claim is not PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Primary { Instance: var instance } }) return 1;
        var gate = new Lock();
        void Write(string line) { lock (gate) File.AppendAllLines(log, [line]); }
        await using (instance)
        {
            instance.Launched += (_, launch) => Write($"launch [{string.Join(' ', launch.Arguments)}] token={launch.PlatformContext?.ActivationToken}");
            await using var opens = ApplicationInstance.CreateOpenRequests(new DesktopOpenOptions([scheme]), instance);
            opens.Opened += (_, request) => { foreach (var item in request.Items) Write("open " + item); };
            var exported = await new Runic.Platform.Linux.Portal.PortalApplication(id).ExportApplicationAsync(instance);
            if (!exported.TryGetValue(out var export)) { Write("export failed " + exported); return 1; }
            Write("pid " + Environment.ProcessId);
            await using (export) await Task.Delay(TimeSpan.FromSeconds(60));
        }
        return 0;
    }

    // The registration API writes the scheme (IDesktopOpenRegistration), refusing a foreign entry first; the shell then
    // starts this executable with the URL as its only argument (Program.cs), which forwards it to the running instance.
    private static async Task WindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        string suffix = Guid.NewGuid().ToString("N")[..8], scheme = "runic-open-" + suffix, id = "org.runic.tests.Open" + suffix;
        string foreign = scheme + "-foreign", foreignKey = @"Software\Classes\" + foreign;
        await using var primary = await ProbeAsync(id, scheme);
        var registration = Runic.Platform.Windows.WindowsPlatformProvider.CreateOpenRegistration(id);
        var probe = new DesktopOpenRegistration("Runic open probe") { UrlSchemes = [scheme], FileTypes = [new("." + scheme)] };
        try
        {
            using (var command = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(foreignKey + @"\shell\open\command"))
                command.SetValue("", "\"C:\\Other\\Other.exe\" \"%1\"");
            var refused = await registration.RegisterAsync(probe with { UrlSchemes = [scheme, foreign] });
            Check(refused is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.Conflict }, $"a foreign protocol is a conflict: {refused}");
            Check(Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + scheme) is null, "nothing is written when one item conflicts");

            Check(await registration.RegisterAsync(probe) is PlatformResult<PlatformUnit>.Success, "the scheme and file type are registered");
            Check(await registration.IsRegisteredAsync(probe) is PlatformResult<bool>.Success { Value: true }, "the registration points to this executable");
            using (Process.Start(new ProcessStartInfo(scheme + ":item?id=1") { UseShellExecute = true })) { }
            string url = await primary.NextAsync();
            Check(new Uri(url) == new Uri(scheme + ":item?id=1"), $"the shell opened the URL through the registered protocol: {url}");
            Check(await registration.UnregisterAsync(probe) is PlatformResult<bool>.Success { Value: true }, "unregistered");
            Check(await registration.IsRegisteredAsync(probe) is PlatformResult<bool>.Success { Value: false }, "no longer registered");
            Check(Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + scheme) is null, "the protocol key is removed");
            Check(Microsoft.Win32.Registry.CurrentUser.OpenSubKey(foreignKey) is not null, "the foreign protocol stays");
            Console.WriteLine("PASS open: IDesktopOpenRegistration registered a URL protocol that the Windows shell opened into the running instance; a foreign entry was refused.");
        }
        finally
        {
            await registration.UnregisterAsync(probe);
            foreach (string key in new[] { foreignKey, @"Software\Classes\" + scheme, @"Software\Classes\." + scheme, @"Software\Classes\" + id + "." + scheme, @"Software\" + id })
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            using var registered = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
            registered?.DeleteValue(id, throwOnMissingValue: false);
        }
    }

    // The process the shell starts for a registered probe scheme: its only argument is the URL, whose scheme names the
    // probe's instance.
    internal static (string Id, string Scheme)? RegisteredProbe(string argument)
    {
        int colon = argument.IndexOf(':', StringComparison.Ordinal);
        if (colon != "runic-open-".Length + 8 || !argument.StartsWith("runic-open-", StringComparison.OrdinalIgnoreCase)) return null;
        string suffix = argument["runic-open-".Length..colon].ToLowerInvariant();
        return suffix.All(char.IsAsciiHexDigitLower) ? ("org.runic.tests.Open" + suffix, "runic-open-" + suffix) : null;
    }

    // The probe: the running instance of a test application, in another process, that prints each request's items.
    private static async Task<Probe> ProbeAsync(string id, string scheme)
    {
        var start = new ProcessStartInfo(Executable()) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string argument in ProbeArguments(id, scheme)) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The probe did not start.");
        var probe = new Probe(process);
        Check(await probe.LineAsync() == "ready", "the probe is the running instance");
        return probe;
    }

    private static string Executable() => Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable");

    private static List<string> ProbeArguments(string id, string scheme)
    {
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(Executable()).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Path.Combine(AppContext.BaseDirectory, "Runic.Platform.Runtime.Tests.dll"));
        arguments.AddRange(["--open-probe", id, scheme]);
        return arguments;
    }

    // The paths CI uses need no escaping inside the quotes.
    private static string DesktopExec(string id, string scheme) =>
        string.Join(' ', new[] { Executable() }.Concat(ProbeArguments(id, scheme)).Select(static argument => "\"" + argument + "\""));

    private static string WindowsCommand(string id, string scheme) =>
        string.Join(' ', new[] { Executable() }.Concat(ProbeArguments(id, scheme)).Select(static argument => "\"" + argument + "\""));

    private static void Run(IReadOnlyDictionary<string, string>? environment, string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
        process.WaitForExit();
        Check(process.ExitCode == 0, $"{program} {string.Join(' ', arguments)} exited with {process.ExitCode}");
    }

    // The probe process's side: claim the instance, deliver open requests and print each item until stdin closes.
    internal static async Task<int> RunProbeAsync(string id, string scheme, string[] arguments)
    {
        var launch = ApplicationLaunch.FromProcess(arguments);
        var claim = await ApplicationInstance.ClaimAsync(id, launch);
        if (claim is PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Forwarded }) return 0;
        if (claim is not PlatformResult<ApplicationInstanceClaim>.Success { Value: ApplicationInstanceClaim.Primary { Instance: var instance } }) return 1;
        await using (instance)
        {
            await using var opens = ApplicationInstance.CreateOpenRequests(new DesktopOpenOptions([scheme]) { IncludeFiles = true }, instance, launch);
            opens.Opened += (_, request) => { foreach (var item in request.Items) Console.WriteLine(item.ToString()); };
            Console.WriteLine("ready");
            await Task.Delay(TimeSpan.FromSeconds(60));
        }
        return 0;
    }

    private sealed class Probe(Process process) : IAsyncDisposable
    {
        internal async Task<string?> LineAsync() => await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        internal async Task<string> NextAsync() => await LineAsync() ?? throw new InvalidOperationException("The probe ended.");

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
            process.Dispose();
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
