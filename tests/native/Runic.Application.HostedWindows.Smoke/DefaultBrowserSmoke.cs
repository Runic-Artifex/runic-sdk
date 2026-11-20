using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;

// #1174 (I-163): the desktop's real default handlers, launched through the public API: the window's file launcher
// (DesktopPlatformServices.ForWindow) and the application's (ForApplication), which are the OpenURI portal on Linux,
// ShellExecuteEx on Windows and NSWorkspace openURL: on macOS.
// - Windows and macOS: the user's default browser. LaunchUriAsync opens http://127.0.0.1:<port>/ URLs of a loopback
//   listener of the smoke, which requires the browser's request for each; it registers nothing. On Windows the shell
//   hands a URL to the user's choice (UserChoice), which a test cannot set or redirect (a hash protects the choice, and
//   the shell's broker resolves it outside the process, so a class override of the user's does not apply). Then
//   (Windows) LaunchAsync refuses a .cer file, a type Windows registers and AssocIsDangerous names; (macOS) an
//   AppleScript applet made the mailto default with LSSetDefaultHandlerForURLScheme, if macOS lets it, records a
//   launched mailto URI.
// - Linux: a test handler, this executable with RecordArgument, as the mailto default in temporary XDG directories, for a
//   session bus of the smoke's own, so the portal it activates resolves the same default; and a really clicked mailto:
//   link of the page (XTEST), which the host opens with gtk_show_uri_on_window (GTK 3) or GtkUriLauncher (GTK 4).
internal static partial class DefaultBrowserSmoke
{
    internal const string Argument = "--default-browser";
    internal const string RecordArgument = "--record-launch";
    // The file the Linux handler appends to; set for the run after the registration.
    private const string RecordVariable = "RUNIC_DEFAULT_HANDLER_RECORD";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
    // A browser's first start on a fresh runner can take tens of seconds (BrowserWarmUp, I-180).
    private static readonly TimeSpan BrowserTimeout = TimeSpan.FromSeconds(90);

    // --record-launch <file> <uri>: the test handler. Appends the URI as one line.
    internal static int Record(string[] args)
    {
        var index = Array.IndexOf(args, RecordArgument);
        if (index < 0 || index + 2 >= args.Length) return 2;
        File.AppendAllText(args[index + 1], args[index + 2] + "\n");
        return 0;
    }

    internal static int Run(DesktopHostOptions options, string toolkit, string[] args)
    {
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable(RecordVariable) is not { Length: > 0 }) return RunLinuxSession(args);
        return DesktopEventLoop.Run(options, host => RunAsync(host, toolkit));
    }

    private static async Task<int> RunAsync(DesktopHost host, string toolkit)
    {
        var collection = new ServiceCollection();
        collection.AddRunicWindowService();
        await using var services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        // GTK 4 cannot place windows; without a window manager both toolkits' windows are at the origin.
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            Width = 400,
            Height = 300,
            X = toolkit == "gtk4" ? null : 0,
            Y = toolkit == "gtk4" ? null : 0,
        });
        await using var scope = services.CreateAsyncScope();
        await using var windowServices = DesktopPlatformServices.AttachWindowServices(window, scope.ServiceProvider);
        using (var ready = new CancellationTokenSource(TestTimeouts.Scale(StepTimeout)))
            await surface.ExecuteJavaScriptAsync("return document.readyState;", ready.Token);
        var forWindow = DesktopPlatformServices.ForWindow(windowServices.NativeOwner).FileLauncher;
        var forApplication = DesktopPlatformServices.ForApplication().FileLauncher;

        if (OperatingSystem.IsLinux())
        {
            var record = Environment.GetEnvironmentVariable(RecordVariable)!;
            await LaunchAsync(forWindow, Mailto("window"), "the window's file launcher");
            await ExpectAsync(record, Mailto("window"), "the window's file launcher");
            await LaunchAsync(forApplication, Mailto("application"), "the application's file launcher");
            await ExpectAsync(record, Mailto("application"), "the application's file launcher");
            // A real click: WebKit opens a link externally only for a user gesture.
            using var pointer = Pointer.Open()
                ?? throw new InvalidOperationException("XTEST is unavailable; set RUNIC_LIBXTST to a libXtst.so.6 for this architecture.");
            await Task.Delay(500);
            await pointer.ClickAsync(200, 150);
            var opener = toolkit == "gtk4" ? "GtkUriLauncher" : "gtk_show_uri_on_window";
            await ExpectAsync(record, Mailto("link"), $"the clicked link ({opener})");
            Console.WriteLine($"PASS default handler ({toolkit}): the window's and the application's LaunchUriAsync (OpenURI portal) and a " +
                "clicked link reached the default mailto handler.");
            return 0;
        }

        if (OperatingSystem.IsWindows()) Console.WriteLine($"Default browser: {Association("http")}.");
        using var browser = new BrowserRequests();
        var native = OperatingSystem.IsWindows() ? "ShellExecuteEx" : "NSWorkspace openURL:";
        foreach (var (launcher, source, what) in new[] { (forWindow, "window", "the window's file launcher"), (forApplication, "application", "the application's file launcher") })
        {
            var uri = new Uri($"http://127.0.0.1:{browser.Port}/runic-launch-smoke?source={source}");
            await LaunchAsync(launcher, uri, what);
            var agent = await browser.ExpectAsync(uri.PathAndQuery, what);
            Console.WriteLine($"PASS default browser: {what} ({native}) opened {uri} in the default browser ({agent}).");
        }
        if (OperatingSystem.IsWindows()) await DangerousTypeAsync();
        if (OperatingSystem.IsMacOS()) await MacMailtoHandlerAsync(forApplication);
        Console.WriteLine($"PASS default browser ({toolkit}): the window's and the application's LaunchUriAsync ({native}) reached the default browser.");
        return 0;
    }

    private static Uri Mailto(string source) => new($"mailto:runic-launch-smoke@example.invalid?subject={source}");

    private static async Task LaunchAsync(IDesktopFileLauncher launcher, Uri uri, string what)
    {
        var result = await launcher.LaunchUriAsync(uri).AsTask().WaitAsync(TestTimeouts.Scale(StepTimeout));
        if (result is PlatformResult<PlatformUnit>.Success) return;
        if (OperatingSystem.IsWindows()) Console.WriteLine($"Default browser: {ShellExecuteDiagnostic(uri)}.");
        throw new InvalidOperationException($"LaunchUriAsync of {uri} through {what} returned {result}.");
    }

    // The handler recorded the URI, exactly, within the step's time.
    private static async Task ExpectAsync(string record, Uri uri, string what)
    {
        var expected = uri.AbsoluteUri;
        var waited = Stopwatch.StartNew();
        string[] lines = [];
        while (waited.Elapsed < TestTimeouts.Scale(StepTimeout))
        {
            lines = File.Exists(record) ? File.ReadAllLines(record) : [];
            if (lines.Contains(expected, StringComparer.Ordinal))
            {
                Console.WriteLine($"PASS default handler: {what} launched it with {expected} after {waited.ElapsedMilliseconds} ms.");
                return;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException(
            $"The default handler did not receive {expected} from {what} within {StepTimeout.TotalSeconds} s; it recorded [{string.Join(", ", lines)}].");
    }

    // A loopback HTTP listener that answers every request with a short page and keeps each request's path and
    // User-Agent: what the default browser asked for.
    private sealed class BrowserRequests : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<(string Path, string Agent)> _requests = new();

        internal BrowserRequests()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        internal async Task<string> ExpectAsync(string path, string what)
        {
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < TestTimeouts.Scale(BrowserTimeout))
            {
                foreach (var request in _requests)
                    if (request.Path == path) return request.Agent;
                await Task.Delay(100);
            }
            throw new TimeoutException($"The default browser did not request {path} from {what} within {BrowserTimeout.TotalSeconds} s; " +
                $"it requested [{string.Join(", ", _requests.Select(static request => request.Path))}].");
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = AnswerAsync(client);
                }
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var stream = client.GetStream();
                    var buffer = new byte[16 * 1024];
                    var length = 0;
                    while (length < buffer.Length)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
                        if (read == 0) break;
                        length += read;
                        if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                    }
                    var lines = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n");
                    var parts = lines[0].Split(' ');
                    if (parts.Length < 2) return;
                    var agent = lines.FirstOrDefault(static line => line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))?[11..].Trim() ?? "";
                    _requests.Enqueue((parts[1], agent));
                    var body = Encoding.UTF8.GetBytes("<!doctype html><title>Runic launch smoke</title><p>Opened by the Runic default browser smoke.");
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, timeout.Token);
                    await stream.WriteAsync(body, timeout.Token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }

    // The command that starts this executable again, with the given arguments.
    private static List<string> Self(params string[] args)
    {
        var self = new List<string> { Environment.ProcessPath ?? throw new InvalidOperationException("The smoke has no process path.") };
        if (string.Equals(Path.GetFileNameWithoutExtension(self[0]), "dotnet", StringComparison.OrdinalIgnoreCase))
            self.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        self.AddRange(args);
        return self;
    }

    // Linux: the handler's desktop file and the mailto default in temporary XDG directories, and the smoke again on a
    // session bus of its own with them, so the portal that bus activates sees them too.
    private static int RunLinuxSession(string[] args)
    {
        var root = Directory.CreateTempSubdirectory("runic-default-handler-");
        try
        {
            var data = Directory.CreateDirectory(Path.Combine(root.FullName, "data", "applications"));
            var config = Directory.CreateDirectory(Path.Combine(root.FullName, "config"));
            var record = Path.Combine(root.FullName, "recorded.txt");
            var exec = string.Join(' ', Self(RecordArgument, record).Select(DesktopFileArgument)) + " %u";
            File.WriteAllText(Path.Combine(data.FullName, "runic-launch-smoke.desktop"),
                $"[Desktop Entry]\nType=Application\nName=Runic launch smoke\nExec={exec}\nMimeType=x-scheme-handler/mailto;\nNoDisplay=true\n");
            File.WriteAllText(Path.Combine(config.FullName, "mimeapps.list"),
                "[Default Applications]\nx-scheme-handler/mailto=runic-launch-smoke.desktop\n");
            Console.WriteLine($"Default handler: registered runic-launch-smoke.desktop for mailto in {root.FullName} (Exec={exec}).");

            // In a process group of its own (setsid), whose services the smoke ends with it: a portal backend that the
            // bus activated can outlive the bus.
            var start = new ProcessStartInfo("setsid") { UseShellExecute = false };
            start.ArgumentList.Add("dbus-run-session");
            start.ArgumentList.Add("--");
            foreach (var argument in Self(args)) start.ArgumentList.Add(argument);
            start.Environment["XDG_DATA_HOME"] = Path.Combine(root.FullName, "data");
            start.Environment["XDG_CONFIG_HOME"] = config.FullName;
            start.Environment[RecordVariable] = record;
            using var session = Process.Start(start) ?? throw new InvalidOperationException("dbus-run-session did not start.");
            try
            {
                if (!session.WaitForExit(TimeSpan.FromMinutes(3)))
                    throw new TimeoutException("The default handler smoke did not finish within 3 minutes.");
                return session.ExitCode;
            }
            finally
            {
                // setsid made the session's process the group's leader.
                _ = Kill(-session.Id, 15);
                Thread.Sleep(500);
            }
        }
        finally
        {
            Remove(root);
        }
    }

    // A handler the desktop started may still be writing its record when the smoke ends.
    private static void Remove(DirectoryInfo root)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                root.Delete(recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(500);
            }
            catch (IOException error)
            {
                Console.WriteLine($"Default handler: {root.FullName} was not removed: {error.Message}");
                return;
            }
        }
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "kill")]
    private static partial int Kill(int process, int signal);

    // The Desktop Entry Specification's quoting of an Exec argument.
    private static string DesktopFileArgument(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal).Replace("$", "\\$", StringComparison.Ordinal) + "\"";

    // A page that is one link, for the click.
    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic default handler</title>
        <style>html,body{margin:0;height:100%} a{position:fixed;inset:0;display:block;background:#cde}</style></head>
        <body><a href="mailto:runic-launch-smoke@example.invalid?subject=link">Write</a></body></html>
        """;
}
