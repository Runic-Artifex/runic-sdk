using CsWebUi;

namespace Runic.Application.Views.CsWebUi;

/// <summary>
/// How a <see cref="RunicCsWebUiHost"/> shows each window. Pass them to
/// <see cref="RunicCsWebUiHost.Run(IServiceProvider, RunicCsWebUiHostOptions, Func{RunicCsWebUiHost, Task{int}})"/>,
/// which keeps the process main thread in WebUI's wait loop, or to the constructor.
/// </summary>
/// <remarks>
/// <code>
/// return RunicCsWebUiHost.Run(provider, new RunicCsWebUiHostOptions { UseWebView = true }, async host =&gt; { ... });
/// </code>
/// </remarks>
public sealed record RunicCsWebUiHostOptions
{
    /// <summary>
    /// Whether windows open in the platform WebView (the Edge WebView2 Runtime on Windows, GTK 3 with WebKitGTK 4.1 on
    /// Linux, WKWebView on macOS) instead of WebUI's recommended presentation: an installed browser in app mode
    /// (Chromium-based browsers first), then the system default browser, then the WebView. The default is
    /// <see langword="false"/>.
    /// </summary>
    public bool UseWebView { get; init; }

    /// <summary>Configures each native window before it is shown, for example its port or kiosk mode.</summary>
    public Action<WebUiWindow>? ConfigureWindow { get; init; }

    /// <summary>
    /// Shows each native window in place of the presentation <see cref="UseWebView"/> selects. It receives the
    /// configured window and <see cref="RunicWindowOptions.StartPage"/>, and can show another URL, such as a frontend
    /// development server, or only start the server with <see cref="WebUiWindow.StartServer"/>.
    /// </summary>
    /// <remarks>
    /// The <c>RUNIC_APPLICATION_SERVE_ONLY</c> switch takes precedence. Under
    /// <see cref="RunicCsWebUiHost.Run(IServiceProvider, RunicCsWebUiHostOptions, Func{RunicCsWebUiHost, Task{int}})"/>
    /// the window counts as shown, so <see cref="RunicWindow{TViewModel}.WaitForCloseAsync"/> completes when WebUI's
    /// wait returns: show the window or start its server here.
    /// </remarks>
    public Action<WebUiWindow, string>? ShowWindow { get; init; }

    /// <summary>
    /// Whether the Bridge error replies of a Window carry the exception type, message and stack, for Windows whose
    /// <see cref="RunicWindowOptions.IncludeFailureDetail"/> is <see langword="null"/>. <see langword="null"/>, the
    /// default, includes them only in a <c>Development</c> environment.
    /// </summary>
    /// <remarks>
    /// The application-wide default; a Window's own <see cref="RunicWindowOptions.IncludeFailureDetail"/> takes
    /// precedence. Detail can contain file paths and application data. The host reads it when each Window opens.
    /// </remarks>
    public bool? IncludeFailureDetail { get; init; }
}
