namespace Runic.Platform;

// The URIs IDesktopFileLauncher.LaunchUriAsync hands to the desktop: absolute http, https and mailto URIs only. A
// relative string or a path (an absolute path parses as a file: URI, a UNC path too) is rejected before anything
// native runs, so a launch never executes a program. One source for every implementation: Runic.Platform.Runtime and
// Runic.Platform.Linux.Portal compile this file too, and the Windows and macOS providers use Runtime's copy.
internal static class DesktopUriPolicy
{
    internal const string RejectedMessage =
        "Only absolute http, https and mailto URIs can be launched. Open local files with LaunchAsync.";

    // Returns the string to hand to the desktop.
    internal static string Require(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        // Uri lowercases the scheme; IsAbsoluteUri first, because Scheme throws for a relative URI.
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "mailto"))
            throw new ArgumentException(RejectedMessage, nameof(uri));
        return uri.AbsoluteUri;
    }
}
