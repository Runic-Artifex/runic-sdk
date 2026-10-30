using System.Runtime.Versioning;
using Runic.Application.Views.Desktop;
using Runic.Desktop;

namespace Runic.Sandbox.Probe;

/// <summary>What a probe mode gets: the open window, its page over the loopback bridge and its platform services.</summary>
internal sealed record ProbeContext(DesktopSurface Surface, DesktopWindow Window, DesktopWindowPlatformServices Services, string Container);

/// <summary>
/// Opens one embedded window in the App Sandbox and runs a probe mode in it. Before the mode it checks that the process
/// is sandboxed and that the page came from Runic's loopback server and answers over the bridge, which needs the
/// network.server and network.client entitlements in the sandbox.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class ProbeWindow
{
    internal const string Title = "Runic Sandbox Probe";

    // DesktopContent.Html connects the Bridge only through the page's own webui.js script.
    private const string Page = $"<!doctype html><html><head><meta charset=\"utf-8\"><script src=\"webui.js\"></script><title>{Title}</title></head>"
        + "<body><p id=\"state\">The sandboxed window is open.</p></body></html>";

    internal static int Run(Func<ProbeContext, Task> probe) =>
        DesktopEventLoop.Run(new DesktopHostOptions
        {
            ConnectionTimeout = TimeSpan.FromSeconds(45),
            DiagnosticSink = diagnostic => Console.Error.WriteLine($"Desktop diagnostic: {diagnostic.Category}/{diagnostic.Code}: {diagnostic.Message}"),
        }, async host =>
        {
            try
            {
                string container = Environment.GetEnvironmentVariable("APP_SANDBOX_CONTAINER_ID") ?? "";
                Check(container.Length != 0, "the process runs in the App Sandbox (APP_SANDBOX_CONTAINER_ID is set)");
                // The scripts the served page references, so a run whose Bridge never connects shows what WebKit had to load.
                Console.WriteLine("PAGE scripts: " + string.Join(", ", ScriptSources(Page)));
                await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
                Console.WriteLine($"PAGE served at {surface.Url}");
                // Opening waits until the page connected its bridge.
                var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 640, Height = 480 });
                // The AppKit loop runs on this thread while the probe works on the thread pool, as in the WebView smoke.
                var work = Task.Run(async () =>
                {
                    try
                    {
                        Check(window.NativeHandle != 0 && window.Browser == BrowserKind.Embedded && !window.IsFallback,
                            $"an embedded WKWebView window opened (browser {window.Browser}, fallback {window.IsFallback})");
                        Check(await ScriptAsync(surface, "return document.title;") == Title, "the page answers over the bridge");
                        Console.WriteLine("PAGE loaded scripts: " + await ScriptAsync(surface,
                            "return Array.from(document.scripts, script => script.src || 'inline').join(', ');"));
                        string origin = await ScriptAsync(surface, "return location.origin;");
                        Check(origin == surface.Url.GetLeftPart(UriPartial.Authority) && surface.Url.IsLoopback,
                            $"the page comes from the loopback server: {origin}, surface {surface.Url.GetLeftPart(UriPartial.Authority)}");
                        Console.WriteLine($"PASS sandboxed window: WKWebView loaded {origin} and answered over the bridge (container {container}).");
                        await using var services = DesktopPlatformServices.ForWindow(new DesktopNativeOwner(window));
                        await probe(new ProbeContext(surface, window, services, container));
                    }
                    finally { await window.CloseAsync(); }
                });
                await window.WaitForCloseAsync();
                await work;
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL " + error);
                return 1;
            }
        });

    private static IEnumerable<string> ScriptSources(string html) =>
        System.Text.RegularExpressions.Regex.Matches(html, "<script[^>]*\\bsrc=\"([^\"]*)\"").Select(static match => match.Groups[1].Value);

    internal static async Task<string> ScriptAsync(DesktopSurface surface, string script)
    {
        using var deadline = TestTimeouts.Cancellation(TimeSpan.FromSeconds(20));
        return await surface.ExecuteJavaScriptAsync(script, deadline.Token);
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Expected " + message + ".");
    }
}
