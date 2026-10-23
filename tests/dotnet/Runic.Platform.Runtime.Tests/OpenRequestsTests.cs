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

    private static async Task LinuxAsync()
    {
        // The CI step runs this under dbus-run-session with a temporary HOME and XDG directories and no display.
        string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? throw new InvalidOperationException("Set XDG_DATA_HOME.");
        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? throw new InvalidOperationException("Set XDG_CONFIG_HOME.");
        string scheme = "runic-open-" + Guid.NewGuid().ToString("N")[..8], id = "org.runic.tests.Open" + Guid.NewGuid().ToString("N")[..8];
        await using var primary = await ProbeAsync(id, scheme);
        string desktopId = id + ".desktop";
        System.IO.Directory.CreateDirectory(Path.Combine(data, "applications"));
        File.WriteAllText(Path.Combine(data, "applications", desktopId),
            $"[Desktop Entry]\nType=Application\nName=Runic open probe\nExec={DesktopExec(id, scheme)} %U\nMimeType=x-scheme-handler/{scheme};text/plain;\nNoDisplay=true\n");
        System.IO.Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "mimeapps.list"), $"[Default Applications]\nx-scheme-handler/{scheme}={desktopId}\ntext/plain={desktopId}\n");
        string file = Path.Combine(Path.GetTempPath(), "runic open " + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(file, "probe");
        try
        {
            Run("gio", "open", scheme + "://item?id=1");
            string url = await primary.NextAsync();
            Check(new Uri(url) == new Uri(scheme + "://item?id=1"), $"gio opened the URL through the x-scheme-handler entry: {url}");
            Run("gio", "open", file);
            string opened = await primary.NextAsync();
            Check(new Uri(opened).LocalPath == file, $"gio opened the file through the MimeType entry: {opened}");
            Console.WriteLine("PASS open: gio delivered a URL and a file through a desktop entry to the running instance.");
        }
        finally { File.Delete(file); }
    }

    private static async Task WindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        string scheme = "runic-open-" + Guid.NewGuid().ToString("N")[..8], id = "org.runic.tests.Open" + Guid.NewGuid().ToString("N")[..8];
        await using var primary = await ProbeAsync(id, scheme);
        string key = @"Software\Classes\" + scheme;
        try
        {
            using (var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key))
            {
                root.SetValue("", "URL:Runic open probe");
                root.SetValue("URL Protocol", "");
                using var command = root.CreateSubKey(@"shell\open\command");
                command.SetValue("", WindowsCommand(id, scheme) + " \"%1\"");
            }
            using (Process.Start(new ProcessStartInfo(scheme + ":item?id=1") { UseShellExecute = true })) { }
            string url = await primary.NextAsync();
            Check(new Uri(url) == new Uri(scheme + ":item?id=1"), $"the shell opened the URL through the registered protocol: {url}");
            Console.WriteLine("PASS open: the Windows shell delivered a registered URL to the running instance.");
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); }
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

    private static void Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
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
