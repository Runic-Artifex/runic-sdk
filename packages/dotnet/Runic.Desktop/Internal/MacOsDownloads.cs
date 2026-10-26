using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// DesktopWebContentPolicy.AllowDownloads in the macOS WKWebView window (G-9): WKDownload, macOS 11.3. The navigation
// delegate answers <a download> and attachment responses with the Download policy only when the window's policy allows
// downloads (MacOsWebContent); WebKit then hands it the WKDownload, which gets the one process-wide download delegate.
// That delegate saves the file in the user's Downloads folder under a name no existing file has, and marks it as
// downloaded from the web (quarantine), as WebView2 marks a download with the mark of the web.
internal sealed partial class MacOsWkWebViewHost
{
    // The folder downloads go to: NSDownloadsDirectory in the user domain, which in the App Sandbox is the container's
    // link to ~/Downloads (com.apple.security.files.downloads.read-write). Tests save to a temporary folder instead.
    internal static Func<string?> DownloadDirectory { get; set; } = UserDownloadsDirectory;

    private static string? UserDownloadsDirectory() => Api.UserDownloadsDirectory();

    // WKDownload.delegate is weak: the object lives for the process, and downloads may outlive their window.
    private static readonly Lazy<nint> DownloadDelegate = new(CreateDownloadDelegate);

    // Each running download's destination and source URL, for the quarantine once it finishes. Main thread only.
    private static readonly ConcurrentDictionary<nint, (string Path, string? Source)> Downloads = new();

    private static unsafe nint CreateDownloadDelegate()
    {
        if (!OperatingSystem.IsMacOS() || !Api.IsAvailable || Api.GetClass("WKDownload") == 0)
        {
            return 0;
        }
        var type = Api.GetClass("RunicDesktopDownloadDelegate");
        if (type == 0)
        {
            type = Api.AllocateClassPair(Api.GetClass("NSObject"), "RunicDesktopDownloadDelegate");
            if (type == 0)
            {
                return 0;
            }
            Api.AddMethod(type, "download:decideDestinationUsingResponse:suggestedFilename:completionHandler:",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&DecideDownloadDestination, "v@:@@@@?");
            Api.AddMethod(type, "downloadDidFinish:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&DownloadDidFinish, "v@:@");
            Api.AddMethod(type, "download:didFailWithError:resumeData:",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DownloadDidFail, "v@:@@@");
            Api.RegisterClassPair(type);
        }
        return Api.SendNint(Api.SendNint(type, "alloc"), "init");
    }

    // webView:navigationAction:didBecomeDownload: and webView:navigationResponse:didBecomeDownload:. The navigation
    // delegate chose the Download policy only for a window whose policy allows downloads; one that does not, or a closed
    // window, cancels the download before it gets a destination.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void NavigationBecameDownload(nint self, nint selector, nint webView, nint navigation, nint download)
    {
        try
        {
            if (download == 0) return;
            var allowed = WebViews.TryGetValue(webView, out var host) && host._webContent is { AllowsDownloads: true };
            var downloadDelegate = allowed ? DownloadDelegate.Value : 0;
            if (downloadDelegate != 0)
            {
                Api.SendVoidNint(download, "setDelegate:", downloadDelegate);
            }
            else
            {
                Api.SendVoidNint(download, "cancel:", 0);
            }
        }
        catch
        {
            // Never unwind through WebKit.
        }
    }

    // The handler is called exactly once, with nil, which cancels the download, when no destination can be chosen.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DecideDownloadDestination(nint self, nint selector, nint download, nint response, nint suggestedFilename, nint completionHandler)
    {
        nint pool = 0;
        nint destination = 0;
        try
        {
            pool = Api.SendNint(Api.SendNint(Api.GetClass("NSAutoreleasePool"), "alloc"), "init");
            if (DownloadDirectory() is { Length: > 0 } directory
                && MacOsWebContent.DownloadPath(directory, suggestedFilename == 0 ? null : Api.Text(suggestedFilename), Exists) is { } path)
            {
                destination = Api.FileUrl(path);
                if (destination != 0)
                {
                    Downloads[download] = (path, DownloadSource(download, response));
                }
            }
        }
        catch
        {
            destination = 0;
        }
        try
        {
            MacOsWebContent.InvokePolicyHandler(completionHandler, destination);
        }
        catch
        {
            // Never unwind through WebKit.
        }
        try
        {
            if (pool != 0) Api.SendVoid(pool, "drain");
        }
        catch
        {
            // Never unwind through WebKit.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DownloadDidFinish(nint self, nint selector, nint download)
    {
        nint pool = 0;
        try
        {
            pool = Api.SendNint(Api.SendNint(Api.GetClass("NSAutoreleasePool"), "alloc"), "init");
            if (Downloads.TryRemove(download, out var saved))
            {
                Api.SetDownloadQuarantine(saved.Path, saved.Source);
            }
        }
        catch
        {
            // The file stays without the quarantine; never unwind through WebKit.
        }
        try
        {
            if (pool != 0) Api.SendVoid(pool, "drain");
        }
        catch
        {
            // Never unwind through WebKit.
        }
    }

    // WebKit removes the partial file of a failed or cancelled download.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DownloadDidFail(nint self, nint selector, nint download, nint error, nint resumeData)
    {
        Downloads.TryRemove(download, out _);
    }

    // A name is taken by any directory entry, also a dangling link.
    private static bool Exists(string path) => Path.Exists(path) || new FileInfo(path).LinkTarget is not null;

    // The URL the file came from: the response's, else the download's original request's.
    private static string? DownloadSource(nint download, nint response)
    {
        var url = response == 0 ? 0 : Api.SendNint(response, "URL");
        if (url == 0)
        {
            var request = Api.SendNint(download, "originalRequest");
            url = request == 0 ? 0 : Api.SendNint(request, "URL");
        }
        var text = url == 0 ? 0 : Api.SendNint(url, "absoluteString");
        return text == 0 ? null : Api.Text(text);
    }
}
