namespace Runic.Desktop.Internal;

// The browser process Runic started exited before its first request, as a browser that restarts itself does, and no
// window connected within the connection deadline (#1097). DesktopSurface reports it as
// presentation-browser-handed-off instead of a plain connection timeout.
internal sealed class BrowserHandOffTimeoutException(string message, Exception innerException)
    : TimeoutException(message, innerException);
