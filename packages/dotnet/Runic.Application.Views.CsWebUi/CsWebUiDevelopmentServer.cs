using System.Globalization;
using System.Text;
using CsWebUi;
using Runic.Application.Views.Desktop;

namespace Runic.Application.Views.CsWebUi;

// The CS-WebUI side of DevelopmentServerForwarding (#1085, #1086): under dotnet runic dev with a development server, a
// window serves its www/ folder, and what that folder does not have is redirected to the server. WebUI serves the
// document and the listener's root from one path, so a page-relative file and a root-relative one such as the Vite
// DevTools dock's /__devtools/__connection.json cannot be told apart; a redirect suits both, since the dock then opens its
// WebSocket on the server, and needs no request to the server from WebUI's thread. A page's fetch() of such a file is a
// cross-origin request that Vite's CORS admits for loopback origins (its default).
// WebUI hands its file handler only the path, so the request checks that need headers are WebUI's own: its single-client
// authentication cookie (SameSite=Strict) answers 403 to a request from another site or from a DNS-rebinding page,
// before the handler runs. The handler redirects only while the window's server listens on loopback alone.
internal static class CsWebUiDevelopmentServer
{
    internal static void Serve(WebUiWindow window, string root, DevelopmentServerForwarding forwarding)
    {
        window.SetRootFolder(root);
        var folder = Path.GetFullPath(root);
        window.SetFileHandler(path => Respond(folder, forwarding, path,
            () => CsWebUiWindowLocality.ListensOnLoopbackOnly((int)window.Port)));
        // As in CsWebUiAssets.Serve: the handler serves the window's own origin, so WebUI's authentication cookie, which
        // installing a file handler turns off, stays on.
        WebUiApplication.SetConfiguration(WebUiConfiguration.UseCookies, true);
    }

    internal static WebUiFileHandlerResult Respond(string root, DevelopmentServerForwarding forwarding, string path,
        Func<bool> local)
    {
        var requestPath = path.StartsWith('/') ? path : "/" + path;
        // What the folder has stays WebUI's, as without the handler: WebUI serves it and sets its authentication cookie.
        // For a path the handler leaves, WebUI probes "<path>/index.html" through the handler again; that probe, below a
        // file of the folder, stays WebUI's too, so WebUI then serves the file instead of following a redirect.
        // Refused paths also stay WebUI's, which answers 404.
        if (InFolder(root, requestPath)) return WebUiFileHandlerResult.NotHandled;
        // civetweb hands over the path decoded, with "//" and dot segments removed; the dock's root endpoints go to the
        // server's root, everything else below its base, both through the same checked target rule.
        var target = DevelopmentServerForwarding.IsDockPath(requestPath)
            ? forwarding.RootTarget(requestPath, query: null, rawPath: requestPath)
            : forwarding.DocumentTarget(requestPath, query: null, rawPath: requestPath);
        if (target is null || !local()) return WebUiFileHandlerResult.NotHandled;
        if (string.Equals(requestPath, DevelopmentServerForwarding.DockScriptPath, StringComparison.Ordinal))
        {
            // As on Runic Desktop: one module instance of the dock's script, under the server's URL.
            var module = Encoding.UTF8.GetBytes(forwarding.DockScriptModule());
            var head = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $"HTTP/1.1 200 OK\r\nContent-Type: text/javascript; charset=utf-8\r\nContent-Length: {module.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n"));
            return WebUiFileHandlerResult.FromResponse((byte[])[.. head, .. module]);
        }
        return WebUiFileHandlerResult.FromResponse(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"HTTP/1.1 307 Temporary Redirect\r\nLocation: {target.AbsoluteUri}\r\nCache-Control: no-store\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")));
    }

    // Whether the path, or a path it lies below, names a file or folder below the window's root folder (or lies outside
    // it); WebUI serves those.
    private static bool InFolder(string root, string requestPath)
    {
        string candidate;
        try { candidate = Path.GetFullPath(Path.Join(root, requestPath.TrimStart('/'))); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            return true;
        if (Directory.Exists(candidate)) return true;
        // The path itself, or one of its ancestors below the root, is a file.
        for (var current = candidate; current.Length > root.Length; current = Path.GetDirectoryName(current) ?? root)
        {
            if (File.Exists(current)) return true;
        }
        return false;
    }
}
