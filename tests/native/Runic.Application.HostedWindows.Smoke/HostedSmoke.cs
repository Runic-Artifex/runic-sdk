using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Generated;
using Runic.Desktop;
using Runic.Platform;

#pragma warning disable RUNICDESK103 // The smoke checks the experimental runic-host/1 window mode.

// runic-host/1 window mode (S11, SH3) in the built-in web views: this executable hosts itself as guest applications
// (--hosted-guest) and shows their Windows in native windows of the host. Each guest page loads top-level from the guest's
// loopback origin through its one-time launch address, in the host session's own data store: it connects to the guest's
// surface without the host's credential, sees none of the host's cookies and none of another guest's. A user's close goes
// through the guest, a guest that crashes loses its windows, and a stopped guest exits. On Windows the guest's cookie and
// storage never reach the disk, which a window with a persistent profile shows the same scan would find. Run it without
// arguments (WebView2, WKWebView, WebKitGTK 4.1) or gtk4 (WebKitGTK 6).
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
        // The host application's own bar, which its windows show and a guest's never does.
        host.Desktop.SetMenuBar([new DesktopMenu("Host Tools", [new DesktopMenuItem("Host Item", "host.item")])]);
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

        // Guest a: its page connects to its own surface, sees no cookie of the host and sets its own. It stays without a
        // window after its window closed, until the host stops it, so its session's folder stays while the scan below runs.
        var a = await applications.StartAsync(Guest("a", "linger"));
        var reportA = await logs.ReportAsync("a");
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

        // menus/1: guest a's bar shows in its native window, and the window's menu command path (the native bar and its
        // shortcuts) runs the guest's command.
        using (var menuWait = new CancellationTokenSource(TestTimeouts.Scale(Wait)))
        {
            while (windowA.MenuBar.Count == 0) await Task.Delay(50, menuWait.Token);
        }
        var openItem = windowA.MenuBar.Single(static menu => menu.Title == "Smoke File").Items.OfType<DesktopMenuItem>().Single();
        Check(windowA.MenuBar.Count == 2 && windowA.MenuBar[0].Role == DesktopMenuRole.File && openItem.Title == "Open…" &&
            openItem.Shortcut == new DesktopShortcut("O"), "Guest a's menu bar did not reach its window.");
        await CheckNativeMenusAsync(toolkit, windowA.Presentation, hostWindow);
        Check(await windowA.Presentation.TryExecuteMenuItemAsync(openItem), "The window's menu command path did not run guest a's item.");
        var ran = await logs.ReportAsync("a-menu");
        Check(ran["command"]!.GetValue<string>() == "file.open", $"Guest a ran {ran}.");
        Console.WriteLine($"PASS hosted {toolkit}: guest a's menu bar is in its window, and choosing its item ran the guest's command.");
        if (toolkit == "wkwebview")
        {
            // The hosted window is key again when it closes: the application menu bar returns to the host's menus.
            await windowA.Presentation.FocusAsync();
            var again = await NativeMenus.WaitAsync(windowA.Presentation, toolkit, static titles => titles.Contains("Smoke File"), TestTimeouts.Scale(Wait));
            Check(again.Contains("Smoke File"), $"The application menu bar did not show guest a's menus again: [{string.Join(", ", again)}].");
        }

        // A close through the guest: its Window closes, then the host's window.
        Check(await windowA.RequestCloseAsync(), "Guest a refused the close.");
        await windowA.Closed.WaitAsync(TestTimeouts.Scale(Wait));
        Check(!windowA.Presentation.IsOpen && !a.Exited.IsCompleted, "Guest a's window is open, or guest a exited with it.");
        Console.WriteLine($"PASS hosted {toolkit}: the window closed through the guest.");
        if (toolkit == "wkwebview")
        {
            var closed = await NativeMenus.WaitAsync(hostWindow, toolkit, static titles => IsHostBar(titles), TestTimeouts.Scale(Wait));
            Check(IsHostBar(closed), $"After the key hosted window closed, the application menu bar shows [{string.Join(", ", closed)}], not the host's menus.");
            await CheckNoMergedItemsAsync(hostWindow, "after the key hosted window closed");
            Console.WriteLine($"PASS hosted {toolkit}: the host's menus returned to the application menu bar when the key hosted window closed.");
        }

        if (OperatingSystem.IsWindows())
        {
            // Guest a's page set a persistent cookie and localStorage; an InPrivate profile writes neither. Its window
            // closed, so WebView2's browser process ends and writes what it would write; the session, and so its folder,
            // lasts until guest a stops. The folder holds the files of the profile an InPrivate one derives from.
            Check(foldersA.Length == 1, $"Guest a's session has {foldersA.Length} user data folders.");
            var (scanned, found) = await FindOnDiskAsync(foldersA[0], "runic_guest_a");
            Check(found.Length == 0, $"Guest a's data is on disk, in {string.Join(", ", found)}.");
            Check(scanned > 0, "Guest a's user data folder has no file.");
            Console.WriteLine($"PASS hosted {toolkit}: none of the {scanned} files in guest a's user data folder holds its cookie or storage.");
            await PersistentControlAsync(host, toolkit);
        }

        // A guest without a window stops when asked.
        Check(await a.StopAsync().AsTask().WaitAsync(TestTimeouts.Scale(Wait)) == 0, "Guest a did not exit cleanly after shutdown.");
        Console.WriteLine($"PASS hosted {toolkit}: guest a, without a window, exited when stopped.");

        // A stop: shutdown closes the guest's Windows and it exits.
        var c = await applications.StartAsync(Guest("c", "stay"));
        _ = await logs.ReportAsync("c");
        var windowC = await WindowOf(c);
        // Guest c declares no bar: its window stays as before a guest's first bar.
        await CheckBarBeforeTheGuestsAsync(toolkit, windowC.Presentation);
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

    // The native bars, read from the toolkit (NativeMenus): a hosted window shows the guest's menus and never the host's,
    // the host's window the host's. On macOS the one application menu bar follows the key window: the host's application
    // menu first, then the guest's File menu in the File menu's place, the standard Edit, View, into which the guest's
    // View menu merged, and Window, without the host's other menus; and the host's menus once a host window is key.
    private static async Task CheckNativeMenusAsync(string toolkit, DesktopWindow hosted, DesktopWindow hostWindow)
    {
        string[] guestBar = ["Smoke File", "View"];
        switch (toolkit)
        {
            case "gtk3" or "gtk4":
                var titles = await NativeMenus.WaitAsync(hosted, toolkit, titles => titles.SequenceEqual(guestBar), TestTimeouts.Scale(Wait));
                Check(titles.SequenceEqual(guestBar), $"Guest a's window shows the native bar [{string.Join(", ", titles)}], not [{string.Join(", ", guestBar)}].");
                var host = await NativeMenus.WaitAsync(hostWindow, toolkit, static titles => titles.SequenceEqual(["Host Tools"]), TestTimeouts.Scale(Wait));
                Check(host.SequenceEqual(["Host Tools"]), $"The host's window shows the native bar [{string.Join(", ", host)}], not its own.");
                Console.WriteLine($"PASS hosted {toolkit}: the native menu bar widget of guest a's window shows the guest's menus, the host's window its own.");
                break;
            case "wkwebview":
                string[] merged = ["Smoke File", "Edit", "View", "Window"];
                await hosted.FocusAsync();
                var bar = await NativeMenus.WaitAsync(hosted, toolkit, titles => titles.Skip(1).SequenceEqual(merged), TestTimeouts.Scale(Wait));
                Check(bar.Skip(1).SequenceEqual(merged),
                    $"While guest a's window is key, NSApp.mainMenu is [{string.Join(", ", bar)}], not the application menu then [{string.Join(", ", merged)}].");
                var view = await NativeMenus.MacItemsAsync(hosted, "View");
                Check(view.Contains("Smoke Zoom"), $"The guest's View menu did not join the standard one: [{string.Join(", ", view)}].");
                Console.WriteLine($"PASS hosted {toolkit}: while guest a's window is key, NSApp.mainMenu shows the host's application menu, then the guest's menus.");
                await hostWindow.FocusAsync();
                var back = await NativeMenus.WaitAsync(hostWindow, toolkit, static titles => IsHostBar(titles), TestTimeouts.Scale(Wait));
                Check(IsHostBar(back), $"While the host's window is key, NSApp.mainMenu is [{string.Join(", ", back)}], not the host's menus.");
                await CheckNoMergedItemsAsync(hostWindow, "while the host's window is key");
                Console.WriteLine($"PASS hosted {toolkit}: when the host's window is key, NSApp.mainMenu shows the host's menus again.");
                break;
            default:
                Console.WriteLine($"Hosted {toolkit}: the native menu bar is not read on this platform; the managed model was checked.");
                break;
        }
    }

    // macOS: the guest's items that merged into the standard View menu left it with the guest's bar.
    private static async Task CheckNoMergedItemsAsync(DesktopWindow hostWindow, string when)
    {
        var view = await NativeMenus.MacItemsAsync(hostWindow, "View");
        Check(!view.Contains("Smoke Zoom"), $"The guest's item stayed in the standard View menu {when}: [{string.Join(", ", view)}].");
    }

    // A hosted window before the guest's first bar shows no menus of the host's application, nor of a guest: on GTK no
    // bar; on macOS, while it is key, an application menu bar without the host's or a guest's menus.
    private static async Task CheckBarBeforeTheGuestsAsync(string toolkit, DesktopWindow hosted)
    {
        switch (toolkit)
        {
            case "gtk3" or "gtk4":
                // Nothing will change the bar: a moment shows whether the host's bar arrives anyway.
                await Task.Delay(TimeSpan.FromSeconds(1));
                var titles = await NativeMenus.TitlesAsync(hosted, toolkit);
                Check(titles.Length == 0, $"Before the guest's first bar, its window shows the native bar [{string.Join(", ", titles)}].");
                break;
            case "wkwebview":
                await hosted.FocusAsync();
                var bar = await NativeMenus.WaitAsync(hosted, toolkit, static titles => !titles.Contains("Host Tools"), TestTimeouts.Scale(Wait));
                Check(!bar.Contains("Host Tools") && !bar.Contains("Smoke File"),
                    $"While a hosted window without the guest's bar is key, NSApp.mainMenu is [{string.Join(", ", bar)}].");
                var view = await NativeMenus.MacItemsAsync(hosted, "View");
                Check(!view.Contains("Smoke Zoom"), $"The View menu holds a guest's item before the guest's bar: [{string.Join(", ", view)}].");
                break;
            default:
                return;
        }
        Console.WriteLine($"PASS hosted {toolkit}: a hosted window shows an empty bar until the guest's first bar.");
    }

    // The host's bar on macOS: its declared menu, the standard File menu under its own title, and no menu of a guest.
    private static bool IsHostBar(string[] titles) =>
        titles.Contains("Host Tools") && titles.Contains("File") && !titles.Contains("Smoke File") && titles.Count(static title => title == "View") == 1;

    // The positive control for the scan: the same page script in a window with a persistent profile, in a scratch folder.
    // Once its browser process let go of the folder, the same scan must find the cookie or the storage key, so a hosted
    // session whose data reached the disk would fail the check above.
    private static async Task PersistentControlAsync(RunicDesktopHost host, string toolkit)
    {
        var folder = Directory.CreateTempSubdirectory("runic-hosted-smoke-control-").FullName;
        try
        {
            await using (var surface = await host.Desktop.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page("Control")) }))
            {
                await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
                {
                    Browser = BrowserKind.Embedded,
                    Width = 420,
                    Height = 300,
                    ProfilePath = folder,
                });
                var seen = await surface.ExecuteJavaScriptAsync(StoreScript("control"));
                Check(seen.Contains("runic_guest_control=1", StringComparison.Ordinal), $"The control page could not set its cookie: {seen}.");
                // Gives the profile time to commit as it runs; the browser's exit writes the rest.
                await Task.Delay(TimeSpan.FromSeconds(5));
                await window.CloseAsync();
            }
            var (scanned, found) = await FindOnDiskAsync(folder, "runic_guest_control");
            Check(found.Length > 0, $"The scan found the persistent profile's cookie and storage in none of its {scanned} files.");
            Console.WriteLine($"PASS hosted {toolkit}: the same scan finds a persistent profile's cookie or storage, in {string.Join(", ", found)}.");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"The control profile {folder} could not be deleted: {error.Message}");
            }
        }
    }

    // The files under the folder that contain the text in ASCII or UTF-16, and how many files there are, once WebView2's
    // browser process let go of the folder: every file opens without sharing, which fails while another process has it
    // open, twice in a row a second apart. A file that stays open or unreadable for 60 seconds fails the smoke; none is
    // skipped.
    private static async Task<(int Scanned, string[] Found)> FindOnDiskAsync(string folder, string text)
    {
        var ascii = System.Text.Encoding.ASCII.GetBytes(text);
        var utf16 = System.Text.Encoding.Unicode.GetBytes(text);
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(TimeSpan.FromSeconds(60));
        var clear = 0;
        while (true)
        {
            var scanned = 0;
            var found = new List<string>();
            var held = new List<string>();
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                byte[] content;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                    content = new byte[stream.Length];
                    stream.ReadExactly(content);
                }
                // Deleted since the listing, such as a temporary file of the browser.
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    held.Add($"{Path.GetRelativePath(folder, file)} ({error.Message.Trim()})");
                    continue;
                }
                scanned++;
                if (content.AsSpan().IndexOf(ascii) >= 0 || content.AsSpan().IndexOf(utf16) >= 0) found.Add(Path.GetRelativePath(folder, file));
            }
            clear = held.Count == 0 ? clear + 1 : 0;
            if (clear == 2) return (scanned, [.. found]);
            if (DateTime.UtcNow > deadline)
                throw new InvalidOperationException($"Files in {folder} stayed open or unreadable, so the scan cannot check them: {string.Join("; ", held)}.");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    // The page script of a guest and of the control: a persistent cookie and a localStorage key named after it.
    private static string StoreScript(string name) => $$"""
        document.cookie = "runic_guest_{{name}}=1; path=/; max-age=3600";
        localStorage.setItem("runic_guest_{{name}}", "1");
        return JSON.stringify({
          origin: location.origin,
          cookies: document.cookie,
          bootstrap: typeof globalThis[Symbol.for("runic-desktop.bootstrap")]
        });
        """;

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
                    // menus/1: the application's bar, whose command reports that it ran; guest c ("stay") declares none.
                    if (behaviour != "stay") host.Desktop.SetMenuBar(
                    [
                        new DesktopMenu("Smoke File", [new DesktopMenuItem("Open…", "file.open") { Shortcut = new DesktopShortcut("O") }])
                        {
                            Role = DesktopMenuRole.File,
                        },
                        // On macOS it joins the standard View menu.
                        new DesktopMenu("View", [new DesktopMenuItem("Smoke Zoom", "view.zoom")]),
                    ]);
                    using var commands = host.UseApplicationMenu(new Dictionary<string, System.Windows.Input.ICommand>
                    {
                        ["file.open"] = new ReportCommand(() =>
                        {
                            Console.Out.Write(Report + name + "-menu {\"command\":\"file.open\"}\n");
                            Console.Out.Flush();
                        }),
                    });
                    await using var window = await host.OpenWindowAsync<HostedSmokeWindow>();
                    var surface = RunicDesktopHost.GetSurface(window) ?? throw new InvalidOperationException("The Window has no surface.");
                    var seen = await surface.ExecuteJavaScriptAsync(StoreScript(name));
                    Console.Out.Write(Report + name + " " + seen + "\n");
                    Console.Out.Flush();
                    if (behaviour == "crash")
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1));
                        Environment.Exit(CrashExitCode);
                    }
                    await window.WaitForCloseAsync();
                    if (behaviour == "linger")
                    {
                        // Without a window until the host stops it (shutdown).
                        try { await Task.Delay(Timeout.Infinite, host.Desktop.ShutdownRequested); }
                        catch (OperationCanceledException) { }
                    }
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

internal sealed class ReportCommand(Action execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
