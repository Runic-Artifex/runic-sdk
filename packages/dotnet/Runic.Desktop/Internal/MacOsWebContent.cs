using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// DesktopWebContentPolicy (SEC-04) in the macOS WKWebView window: maps what WKNavigationDelegate and WKUIDelegate
// report to WebContentRules, and answers WebKit's decision handlers. MacOsWkWebViewHost reads the native values.
internal static class MacOsWebContent
{
    // WKNavigationActionPolicyCancel and WKNavigationResponsePolicyCancel; ...Allow is 1 and ...Download (macOS 11.3)
    // is 2 in both (NSInteger enums).
    internal const nint PolicyCancel = 0;
    internal const nint PolicyAllow = 1;
    internal const nint PolicyDownload = 2;

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
        // <a download> (shouldPerformDownload, macOS 11.3) becomes a WKDownload when the policy allows downloads, in any
        // frame; otherwise the window neither downloads nor shows the file.
        if (shouldPerformDownload) return rules.AllowsDownloads ? new(PolicyDownload) : Cancel;
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

    // webView:decidePolicyForNavigationResponse:decisionHandler:. A Content-Disposition attachment and a response WebKit
    // cannot show are downloads, in every frame: they become a WKDownload when the policy allows downloads and are
    // cancelled otherwise, as WebKit does without a navigation delegate. Everything else loads.
    internal static nint ForNavigationResponse(WebContentRules rules, bool canShowMimeType, string? contentDisposition)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (canShowMimeType && !IsAttachment(contentDisposition)) return PolicyAllow;
        return rules.AllowsDownloads ? PolicyDownload : PolicyCancel;
    }

    // RFC 6266: the disposition type is the first token, case-insensitive.
    internal static bool IsAttachment(string? contentDisposition)
    {
        if (string.IsNullOrEmpty(contentDisposition)) return false;
        var end = contentDisposition.IndexOf(';', StringComparison.Ordinal);
        var type = (end < 0 ? contentDisposition : contentDisposition[..end]).Trim();
        return type.Equals("attachment", StringComparison.OrdinalIgnoreCase);
    }

    // The most names DownloadPath tries before it gives up on a crowded directory.
    internal const int MaximumDownloadNames = 1000;

    // download:decideDestinationUsingResponse:suggestedFilename:completionHandler:. The file WebKit's suggested name gets
    // in the directory, never an existing one: "name (1).ext", "name (2).ext" and so on, as Safari names them. Null when
    // every name is taken; WebKit also fails a download whose destination exists rather than replace it. The name loses
    // path separators, control characters and leading dots, and is at most 255 UTF-8 bytes.
    internal static string? DownloadPath(string directory, string? suggestedFilename, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(exists);
        var name = SafeFileName(suggestedFilename);
        var extension = Path.GetExtension(name);
        if (extension.Length > 32 || extension == name) extension = "";
        var stem = name[..^extension.Length];
        for (var attempt = 0; attempt < MaximumDownloadNames; attempt++)
        {
            var suffix = attempt == 0 ? extension : $" ({attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)}){extension}";
            var candidate = Path.Combine(directory, Truncate(stem, MaximumFileNameBytes - System.Text.Encoding.UTF8.GetByteCount(suffix)) + suffix);
            if (!exists(candidate)) return candidate;
        }
        return null;
    }

    private const int MaximumFileNameBytes = 255;
    private const string DefaultDownloadName = "download";

    private static string SafeFileName(string? suggested)
    {
        if (string.IsNullOrWhiteSpace(suggested)) return DefaultDownloadName;
        var builder = new System.Text.StringBuilder(suggested.Length);
        foreach (var character in suggested)
            builder.Append(character is '/' or '\\' or ':' || char.IsControl(character) ? '_' : character);
        var name = builder.ToString().Trim().TrimStart('.').Trim();
        return name.Length == 0 ? DefaultDownloadName : name;
    }

    // The longest prefix of value within the UTF-8 byte budget that does not split a surrogate pair.
    private static string Truncate(string value, int bytes)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(value) <= bytes) return value;
        var length = value.Length;
        while (length > 0 && System.Text.Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) > bytes) length--;
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        var truncated = value[..length].TrimEnd();
        return truncated.Length == 0 ? DefaultDownloadName : truncated;
    }

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
