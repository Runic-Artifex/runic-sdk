namespace Runic.Desktop.Internal;

// The WebKitGTK side of a window's DesktopWebContentPolicy (SEC-04), shared by the GTK 3 and GTK 4 hosts; Runic.Desktop.Gtk4
// compiles this file too. GTK thread only.
//
// WebKitGTK asks decide-policy about the navigation of every frame and does not say which frame navigates: the
// navigation action of an iframe looks like the main frame's. Only the response decision tells the main frame's document
// apart (webkit_response_policy_decision_is_main_frame_main_resource, WebKitGTK 2.40). So a navigation that WebKit loads
// and answers with a response (http, https, data, blob) and that the rules would not allow continues, with its gesture
// remembered, and is decided when its response arrives: an iframe keeps loading, the main frame's navigation is
// cancelled before its document commits. Its request has reached the server by then, as an image or fetch from the page
// could. When no response arrives (an unreachable server), the failed load is decided instead and WebKit's error page
// is suppressed. Other schemes (mailto, file, custom handlers) are decided at once, for any frame. Without the response
// API every navigation is decided at once, iframes included.
internal sealed class WebKitGtkWebContent(WebContentRules rules, bool canIdentifyMainFrame)
{
    // Navigations waiting for their response; bounded, because an iframe's response is not looked up.
    private const int MaximumPending = 64;
    private readonly Dictionary<string, bool> _pending = new(StringComparer.Ordinal);

    internal WebContentRules Rules { get; } = rules;

    // WEBKIT_POLICY_DECISION_TYPE_NAVIGATION_ACTION. Allow lets WebKit continue.
    internal WebContentRules.Decision ForNavigationAction(string? url, bool userGesture)
    {
        var decision = Rules.ForNavigation(url, userGesture);
        if (decision == WebContentRules.Decision.Allow || !canIdentifyMainFrame || !IsAnsweredByResponse(url)) return decision;
        if (_pending.Count >= MaximumPending) _pending.Clear();
        // A redirect asks again with its target; its gesture is the original navigation's.
        _pending[url!] = userGesture || (_pending.TryGetValue(url!, out var earlier) && earlier);
        return WebContentRules.Decision.Allow;
    }

    // WEBKIT_POLICY_DECISION_TYPE_RESPONSE of the main frame's document.
    internal WebContentRules.Decision ForMainFrameResponse(string? url) => Rules.ForNavigation(url, Take(url));

    // A failed main-frame load: whether to suppress WebKit's error page, and what to do with a navigation that was waiting
    // for its response. A navigation the rules allow keeps WebKit's error page.
    internal WebContentRules.Decision ForFailedMainFrameLoad(string? url, out bool suppressErrorPage)
    {
        var waiting = url is not null && _pending.ContainsKey(url);
        var decision = Rules.ForNavigation(url, Take(url));
        suppressErrorPage = decision != WebContentRules.Decision.Allow;
        // A navigation decided by its response was handled then; only one still waiting opens now.
        return waiting ? decision : WebContentRules.Decision.Cancel;
    }

    // A response WebKit would download rather than show.
    internal bool CancelsResponse(bool mimeTypeSupported) => !Rules.AllowsDownloads && !mimeTypeSupported;

    internal void Clear() => _pending.Clear();

    private bool Take(string? url) => url is not null && _pending.Remove(url, out var userGesture) && userGesture;

    private static bool IsAnsweredByResponse(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var target) && target.Scheme is "http" or "https" or "data" or "blob";
}
