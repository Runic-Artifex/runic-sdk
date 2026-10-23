using Runic.Desktop;

// SEC-06: DesktopSurfaceOptions.ContentSecurityPolicy reaches the embedded WebView (WebView2, WKWebView, WebKitGTK):
// the page's inline script is blocked while the Bridge connects and runs host scripts. Runic Desktop delivers host
// scripts with the Function constructor, so the policy allows 'unsafe-eval' for 'self' scripts.
internal static class ContentSecurityPolicySmoke
{
    internal const string Policy = "default-src 'self'; script-src 'self' 'unsafe-eval'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";

    internal static string Page() =>
        """
        <!doctype html><html><head><script src="webui.js"></script><title>csp</title>
        <script>document.title = "inline script ran";</script></head><body></body></html>
        """;

    internal static DesktopSurfaceOptions SurfaceOptions() =>
        new() { Content = new DesktopContent.Html(Page()), ContentSecurityPolicy = Policy };

    internal static async Task ExerciseAsync(DesktopSurface surface)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var title = await surface.ExecuteJavaScriptAsync("return document.title;", cancellationToken: deadline.Token);
        if (title != "csp")
        {
            throw new InvalidOperationException($"The surface's Content-Security-Policy did not block the inline script: the title is '{title}'.");
        }

        Console.WriteLine("Content-Security-Policy passed: the inline script was blocked and the Bridge ran host scripts.");
    }
}
