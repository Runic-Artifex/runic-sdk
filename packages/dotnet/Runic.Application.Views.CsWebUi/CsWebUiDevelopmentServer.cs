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
        // Whatever the folder has, and every path that is not plain, stays WebUI's.
        // civetweb hands over the path decoded, with "//" and dot segments removed; the dock's root endpoints go to the
        // server's root, everything else below its base, both through the same checked target rule.
        var target = DevelopmentServerForwarding.IsDockPath(requestPath)
            ? forwarding.RootTarget(requestPath, query: null, rawPath: requestPath)
            : forwarding.DocumentTarget(requestPath, query: null, rawPath: requestPath);
        if (InFolder(root, requestPath) || target is null || !local())
            return WebUiFileHandlerResult.NotHandled;
        return WebUiFileHandlerResult.FromResponse(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"HTTP/1.1 307 Temporary Redirect\r\nLocation: {target.AbsoluteUri}\r\nCache-Control: no-store\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")));
    }

    // Whether the path names a file or folder below the window's root folder, which WebUI serves itself.
    private static bool InFolder(string root, string requestPath)
    {
        string candidate;
        try { candidate = Path.GetFullPath(Path.Join(root, requestPath.TrimStart('/'))); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            return true;
        return File.Exists(candidate) || Directory.Exists(candidate);
    }
}
