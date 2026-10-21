namespace Runic.Platform;

// The URIs IDesktopFileLauncher.LaunchUriAsync hands to the desktop: absolute http, https, mailto and ms-settings URIs
// only. A relative string or a path (an absolute path parses as a file: URI, a UNC path too) is rejected before
// anything native runs, so a launch never executes a program. ms-settings: (the Windows Settings app, D028.5, D031.6)
// must be exactly ms-settings:<page>, the page 1 to 64 letters, digits or hyphens and not an -action page; every
// platform validates it, only Windows dispatches it, and the other providers report it unavailable without a native
// call (IsWindowsOnly). One source for every implementation: Runic.Platform.Runtime and Runic.Platform.Linux.Portal
// compile this file too, and the Windows and macOS providers use Runtime's copy.
internal static class DesktopUriPolicy
{
    internal const string RejectedMessage =
        "Only absolute http, https, mailto and ms-settings URIs can be launched. Open local files with LaunchAsync.";
    internal const string SettingsRejectedMessage =
        "An ms-settings URI must be ms-settings:<page>, where the page is 1 to 64 letters, digits or hyphens, with no " +
        "path, query, fragment or escapes, and does not end in -action.";

    private const string SettingsScheme = "ms-settings";
    private const int MaximumSettingsPage = 64;

    // Returns the string to hand to the desktop.
    internal static string Require(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        // Uri lowercases the scheme; IsAbsoluteUri first, because Scheme throws for a relative URI.
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "mailto" or SettingsScheme))
            throw new ArgumentException(RejectedMessage, nameof(uri));
        return uri.Scheme == SettingsScheme ? SettingsTarget(uri) : uri.AbsoluteUri;
    }

    // An accepted URI that only Windows can open. Call after Require.
    internal static bool IsWindowsOnly(Uri uri) => uri.Scheme == SettingsScheme;

    // The original text, not the parsed parts: Uri unescapes, escapes and normalizes, and every character outside the
    // page alphabet (/, \, ?, #, %, whitespace) is refused rather than interpreted.
    private static string SettingsTarget(Uri uri)
    {
        string text = uri.OriginalString;
        int colon = text.IndexOf(':');
        if (colon != SettingsScheme.Length || !text.AsSpan(0, colon).Equals(SettingsScheme, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(SettingsRejectedMessage, nameof(uri));
        string page = text[(colon + 1)..];
        // ASCII only before lowering: ToLowerInvariant maps some non-ASCII letters (the Kelvin sign) to ASCII ones.
        foreach (char character in page)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-'))
                throw new ArgumentException(SettingsRejectedMessage, nameof(uri));
        }
        page = page.ToLowerInvariant();
        if (page.Length is 0 or > MaximumSettingsPage || page.EndsWith("-action", StringComparison.Ordinal))
            throw new ArgumentException(SettingsRejectedMessage, nameof(uri));
        return SettingsScheme + ":" + page;
    }
}
