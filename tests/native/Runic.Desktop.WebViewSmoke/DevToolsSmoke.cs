using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Runic.Desktop;
using WebView2Host = Runic.Desktop.Internal.WindowsWebView2Host;

// sdk#1043 on the Windows runners: DesktopHostOptions.DevToolsExtensions and DesktopWindow.OpenDevToolsAsync. The smoke
// writes an unpacked extension whose content script marks the page. In Production the window has no Open DevTools item
// in its system menu, OpenDevToolsAsync returns false and the extension does not run. In Development the extension runs
// in the first page, the system menu has the item, its WM_SYSCOMMAND and OpenDevToolsAsync open the DevTools window,
// and no devtools-extension-failed diagnostic is reported; a second window of the same profile then opens too.
//
// Each phase runs in a process of its own (--devtools-phase), started with its DOTNET_ENVIRONMENT as an application is,
// and with a user data folder of its own. Every step has a deadline and logs its start and end, so a hang names its step.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static partial class DevToolsSmoke
{
    private const string Marker = "data-runic-devtools-smoke";
    private const uint WmSysCommand = 0x0112;
    private const uint MfByCommand = 0;
    private static readonly TimeSpan PhaseDeadline = TimeSpan.FromSeconds(75);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    // --devtools, and the end of --menus: both phases, each in a child process.
    internal static async Task RunAsync()
    {
        foreach (var phase in (string[])["Production", "Development"])
        {
            // The child has its own deadline below the smoke's watchdog.
            SmokeSoak.Watchdog?.Change(TimeSpan.FromSeconds(180), Timeout.InfiniteTimeSpan);
            await RunChildAsync(phase);
        }
        Console.WriteLine("DevTools passed: in Production no extension runs and no DevTools window opens; in Development the " +
            "unpacked extension runs in the first page, a second window of the profile opens, and the system menu item and " +
            "OpenDevToolsAsync open the DevTools window.");
    }

    private static async Task RunChildAsync(string phase)
    {
        var entry = Environment.GetCommandLineArgs()[0];
        var start = new ProcessStartInfo { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = Environment.ProcessPath ?? "dotnet";
            start.ArgumentList.Add(entry);
        }
        else
        {
            start.FileName = Environment.ProcessPath ?? entry;
        }
        start.ArgumentList.Add("--devtools-phase");
        start.ArgumentList.Add(phase);
        start.Environment["DOTNET_ENVIRONMENT"] = phase;
        start.Environment.Remove("ASPNETCORE_ENVIRONMENT");
        start.Environment.Remove(Runic.Desktop.Internal.DevToolsExtensionFolders.Variable);
        Log(phase, "starting its process");
        using var child = Process.Start(start) ?? throw new InvalidOperationException($"DevTools smoke: the {phase} process did not start.");
        child.OutputDataReceived += (_, line) => { if (line.Data is not null) Console.WriteLine("  " + line.Data); };
        child.ErrorDataReceived += (_, line) => { if (line.Data is not null) Console.Error.WriteLine("  " + line.Data); };
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();
        using var deadline = new CancellationTokenSource(PhaseDeadline);
        try
        {
            await child.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try { child.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw new TimeoutException($"DevTools smoke: the {phase} process exceeded {PhaseDeadline.TotalSeconds:0} s; its last step is logged above.");
        }
        child.WaitForExit(); // The redirected output is complete.
        if (child.ExitCode != 0) throw new InvalidOperationException($"DevTools smoke: the {phase} process failed with exit code {child.ExitCode}.");
        Log(phase, "passed");
    }

    // --devtools-phase Production|Development, in the process that RunAsync started.
    internal static async Task RunPhaseAsync(DesktopHostOptions options, string phase)
    {
        var development = string.Equals(phase, "Development", StringComparison.Ordinal);
        Check(development == string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development", StringComparison.Ordinal),
            $"{phase}: DOTNET_ENVIRONMENT is {Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")}");
        var root = Directory.CreateTempSubdirectory("runic-devtools-smoke-");
        try
        {
            var extension = WriteExtension(root.CreateSubdirectory("extension").FullName);
            await CheckAsync(options, phase, extension, Path.Combine(root.FullName, "profile"), development);
        }
        finally
        {
            // WebView2's processes may still hold the profile folder for a moment.
            try { root.Delete(recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task CheckAsync(DesktopHostOptions options, string phase, string extension, string profile, bool development)
    {
        var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
        var host = await StepAsync(phase, "start the host", TimeSpan.FromSeconds(30), () => DesktopHost.StartAsync(options with
        {
            DevToolsExtensions = [extension],
            ConnectionTimeout = TimeSpan.FromSeconds(40),
            DiagnosticSink = diagnostic =>
            {
                diagnostics.Enqueue(diagnostic);
                options.DiagnosticSink?.Invoke(diagnostic);
            },
        }).AsTask());
        await using var hostScope = host;
        var surface = await StepAsync(phase, "create the surface", TimeSpan.FromSeconds(30), () => host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html("<!doctype html><html><head><meta charset=\"utf-8\"><script src=\"webui.js\"></script><title>DevTools smoke</title></head><body><p>DevTools</p></body></html>"),
        }).AsTask());
        await using var surfaceScope = surface;
        var window = await StepAsync(phase, "open the window", TimeSpan.FromSeconds(50), () => surface.OpenWindowAsync(Window(profile)).AsTask());

        var mark = "none";
        var markDeadline = Clock.Elapsed + TimeSpan.FromSeconds(development ? 15 : 2);
        await StepAsync(phase, "read the extension's mark", TimeSpan.FromSeconds(30), async () =>
        {
            do
            {
                mark = await surface.ExecuteJavaScriptAsync($"return document.documentElement.getAttribute('{Marker}') ?? 'none';", TimeSpan.FromSeconds(10));
                if (mark == "loaded") break;
                await Task.Delay(250);
            }
            while (Clock.Elapsed < markDeadline);
            return mark;
        });
        Check(mark == (development ? "loaded" : "none"), $"{phase}: the extension's content script left {mark}");
        var failures = diagnostics.Where(static diagnostic => diagnostic.Code == WebView2Host.DevToolsExtensionFailedCode).ToArray();
        Check(failures.Length == 0, $"{phase}: {string.Join("; ", failures.Select(static failure => failure.Message))}");

        var hasItem = false;
        await StepAsync(phase, "read the system menu", TimeSpan.FromSeconds(10), () => window.InvokeNativeAsync(handle =>
        {
            var systemMenu = GetSystemMenu(handle, false);
            hasItem = systemMenu != 0 && GetMenuState(systemMenu, WebView2Host.OpenDevToolsSystemCommand, MfByCommand) != uint.MaxValue;
        }).AsTask());
        Check(hasItem == development, $"{phase}: the system menu {(hasItem ? "has" : "lacks")} Open DevTools");
        if (development)
        {
            // Posted, as when the user chooses the item; the window thread runs it.
            await StepAsync(phase, "choose Open DevTools in the system menu", TimeSpan.FromSeconds(10), () =>
                window.InvokeNativeAsync(handle => _ = PostMessage(handle, WmSysCommand, WebView2Host.OpenDevToolsSystemCommand, 0)).AsTask());
        }
        var opened = await StepAsync(phase, "OpenDevToolsAsync", TimeSpan.FromSeconds(15), () => window.OpenDevToolsAsync().AsTask());
        Check(opened == development, $"{phase}: OpenDevToolsAsync returned {opened}");
        var alive = await StepAsync(phase, "run a script after OpenDevToolsAsync", TimeSpan.FromSeconds(20),
            () => surface.ExecuteJavaScriptAsync("return 'alive';", TimeSpan.FromSeconds(15)));
        Check(alive == "alive", $"{phase}: the page did not answer after OpenDevToolsAsync: {alive}");

        if (development)
        {
            // A later window of the same user data folder: the environment's options match, and the extension, already
            // installed in this process, is not installed again.
            await using var second = await StepAsync(phase, "create a second surface", TimeSpan.FromSeconds(30), () => host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("<!doctype html><html><head><meta charset=\"utf-8\"><script src=\"webui.js\"></script><title>DevTools smoke 2</title></head><body><p>Second</p></body></html>"),
            }).AsTask());
            var secondWindow = await StepAsync(phase, "open a second window of the profile", TimeSpan.FromSeconds(50),
                () => second.OpenWindowAsync(Window(profile)).AsTask());
            var secondMark = await StepAsync(phase, "read the second window's mark", TimeSpan.FromSeconds(20), () =>
                second.ExecuteJavaScriptAsync($"return document.documentElement.getAttribute('{Marker}') ?? 'none';", TimeSpan.FromSeconds(15)));
            Check(secondMark == "loaded", $"{phase}: the second window's page has {secondMark}");
            await StepAsync(phase, "close the second window", TimeSpan.FromSeconds(20), () => secondWindow.CloseAsync().AsTask());
        }
        Log(phase, $"extension {mark}, system menu item {hasItem}, OpenDevToolsAsync {opened}");
        await StepAsync(phase, "close the window", TimeSpan.FromSeconds(20), () => window.CloseAsync().AsTask());
    }

    private static DesktopWindowOptions Window(string profile) => new()
    {
        Browser = BrowserKind.Embedded,
        Width = 640,
        Height = 480,
        ProfilePath = profile,
    };

    private static async Task StepAsync(string phase, string step, TimeSpan deadline, Func<Task> run) =>
        await StepAsync(phase, step, deadline, async () => { await run(); return true; });

    private static async Task<T> StepAsync<T>(string phase, string step, TimeSpan deadline, Func<Task<T>> run)
    {
        var started = Clock.Elapsed;
        Log(phase, step + "…");
        try
        {
            var result = await run().WaitAsync(deadline);
            Log(phase, $"{step}: done in {(Clock.Elapsed - started).TotalMilliseconds:0} ms");
            return result;
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"DevTools smoke ({phase}): '{step}' did not finish within {deadline.TotalSeconds:0} s.");
        }
    }

    private static void Log(string phase, string message) =>
        Console.WriteLine($"DevTools ({phase}) [{Clock.Elapsed.TotalSeconds:0.0} s]: {message}");

    // A Manifest V3 extension whose content script marks every page at document start.
    private static string WriteExtension(string folder)
    {
        File.WriteAllText(Path.Combine(folder, "manifest.json"), """
            {
              "manifest_version": 3,
              "name": "Runic DevTools smoke",
              "version": "1.0",
              "content_scripts": [{ "matches": ["<all_urls>"], "js": ["content.js"], "run_at": "document_start" }]
            }
            """);
        File.WriteAllText(Path.Combine(folder, "content.js"), $"document.documentElement.setAttribute('{Marker}', 'loaded');\n");
        return folder;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("DevTools smoke: " + message);
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetSystemMenu(nint window, [MarshalAs(UnmanagedType.Bool)] bool revert);

    [LibraryImport("user32.dll")]
    private static partial uint GetMenuState(nint menu, uint item, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}
