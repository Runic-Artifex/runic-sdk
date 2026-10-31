namespace Runic.Desktop.Internal;

// The user closed an embedded window that was still opening, such as a WebView2 window showing its loading surface
// (D053.27). DesktopSurface.OpenWindowAsync then returns the window as closed: it is neither a failure to report nor a
// reason to try a fallback presentation.
internal sealed class WindowClosedWhileOpeningException()
    : InvalidOperationException("The embedded window was closed while it was opening.");
