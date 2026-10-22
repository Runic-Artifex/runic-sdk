using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// DesktopWebContentPolicy (SEC-04) in the macOS WKWebView window: maps what WKNavigationDelegate and WKUIDelegate
// report to WebContentRules, and answers WebKit's decision handlers. MacOsWkWebViewHost reads the native values.
internal static class MacOsWebContent
{
    // WKNavigationActionPolicyCancel and WKNavigationResponsePolicyCancel; ...Allow is 1 in both (NSInteger enums).
    internal const nint PolicyCancel = 0;
    internal const nint PolicyAllow = 1;

    // WKNavigationTypeLinkActivated: a link the user clicked or activated with the keyboard.
    internal const nint NavigationTypeLinkActivated = 0;

    // What a callback answers: the policy for its decision handler (unused for a new-window request, which never
    // gets a WebView), and what the window does besides.
    internal readonly record struct Outcome(nint Policy, string? OpenExternally = null, string? NavigateInWindow = null);

    internal static readonly Outcome Cancel = new(PolicyCancel);

    // webView:decidePolicyForNavigationAction:decisionHandler:. mainFrame is null when targetFrame is nil, a request
    // for a new window (target="_blank" or a form with a new target); WebKit asks the UI delegate for a WebView only
    // after this allowed it, so cancelling answers the request here. Frames other than the main frame are not decided.
    internal static Outcome ForNavigationAction(WebContentRules rules, string? url, bool? mainFrame, nint navigationType, bool shouldPerformDownload)
    {
        ArgumentNullException.ThrowIfNull(rules);
        // <a download> (shouldPerformDownload, macOS 11.3): the window has no downloads, nor does it show the file.
        if (shouldPerformDownload) return Cancel;
        var userInitiated = navigationType == NavigationTypeLinkActivated;
        if (mainFrame is null) return ForNewWindow(rules, url, userInitiated);
        if (mainFrame == false) return new(PolicyAllow);
        return rules.ForNavigation(url, userInitiated) switch
        {
            WebContentRules.Decision.Allow => new(PolicyAllow),
            WebContentRules.Decision.OpenExternally => new(PolicyCancel, OpenExternally: WebContentRules.ExternalTarget(url)),
            _ => Cancel,
        };
    }

    // A request for a new window, which never opens one: the handler is cancelled, and the URL may open externally
    // or load in this window.
    internal static Outcome ForNewWindow(WebContentRules rules, string? url, bool userInitiated)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.ForNewWindow(url, userInitiated) switch
        {
            WebContentRules.Decision.OpenExternally => new(PolicyCancel, OpenExternally: WebContentRules.ExternalTarget(url)),
            WebContentRules.Decision.NavigateInWindow => new(PolicyCancel, NavigateInWindow: url),
            _ => Cancel,
        };
    }

    // webView:decidePolicyForNavigationResponse:decisionHandler:. A response WebKit cannot show would be a download,
    // which WKWebView does not perform for the window: it is cancelled in every frame, as WebKit does without a
    // navigation delegate, whatever AllowDownloads says.
    internal static nint ForNavigationResponse(bool canShowMimeType) => canShowMimeType ? PolicyAllow : PolicyCancel;

    // WKPermissionDecisionGrant and WKPermissionDecisionDeny (NSInteger, macOS 12); Prompt (0) is never answered.
    internal const nint PermissionGrant = 1;
    internal const nint PermissionDeny = 2;

    // webView:requestMediaCapturePermissionForOrigin:initiatedByFrame:type:decisionHandler: (SEC-37). As WebView2 and
    // WebKitGTK, the MediaCapture grant covers camera and microphone (WKMediaCaptureType Camera 0, Microphone 1,
    // CameraAndMicrophone 2) for the presented origin only: the requesting origin, the requesting frame's origin and the
    // top-level document must all have it. Without the grant the request is denied, as on the other hosts; macOS still
    // asks the user for the application's own camera and microphone access.
    internal static nint ForMediaCapture(bool granted, Uri presented, string? requestingOrigin, string? frameOrigin, string? topLevelUrl, nint type) =>
        granted && type is 0 or 1 or 2 &&
        PresentationSecurityPolicy.IsSameOrigin(presented, requestingOrigin) &&
        PresentationSecurityPolicy.IsSameOrigin(presented, frameOrigin) &&
        PresentationSecurityPolicy.IsSameOrigin(presented, topLevelUrl)
            ? PermissionGrant
            : PermissionDeny;

    // A WKSecurityOrigin's protocol, host and port (0 for the scheme's default) as an origin URL, or null.
    internal static string? OriginUrl(string? protocol, string? host, long port)
    {
        if (string.IsNullOrEmpty(protocol) || string.IsNullOrEmpty(host)) return null;
        var authority = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        return port is > 0 and <= ushort.MaxValue
            ? $"{protocol}://{authority}:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : $"{protocol}://{authority}";
    }

    // Calls a decision handler, an Objective-C block void (^)(NSInteger policy). A block literal starts with isa,
    // int flags, int reserved and its invoke function, which takes the block and then the block's arguments.
    internal static unsafe void InvokePolicyHandler(nint block, nint policy)
    {
        if (block == 0) return;
        ((delegate* unmanaged[Cdecl]<nint, nint, void>)((BlockLiteral*)block)->Invoke)(block, policy);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public nint Descriptor;
    }
}
