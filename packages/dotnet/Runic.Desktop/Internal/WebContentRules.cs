using Runic.Platform;

namespace Runic.Desktop.Internal;

// Applies a window's DesktopWebContentPolicy (SEC-04): which top-level navigations and new-window requests stay in the
// embedded window, which open with the system's handler, and which are cancelled. One copy for every built-in host;
// Runic.Desktop.Gtk4 compiles this file too.
internal sealed class WebContentRules
{
    internal enum Decision
    {
        // Let the WebView load it.
        Allow,
        // Cancel it.
        Cancel,
        // Cancel it and open ExternalTarget with the system's default handler.
        OpenExternally,
        // A new-window request under AllowInWindow: cancel it and load the URL in this window.
        NavigateInWindow,
    }

    private readonly HashSet<string> _origins;

    internal WebContentRules(DesktopWebContentPolicy policy, Uri presentedUrl, IEnumerable<Uri> additionalOrigins)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(presentedUrl);
        Policy = policy;
        _origins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var origin in additionalOrigins.Prepend(presentedUrl))
        {
            if (Canonical(origin) is { } canonical) _origins.Add(canonical);
        }
        DevToolsEnabled = policy.DevTools ?? false;
    }

    internal DesktopWebContentPolicy Policy { get; }

    internal bool DevToolsEnabled { get; }

    internal bool AllowsDownloads => Policy.AllowDownloads;

    // The policy a host receives: the Development default resolved, so DevTools is never null.
    internal static DesktopWebContentPolicy Resolve(DesktopWebContentPolicy policy, Func<string, string?> readEnvironment) =>
        policy.DevTools is null ? policy with { DevTools = IsDevelopment(readEnvironment) } : policy;

    internal static bool IsDevelopment(Func<string, string?> read)
    {
        var name = read("DOTNET_ENVIRONMENT");
        if (string.IsNullOrEmpty(name)) name = read("ASPNETCORE_ENVIRONMENT");
        return string.Equals(name, "Development", StringComparison.OrdinalIgnoreCase);
    }

    // A navigation of the window's top-level document. Frames are not decided here.
    internal Decision ForNavigation(string? url, bool userInitiated)
    {
        if (Policy.ExternalNavigation == DesktopExternalNavigation.AllowInWindow) return Decision.Allow;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) return Decision.Cancel;
        if (IsApplicationDocument(target)) return Decision.Allow;
        return External(target, userInitiated);
    }

    // target="_blank", window.open and other requests for a new window. No built-in host opens a second WebView window.
    internal Decision ForNewWindow(string? url, bool userInitiated)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || IsApplicationDocument(target)) return Decision.Cancel;
        if (Policy.ExternalNavigation == DesktopExternalNavigation.AllowInWindow && target.Scheme is "http" or "https")
            return Decision.NavigateInWindow;
        return External(target, userInitiated);
    }

    // The URL to hand to the system's handler, or null when Runic.Platform's launcher rules refuse it: only http, https
    // and mailto (without attachments), never ms-settings from page content.
    internal static string? ExternalTarget(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https" or "mailto")) return null;
        try
        {
            return DesktopUriPolicy.Require(target);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private Decision External(Uri target, bool userInitiated) =>
        Policy.ExternalNavigation == DesktopExternalNavigation.Block || !userInitiated || ExternalTarget(target.OriginalString) is null
            ? Decision.Cancel
            : Decision.OpenExternally;

    // A document of an application origin, an empty document, or a blob of an application origin.
    private bool IsApplicationDocument(Uri target)
    {
        if (target.Scheme == "about") return target.AbsolutePath is "blank" or "srcdoc";
        if (target.Scheme == "blob")
            return Uri.TryCreate(target.AbsolutePath, UriKind.Absolute, out var inner) && Canonical(inner) is { } blobOrigin && _origins.Contains(blobOrigin);
        return Canonical(target) is { } origin && _origins.Contains(origin);
    }

    private static string? Canonical(Uri origin)
    {
        if (!origin.IsAbsoluteUri || origin.Scheme is not ("http" or "https")) return null;
        var port = origin.IsDefaultPort ? origin.Scheme == "https" ? 443 : 80 : origin.Port;
        return $"{origin.Scheme}://{origin.IdnHost.ToLowerInvariant()}:{port}";
    }
}
