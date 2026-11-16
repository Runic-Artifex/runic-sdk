namespace Runic.Desktop.Internal;

// D067.15, as #1028 for WebKitGTK: WebView2 adds WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS to its browser's command line in any
// process, so a release started with --remote-debugging-port in it opens a DevTools Protocol endpoint, which can script
// the page and its Bridge, whatever DesktopWebContentPolicy.DevTools says. Outside Development the WebView2 host removes
// the variable before it creates its WebView2 environment; the whole variable, since other switches it can carry
// (--remote-debugging-pipe, --remote-allow-origins, --load-extension) are debugging or extension switches too. The loader
// reads the process environment, which Environment.SetEnvironmentVariable changes on Windows, and processes the
// application starts later no longer inherit it. In Development it stays: dotnet runic dev and the template's
// "Window with WebView2 debugging (Windows)" open the endpoint through it.
internal static class WebView2BrowserArgumentsEnvironment
{
    internal const string Variable = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";

    private static int _applied;

    // Before each WebView2 environment the host creates; acts once per process, before the first.
    internal static void Apply()
    {
        if (Interlocked.Exchange(ref _applied, 1) != 0) return;
        Apply(Environment.GetEnvironmentVariable, static name => Environment.SetEnvironmentVariable(name, null));
    }

    // The environment test of DevTools (WebContentRules.IsDevelopment): outside Development the variable is cleared.
    internal static void Apply(Func<string, string?> read, Action<string> clear)
    {
        if (WebContentRules.IsDevelopment(read) || string.IsNullOrEmpty(read(Variable))) return;
        clear(Variable);
    }
}
