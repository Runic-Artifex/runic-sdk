using System.Diagnostics;
using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;

namespace Runic.Application.Views.CsWebUi;

// Event IDs are documented in the Views README (Telemetry).
internal static partial class CsWebUiLog
{
    internal const string LogCategory = "Runic.Application.Views.CsWebUi";

    [LoggerMessage(EventId = 2100, EventName = "CsWebUiWindowShowFailed", Level = LogLevel.Error,
        Message = "CS-WebUI could not show {Window} ({Code}, browser {Browser} at {BrowserPath}): {DiagnosticMessage}")]
    private static partial void WindowShowFailed(ILogger logger, Exception exception, string window, string code,
        string browser, string browserPath, string diagnosticMessage);

    [LoggerMessage(EventId = 2101, EventName = "CsWebUiNativeLibraryUnchecked", Level = LogLevel.Warning,
        Message = "CS-WebUI loads the WebUI library {Path}, whose version Runic cannot check. Runic needs WebUI commit efac0e24 or later: with an older WebUI, Bridge calls that arrive together can lose their replies or receive another call's arguments or reply.")]
    private static partial void NativeLibraryUnchecked(ILogger logger, string path);

    internal const string NativeLibraryEnvironmentVariable = "CSWEBUI_NATIVE_LIBRARY";

    private static int s_nativeLibraryChecked;

    // SEC-23: the bundled WebUI has the fix for concurrent calls, but a library the application supplies through
    // CSWEBUI_NATIVE_LIBRARY or WebUiNativeLibrary.SetLibraryPath may not, and WebUI reports no version or commit to
    // check. Warns once per process, before the first native call.
    internal static void WarnIfNativeLibraryUnchecked(IServiceProvider services)
    {
        if (Interlocked.Exchange(ref s_nativeLibraryChecked, 1) != 0) return;
        var path = ConfiguredNativeLibrary();
        if (string.IsNullOrWhiteSpace(path)) return;
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(LogCategory);
        if (logger is not null) NativeLibraryUnchecked(logger, path);
        else Trace.TraceWarning($"CS-WebUI loads the WebUI library {path}, whose version Runic cannot check; Runic needs WebUI commit efac0e24 or later.");
    }

    private static string? ConfiguredNativeLibrary() =>
        global::CsWebUi.Native.WebUiNativeLibrary.ConfiguredPath is { Length: > 0 } configured
            ? configured
            : Environment.GetEnvironmentVariable(NativeLibraryEnvironmentVariable);

    // A browser or WebView that cannot show the window fails the open with a Window diagnostic, the same exception
    // Runic Desktop throws when no presentation can open, and is logged; WebUI's exception stays the inner exception.
    internal static RunicWindowConfigurationException ShowFailed(IServiceProvider services, string window, WebUiException error)
    {
        var show = error as WebUiShowException;
        var code = show?.Failure switch
        {
            WebUiShowFailure.BrowserNotFound => "browser-not-found",
            WebUiShowFailure.BrowserNotStarted => "browser-not-started",
            WebUiShowFailure.NotConnected => "browser-not-connected",
            WebUiShowFailure.WebViewNotStarted => "webview-not-started",
            _ => "window-show-failed",
        };
        var browser = show?.Browser.ToString() ?? "unknown";
        var browserPath = show?.BrowserPath ?? "unknown path";
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(LogCategory);
        if (logger is not null) WindowShowFailed(logger, error, window, code, browser, browserPath, error.Message);
        else Trace.TraceError($"CS-WebUI could not show {window} ({code}): {error.Message}");
        // CS-WebUI's message already says how to choose a browser; a WebView failure does not.
        var remediation = show?.Failure == WebUiShowFailure.WebViewNotStarted
            ? "Install the platform WebView (the Edge WebView2 Runtime on Windows, GTK 3 with WebKitGTK 4.1 on Linux), or show the window in a browser."
            : null;
        return new RunicWindowConfigurationException([new RunicWindowDiagnostic(code, error.Message, remediation)], error);
    }
}
