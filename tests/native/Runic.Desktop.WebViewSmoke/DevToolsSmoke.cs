using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Runic.Desktop;
using WebView2Host = Runic.Desktop.Internal.WindowsWebView2Host;

// sdk#1043 on the Windows runners: DesktopHostOptions.DevToolsExtensions and DesktopWindow.OpenDevToolsAsync. The smoke
// writes an unpacked extension whose content script marks the page. In Production the window has no Open DevTools item
// in its system menu, OpenDevToolsAsync returns false and the extension does not run. In Development the extension runs
// in the first page, the system menu has the item, its WM_SYSCOMMAND and OpenDevToolsAsync open the DevTools window,
// and no devtools-extension-failed diagnostic is reported. Each phase uses a profile folder of its own, because the
// environment option that enables extensions is shared by every window of a user data folder.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static partial class DevToolsSmoke
{
    private const string Marker = "data-runic-devtools-smoke";
    private const uint WmSysCommand = 0x0112;
    private const uint MfByCommand = 0;

    internal static async Task RunAsync(DesktopHostOptions options)
    {
        var root = Directory.CreateTempSubdirectory("runic-devtools-smoke-");
        var previous = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        try
        {
            var extension = WriteExtension(root.CreateSubdirectory("extension").FullName);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
            await CheckAsync(options, extension, Path.Combine(root.FullName, "production"), development: false);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
            await CheckAsync(options, extension, Path.Combine(root.FullName, "development"), development: true);
            Console.WriteLine("DevTools passed: in Production no extension runs and no DevTools window opens; in Development the " +
                "unpacked extension runs in the first page, and the system menu item and OpenDevToolsAsync open the DevTools window.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previous);
            // WebView2's processes may still hold the profile folders for a moment.
            try { root.Delete(recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task CheckAsync(DesktopHostOptions options, string extension, string profile, bool development)
    {
        var phase = development ? "Development" : "Production";
        var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
        await using var host = await DesktopHost.StartAsync(options with
        {
            DevToolsExtensions = [extension],
            DiagnosticSink = diagnostic =>
            {
                diagnostics.Enqueue(diagnostic);
                options.DiagnosticSink?.Invoke(diagnostic);
            },
        });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html("<!doctype html><html><head><title>DevTools smoke</title></head><body><p>DevTools</p></body></html>"),
        });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            Width = 640,
            Height = 480,
            ProfilePath = profile,
        });

        var mark = "none";
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(development ? 15 : 2);
        do
        {
            mark = await surface.ExecuteJavaScriptAsync($"return document.documentElement.getAttribute('{Marker}') ?? 'none';", TimeSpan.FromSeconds(30));
            if (mark == "loaded") break;
            await Task.Delay(250);
        }
        while (DateTime.UtcNow < deadline);
        Check(mark == (development ? "loaded" : "none"), $"{phase}: the extension's content script left {mark}");
        var failures = diagnostics.Where(static diagnostic => diagnostic.Code == WebView2Host.DevToolsExtensionFailedCode).ToArray();
        Check(failures.Length == 0, $"{phase}: {string.Join("; ", failures.Select(static failure => failure.Message))}");

        var systemMenu = GetSystemMenu(window.NativeHandle, false);
        Check(systemMenu != 0, $"{phase}: the window has no system menu");
        var hasItem = GetMenuState(systemMenu, WebView2Host.OpenDevToolsSystemCommand, MfByCommand) != uint.MaxValue;
        Check(hasItem == development, $"{phase}: the system menu {(hasItem ? "has" : "lacks")} Open DevTools");
        if (development)
        {
            // The system menu item runs on the window thread, as when the user chooses it.
            _ = SendMessage(window.NativeHandle, WmSysCommand, WebView2Host.OpenDevToolsSystemCommand, 0);
        }
        var opened = await window.OpenDevToolsAsync();
        Check(opened == development, $"{phase}: OpenDevToolsAsync returned {opened}");
        // The page keeps working with the DevTools window open.
        var alive = await surface.ExecuteJavaScriptAsync("return 'alive';", TimeSpan.FromSeconds(30));
        Check(alive == "alive", $"{phase}: the page did not answer after OpenDevToolsAsync: {alive}");
        Console.WriteLine($"DevTools ({phase}): extension {mark}, system menu item {hasItem}, OpenDevToolsAsync {opened}.");
        await window.CloseAsync();
    }

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

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nuint wParam, nint lParam);
}
