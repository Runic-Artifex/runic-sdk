using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Generated;
using Runic.Desktop;

#pragma warning disable RUNICDESK103 // The smoke checks the experimental runic-host/1 window mode.

// runic-host/1 window mode (S11, SH3) in the built-in web views: this executable hosts itself as guest applications
// (--hosted-guest) and shows their Windows in native windows of the host. Each guest page loads top-level from the guest's
// loopback origin through its one-time launch address, in the host session's own data store: it connects to the guest's
// surface without the host's credential, sees none of the host's cookies and none of another guest's. A user's close goes
// through the guest, a guest that crashes loses its windows, and a stopped guest exits. Run it without arguments (WebView2,
// WKWebView, WebKitGTK 4.1) or gtk4 (WebKitGTK 6).
internal static class HostedSmoke
{
    internal const string GuestArgument = "--hosted-guest";
    private const string Report = "RUNIC_HOSTED_SMOKE ";
    private const int CrashExitCode = 7;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    // The host. Must run before the first await: on macOS the AppKit loop owns the main thread.
    internal static int Run(DesktopHostOptions options, string toolkit)
    {
        var logs = new GuestOutput();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(logs);
        services.AddRunicHostedApplications();
        using var provider = services.BuildServiceProvider();
        return RunicDesktopHost.Run(provider, options, new RunicDesktopHostOptions(), host => RunHostAsync(host, logs, toolkit));
    }

    private static async Task<int> RunHostAsync(RunicDesktopHost host, GuestOutput logs, string toolkit)
    {
        // The host's own page sets a cookie for 127.0.0.1, which cookies share across ports.
        await using var surface = await host.Desktop.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page("Host")) });
        await using var hostWindow = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 420, Height = 300 });
        var hostCookies = await surface.ExecuteJavaScriptAsync("document.cookie = 'runic_host_probe=1; path=/'; return document.cookie;");
        Check(hostCookies.Contains("runic_host_probe=1", StringComparison.Ordinal), $"The host's page could not set its cookie: '{hostCookies}'.");

        var applications = host.HostedApplications;
        var opened = new Dictionary<int, RunicHostedWindow>();
        var openedChanged = new SemaphoreSlim(0);
        applications.WindowOpened += (_, e) =>
        {
            lock (opened) opened[e.Window.Application.ProcessId] = e.Window;
            openedChanged.Release();
        };
        async Task<RunicHostedWindow> WindowOf(RunicHostedApplication application)
        {
            using var timeout = new CancellationTokenSource(TestTimeouts.Scale(Wait));
            while (true)
            {
                lock (opened)
                {
                    if (opened.TryGetValue(application.ProcessId, out var window)) return window;
                }
                await openedChanged.WaitAsync(timeout.Token);
            }
        }

        // WebView2 keeps each guest session's InPrivate profile in a user data folder of its own in the temporary folder.
        var foldersBefore = HostedFolders();

        // Guest a: its page connects to its own surface, sees no cookie of the host and sets its own.
        var a = await applications.StartAsync(Guest("a", "stay"));
        var reportA = await logs.ReportAsync("a");
        var reportedA = DateTime.UtcNow;
        var foldersA = HostedFolders().Except(foldersBefore).ToArray();
        var windowA = await WindowOf(a);
        CheckPage(reportA, "a", host.Desktop.Port, mustNotSee: ["runic_host_probe"]);
        Check(windowA.IsOpen && windowA.Presentation.Browser == BrowserKind.Embedded && windowA.Presentation.NativeHandle != 0,
            "Guest a's window is not an open native embedded window of the host.");
        Console.WriteLine($"PASS hosted {toolkit}: guest a's page loaded from {reportA["origin"]} in a window of the host, without the host's cookie.");

        // Guest b: a data store of its own, without guest a's cookie; then it crashes.
        var b = await applications.StartAsync(Guest("b", "crash"));
        var reportB = await logs.ReportAsync("b");
        var windowB = await WindowOf(b);
        CheckPage(reportB, "b", host.Desktop.Port, mustNotSee: ["runic_host_probe", "runic_guest_a"]);
        Console.WriteLine($"PASS hosted {toolkit}: guest b's page sees neither the host's nor guest a's cookie.");
        Check(await b.Exited.WaitAsync(TestTimeouts.Scale(Wait)) == CrashExitCode, "Guest b's exit code was not reported.");
        await windowB.Closed.WaitAsync(TestTimeouts.Scale(Wait));
        Check(!windowB.Presentation.IsOpen && windowA.IsOpen, "The crashed guest's window is open, or guest a's closed.");
        Console.WriteLine($"PASS hosted {toolkit}: the host closed the crashed guest's window and reported its exit code.");

        if (OperatingSystem.IsWindows())
        {
            // Guest a's page set a persistent cookie and localStorage. WebView2 writes a profile's cookies within about 30
            // seconds and its storage sooner; an InPrivate profile writes neither. Its folder holds the files of the
            // profile an InPrivate one derives from, without the guest's data.
            Check(foldersA.Length == 1, $"Guest a's session has {foldersA.Length} user data folders.");
            var wait = TimeSpan.FromSeconds(40) - (DateTime.UtcNow - reportedA);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            var (scanned, found) = FindOnDisk(foldersA[0], "runic_guest_a");
            Check(found.Length == 0, $"Guest a's data is on disk, in {string.Join(", ", found)}.");
            Check(scanned > 0, "No file of guest a's user data folder could be read.");
            Console.WriteLine($"PASS hosted {toolkit}: none of the {scanned} files in guest a's user data folder holds its cookie or storage.");
        }

        // A close through the guest: its Window closes, then the host's window, then the guest exits.
        Check(await windowA.RequestCloseAsync(), "Guest a refused the close.");
        await windowA.Closed.WaitAsync(TestTimeouts.Scale(Wait));
        Check(await a.Exited.WaitAsync(TestTimeouts.Scale(Wait)) == 0, "Guest a did not exit cleanly after its window closed.");
        Console.WriteLine($"PASS hosted {toolkit}: the window closed through the guest, which then exited.");

        // A stop: shutdown closes the guest's Windows and it exits.
        var c = await applications.StartAsync(Guest("c", "stay"));
        _ = await logs.ReportAsync("c");
        var windowC = await WindowOf(c);
        Check(await c.StopAsync().AsTask().WaitAsync(TestTimeouts.Scale(Wait)) == 0, "Guest c did not exit cleanly after shutdown.");
        Check(windowC.Closed.IsCompleted && !windowC.Presentation.IsOpen, "Guest c's window is still open after it stopped.");
        Check(applications.Applications.Count == 0, "A guest is still listed after it exited.");
        Console.WriteLine($"PASS hosted {toolkit}: the stopped guest's window closed and the guest exited.");

        if (OperatingSystem.IsWindows())
        {
            // Each ended session's folder is deleted once WebView2's browser process let go of it.
            var left = HostedFolders().Except(foldersBefore).ToArray();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (left.Length > 0 && !deadline.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                left = HostedFolders().Except(foldersBefore).ToArray();
            }
            Check(left.Length == 0, $"The ended sessions' user data folders were not deleted: {string.Join(", ", left)}.");
            Console.WriteLine($"PASS hosted {toolkit}: the ended sessions' user data folders were deleted.");
        }

        await hostWindow.CloseAsync();
        return 0;
    }

    // The files under the folder that contain the text in ASCII or UTF-16, and how many files could be read; a file the
    // browser holds without sharing is skipped.
    private static (int Scanned, string[] Found) FindOnDisk(string folder, string text)
    {
        var ascii = System.Text.Encoding.ASCII.GetBytes(text);
        var utf16 = System.Text.Encoding.Unicode.GetBytes(text);
        var scanned = 0;
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            byte[] content;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 64 * 1024 * 1024) continue;
                content = new byte[stream.Length];
                stream.ReadExactly(content);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            scanned++;
            if (content.AsSpan().IndexOf(ascii) >= 0 || content.AsSpan().IndexOf(utf16) >= 0) found.Add(Path.GetRelativePath(folder, file));
        }
        return (scanned, [.. found]);
    }

    private static string[] HostedFolders() =>
        OperatingSystem.IsWindows() ? Directory.GetDirectories(Path.GetTempPath(), "runic-desktop-hosted-*") : [];

    private static void CheckPage(JsonObject report, string name, int hostPort, string[] mustNotSee)
    {
        var origin = new Uri(report["origin"]!.GetValue<string>());
        // 127.0.0.1, the host page's own host: cookies ignore ports, so only there does a missing host cookie prove that
        // the stores are apart. A guest on [::1] would pass the check without testing anything.
        Check(origin.Host == "127.0.0.1" && origin.Port != hostPort, $"Guest {name}'s page is not on its own 127.0.0.1 origin: {origin}.");
        var cookies = report["cookies"]!.GetValue<string>();
        Check(cookies.Contains($"runic_guest_{name}=1", StringComparison.Ordinal), $"Guest {name}'s page could not set its cookie: '{cookies}'.");
        foreach (var cookie in mustNotSee)
            Check(!cookies.Contains(cookie, StringComparison.Ordinal), $"Guest {name}'s page sees the cookie {cookie}: '{cookies}'.");
        // The host injects no credential into the guest's documents.
        Check(report["bootstrap"]!.GetValue<string>() == "undefined", $"Guest {name}'s page received a document-start credential.");
    }

    private static RunicGuestStartInfo Guest(string name, string behaviour)
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("The smoke has no process path.");
        var arguments = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        arguments.AddRange([GuestArgument, name, behaviour]);
        return new RunicGuestStartInfo(path, arguments) { WorkingDirectory = AppContext.BaseDirectory };
    }

    // A guest: one Window, whose open waits for its page to connect in the host's window. It reports what the page sees
    // on its standard output, which the host logs.
    internal static int RunGuest(string name, string behaviour)
    {
        var services = new ServiceCollection();
        services.AddScoped<HostedSmokeModel>();
        services.AddScoped<Func<IBridgeTransport, HostedSmokeModel, IDisposable>>(_ => (transport, model) => new HostedSmokeBridge(transport, model));
        using var provider = services.BuildServiceProvider();
        try
        {
            return RunicDesktopHost.Run(provider, new DesktopHostOptions { ConnectionTimeout = TestTimeouts.Scale(Wait) },
                new RunicDesktopHostOptions
                {
                    SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page("Guest " + name)) },
                    Guest = RunicGuestOptions.FromStandardInput() with
                    {
                        ApplicationName = "Hosted smoke " + name,
                        ApplicationVersion = "1.0.0",
                        ApplicationId = "dev.runic.smoke.hosted-" + name,
                    },
                },
                async host =>
                {
                    await using var window = await host.OpenWindowAsync<HostedSmokeWindow>();
                    var surface = RunicDesktopHost.GetSurface(window) ?? throw new InvalidOperationException("The Window has no surface.");
                    var seen = await surface.ExecuteJavaScriptAsync($$"""
                        document.cookie = "runic_guest_{{name}}=1; path=/; max-age=3600";
                        localStorage.setItem("runic_guest_{{name}}", "1");
                        return JSON.stringify({
                          origin: location.origin,
                          cookies: document.cookie,
                          bootstrap: typeof globalThis[Symbol.for("runic-desktop.bootstrap")]
                        });
                        """);
                    Console.Out.Write(Report + name + " " + seen + "\n");
                    Console.Out.Flush();
                    if (behaviour == "crash")
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1));
                        Environment.Exit(CrashExitCode);
                    }
                    await window.WaitForCloseAsync();
                    return 0;
                });
        }
        catch (RunicHostedApplicationException error)
        {
            Console.Error.WriteLine($"Hosted mode did not start ({error.Code}): {error.Message}");
            return 3;
        }
    }

    private static string Page(string title) =>
        $"<!doctype html><html><head><title>{title}</title><script src=\"webui.js\"></script></head><body><h1>{title}</h1></body></html>";

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // The host's log: it shows everything, and keeps the guests' reports, which their standard output carries (event 2021).
    private sealed class GuestOutput : ILoggerFactory
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource<JsonObject>> _reports = new(StringComparer.Ordinal);

        public async Task<JsonObject> ReportAsync(string name) => await Report(name).Task.WaitAsync(TestTimeouts.Scale(Wait));

        private TaskCompletionSource<JsonObject> Report(string name)
        {
            lock (_gate)
            {
                if (!_reports.TryGetValue(name, out var report))
                    _reports[name] = report = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                return report;
            }
        }

        internal void Record(string message)
        {
            var start = message.IndexOf(HostedSmoke.Report, StringComparison.Ordinal);
            if (start < 0) return;
            var rest = message[(start + HostedSmoke.Report.Length)..];
            var space = rest.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0) return;
            Report(rest[..space]).TrySetResult(JsonNode.Parse(rest[(space + 1)..])!.AsObject());
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class Logger(GuestOutput output, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                Console.Error.WriteLine($"{logLevel} {category}[{eventId.Id}]: {message}{(exception is null ? "" : " " + exception)}");
                if (eventId.Id == 2021) output.Record(message);
            }
        }
    }
}

internal sealed class HostedSmokeWindow : RunicWindow<HostedSmokeModel>;

internal sealed class HostedSmokeModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

internal sealed class HostedSmokeBridge(IBridgeTransport transport, HostedSmokeModel model)
    : ViewModelBridge<HostedSmokeModel>(transport, model, "hosted-smoke", [], [], [], []);
