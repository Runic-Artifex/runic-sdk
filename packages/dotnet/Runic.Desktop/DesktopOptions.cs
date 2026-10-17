using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Runic.Desktop;

/// <summary>Controls which network interfaces a Desktop host listens on.</summary>
public enum DesktopNetworkExposure
{
    /// <summary>Accept requests only through a local loopback interface.</summary>
    Loopback,

    /// <summary>Accept requests through every IPv4 interface.</summary>
    AllInterfaces,
}

/// <summary>Controls concurrent client admission for one surface.</summary>
public enum DesktopClientAdmission
{
    /// <summary>Admit one connected client at a time.</summary>
    Single,

    /// <summary>Admit multiple concurrent clients.</summary>
    Multiple,
}

/// <summary>Defines immutable session admission policy for Desktop surfaces.</summary>
public sealed record DesktopSecurityPolicy
{
    /// <summary>Gets a safe loopback-browser policy.</summary>
    public static DesktopSecurityPolicy Default { get; } = new();

    /// <summary>Gets whether a browser must present its surface-scoped session credential.</summary>
    public bool RequireSessionCredential { get; init; } = true;

    /// <summary>Gets whether non-browser clients may omit the <c>Origin</c> header.</summary>
    public bool AllowMissingOrigin { get; init; }

    /// <summary>Gets whether one or multiple clients may connect concurrently.</summary>
    public DesktopClientAdmission ClientAdmission { get; init; } = DesktopClientAdmission.Single;

    /// <summary>Gets additional canonical HTTP or HTTPS origins admitted alongside the surface's own origin.</summary>
    public IReadOnlyList<Uri> AdditionalOrigins { get; init; } = [];
}

/// <summary>Configures a Desktop host before its listener starts.</summary>
public sealed record DesktopHostOptions
{
    /// <summary>Gets the TCP port, where zero selects an ephemeral port.</summary>
    public int Port { get; init; }

    /// <summary>Gets explicit Linux embedded-backend selection.</summary>
    public LinuxDesktopOptions Linux { get; init; } = new();

    /// <summary>Gets the listener exposure policy.</summary>
    public DesktopNetworkExposure NetworkExposure { get; init; } = DesktopNetworkExposure.Loopback;

    /// <summary>Gets the default security policy copied by newly created surfaces.</summary>
    /// <remarks><see cref="DesktopNetworkExposure.AllInterfaces"/> requires assigning this property explicitly.</remarks>
    public DesktopSecurityPolicy Security
    {
        get => _security;
        init
        {
            _security = value;
            HasExplicitSecurity = true;
        }
    }

    private readonly DesktopSecurityPolicy _security = DesktopSecurityPolicy.Default;

    internal bool HasExplicitSecurity { get; private init; }

    /// <summary>Gets an optional service-registration callback run once while the host is built.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>Gets an optional folder searched before system browser locations.</summary>
    public string? BrowserFolder { get; init; }

    /// <summary>Gets the embedded-window factory, or the platform default when omitted.</summary>
    public IDesktopWindowHostFactory? WindowHostFactory { get; init; }

    /// <summary>Gets whether opening a window waits for bridge authentication.</summary>
    public bool WaitForConnection { get; init; } = true;

    /// <summary>Gets the bridge authentication deadline.</summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(15);

    internal const int DefaultBrowserLaunchAttempts = 2;

    internal const int MaximumBrowserLaunchAttempts = 5;

    /// <summary>
    /// Gets how many times a browser presentation is launched when the browser starts but requests nothing.
    /// </summary>
    /// <remarks>
    /// Each attempt waits the whole <see cref="ConnectionTimeout"/>. A browser that is still running but has
    /// not requested its page by then is stopped and launched again, with a fresh profile unless the window
    /// configures one, and a <c>browser-launch-stalled</c> diagnostic is reported. A page that was requested,
    /// a browser that exited and embedded WebViews are never relaunched. Defaults to 2; 1 disables relaunching.
    /// Values outside 1 to 5 make building the host throw <see cref="ArgumentOutOfRangeException"/>.
    /// </remarks>
    public int BrowserLaunchAttempts { get; init; } = DefaultBrowserLaunchAttempts;

    /// <summary>
    /// Gets whether <see cref="DesktopEventLoop.Run(DesktopHostOptions, Func{DesktopHost, Task{int}})"/> handles
    /// Ctrl+C, SIGTERM and SIGHUP (a closed Windows console) by shutting the application down in order. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// The first signal cancels <see cref="DesktopHost.ShutdownRequested"/> and closes every open window without
    /// close confirmation, as if the user had closed them; the application then returns, its window and services
    /// release their work, and the host is disposed. A second Ctrl+C, at least a second after the first signal, keeps
    /// its default action and ends the process at once; other repeats are ignored. While the host has no surface (no
    /// window and no page served at a surface URL), the first signal also keeps its default action unless
    /// <see cref="HandlesShutdownWithoutWindow"/> is set: nothing would end the application. Set to <see langword="false"/> when the application handles these signals itself. Hosts created with
    /// <see cref="DesktopHost.StartAsync"/> never register signal handlers.
    /// </remarks>
    public bool HandleShutdownSignals { get; init; } = true;

    /// <summary>
    /// Gets whether the application ends its work when <see cref="DesktopHost.ShutdownRequested"/> is cancelled, also
    /// while no window is open. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Set it for an application that runs without a window, such as one behind a status icon, and end its loop on
    /// <see cref="DesktopHost.ShutdownRequested"/>. Then the first Ctrl+C, SIGTERM or SIGHUP shuts it down in order
    /// whether or not a window is open. When it is <see langword="false"/> and the host has no surface, neither a native
    /// or browser window nor a page served at a surface URL, the first signal keeps its default action and ends the
    /// process, because the host has nothing to close and nothing else would end the application. Has no effect when <see cref="HandleShutdownSignals"/> is <see langword="false"/>.
    /// </remarks>
    public bool HandlesShutdownWithoutWindow { get; init; }

    /// <summary>
    /// Gets the application's menus for the macOS menu bar, beside the standard application, File, Edit, View and
    /// Window menus; empty by default. Windows and Linux show no native menu bar in this release.
    /// </summary>
    /// <remarks>
    /// Each <see cref="DesktopMenuItem"/> names a command that the key window's <see cref="DesktopWindow.MenuCommands"/>
    /// runs, or else <see cref="DesktopHost.MenuCommands"/>; an item is disabled while neither can run it. Titles are
    /// user-visible text, so take them from the application's translations, and call
    /// <see cref="DesktopHost.SetMenuBar"/> to show the bar again after the language changes. Building the host throws
    /// <see cref="ArgumentException"/> for a menu without a title, an item without a title or command, or a second
    /// Application, File or Help menu. See <see cref="DesktopHost.Validate"/> for the shortcuts it does not apply.
    /// </remarks>
    public IReadOnlyList<DesktopMenu> MenuBar { get; init; } = [];

    /// <summary>Gets an optional sink for redacted host diagnostics.</summary>
    public Action<DesktopDiagnostic>? DiagnosticSink { get; init; }

    /// <summary>
    /// Gets an optional logger factory for host and window messages, such as a shutdown request or a failed close
    /// confirmation. <c>RunicDesktopHost.Run</c> lends it the application's registered <c>ILoggerFactory</c> when
    /// it is not set, until <c>Run</c> returns; a later message goes to Trace. When omitted, they are written to <see cref="System.Diagnostics.Trace"/>.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; init; }
}

/// <summary>Configures one isolated presentation surface.</summary>
public sealed record DesktopSurfaceOptions
{
    /// <summary>Gets the optional single-segment listener path; an opaque path is generated when omitted.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// Gets what the surface serves: a local <see cref="DesktopContent.Directory"/>, an <see cref="DesktopContent.Html"/>
    /// document, an <see cref="DesktopContent.ExternalUrl"/>, or a request <see cref="DesktopContent.Handler"/>.
    /// </summary>
    /// <remarks>Defaults to the current directory with index discovery.</remarks>
    public DesktopContent Content { get; init; } = new DesktopContent.Directory(Environment.CurrentDirectory);

    /// <summary>
    /// Gets a handler for the requests that <see cref="Content"/> does not serve, such as application files outside
    /// the frontend build, or <see langword="null"/>, the default, for none.
    /// </summary>
    /// <remarks>
    /// The surface asks the handler for a <c>GET</c> or <c>HEAD</c> request only when <see cref="Content"/> has no
    /// file or response for it, before it responds 404 Not Found; a <see langword="null"/> response is 404. The
    /// resources are served from the surface's own URL, so a page shown in a window and the same page served to a
    /// browser load them alike, for example as <c>&lt;img src="icons/org.example.App.png"&gt;</c>.
    /// <see cref="ContentRequest.Path"/> is decoded and can contain <c>..</c> segments: never pass it to the file
    /// system unchecked.
    /// </remarks>
    public ContentHandler? Resources { get; init; }

    /// <summary>Gets a surface-specific security policy, or the host default when omitted.</summary>
    public DesktopSecurityPolicy? Security { get; init; }

    /// <summary>Gets whether this surface owns a separate listener.</summary>
    public bool UseIsolatedListener { get; init; }
}

/// <summary>Identifies an installed or embedded browser presentation.</summary>
public enum BrowserKind
{
    /// <summary>The first available installed browser.</summary>
    Any,

    /// <summary>Google Chrome.</summary>
    Chrome,

    /// <summary>Mozilla Firefox.</summary>
    Firefox,

    /// <summary>Microsoft Edge.</summary>
    Edge,

    /// <summary>Apple Safari. Runic Desktop cannot launch it as a presentation.</summary>
    Safari,

    /// <summary>Chromium.</summary>
    Chromium,

    /// <summary>Opera. Runic Desktop cannot launch it as a presentation.</summary>
    Opera,

    /// <summary>Brave.</summary>
    Brave,

    /// <summary>Vivaldi.</summary>
    Vivaldi,

    /// <summary>Epic Privacy Browser.</summary>
    Epic,

    /// <summary>Yandex Browser.</summary>
    Yandex,

    /// <summary>The first available installed Chromium-based browser, searched in the order Chrome, Edge, Epic, Vivaldi, Brave, Yandex, Chromium.</summary>
    ChromiumBased,

    /// <summary>The platform's embedded WebView: WebView2 on Windows, WKWebView on macOS, or the selected WebKitGTK toolkit on Linux.</summary>
    Embedded,
}

/// <summary>Controls whether a requested presentation may explicitly fall back to another host.</summary>
public enum DesktopPresentationPolicy
{
    /// <summary>Open only the requested browser or embedded WebView.</summary>
    RequestedOnly,

    /// <summary>Prefer the embedded WebView and fall back to the selected installed browser.</summary>
    EmbeddedThenBrowser,
}

/// <summary>Identifies sensitive presentation permissions that an application may explicitly grant.</summary>
[Flags]
public enum DesktopPermissionGrant
{
    /// <summary>Deny sensitive permissions.</summary>
    None = 0,

    /// <summary>Allow camera and microphone capture for the presentation.</summary>
    /// <remarks>
    /// WebView2, WebKitGTK and GTK 4 windows grant it only to the origin of the presented URL, never screen capture.
    /// WKWebView windows do not apply the grant; WebKit asks the user on macOS. An explicitly selected
    /// Chromium-based browser accepts capture for every origin it opens, and Firefox asks the user. A
    /// <see cref="DesktopPresentationPolicy.EmbeddedThenBrowser"/> fallback opens without the grant.
    /// <see cref="DesktopHost.Validate"/> reports each of these cases before the window opens.
    /// </remarks>
    MediaCapture = 1 << 0,
}

/// <summary>Identifies a platform-window operation reported by a presentation.</summary>
[Flags]
public enum DesktopWindowCapabilities
{
    /// <summary>No platform-window operation is available.</summary>
    None = 0,

    /// <summary>The presentation exposes its native top-level window handle.</summary>
    NativeHandle = 1 << 0,

    /// <summary>The window can be activated and focused.</summary>
    Focus = 1 << 1,

    /// <summary>The window can be minimized.</summary>
    Minimize = 1 << 2,

    /// <summary>The window can be maximized and restored.</summary>
    Maximize = 1 << 3,

    /// <summary>The window can be resized.</summary>
    Resize = 1 << 4,

    /// <summary>The window can be moved to a screen position.</summary>
    Move = 1 << 5,

    /// <summary>User close requests can be confirmed by <see cref="DesktopWindowOptions.ConfirmCloseAsync"/>.</summary>
    CloseConfirmation = 1 << 6,

    /// <summary>The window can ask the screen reader to announce a message (<see cref="DesktopWindow.AnnounceAsync"/>).</summary>
    Announcements = 1 << 7,

    /// <summary>
    /// The window reports its bounds, maximized and full-screen state (<see cref="DesktopWindow.State"/> and
    /// <see cref="DesktopWindow.StateChanged"/>), and applies <see cref="DesktopWindowOptions.Placement"/>.
    /// </summary>
    State = 1 << 8,

    /// <summary>
    /// The window reports files the user drops onto it, with their native paths (<see cref="DesktopWindow.FilesDropped"/>).
    /// </summary>
    FileDrop = 1 << 9,
}

/// <summary>How urgently a screen reader speaks an announcement (<see cref="DesktopWindow.AnnounceAsync"/>).</summary>
public enum DesktopAnnouncementPriority
{
    /// <summary>After what the screen reader is saying, as <c>aria-live="polite"</c>.</summary>
    Polite,

    /// <summary>Interrupting what the screen reader is saying, as <c>aria-live="assertive"</c>; for errors and other messages the user must hear at once.</summary>
    Assertive,
}

/// <summary>Configures one browser or embedded-WebView presentation.</summary>
public sealed record DesktopWindowOptions
{
    /// <summary>Asynchronously approves a user close request. Requires the embedded window.</summary>
    /// <remarks>A browser cannot keep its window open: an explicitly selected browser fails with
    /// <c>close-confirmation-unavailable</c>, and an <see cref="DesktopPresentationPolicy.EmbeddedThenBrowser"/> fallback
    /// opens without confirmation and reports <c>close-confirmation-unavailable</c> as a warning.
    /// Runs on the thread pool. Return false to keep the window open. Exceptions and cancellation deny closing.
    /// Concurrent requests share a decision. The token is cancelled when the window is forcibly closed.
    /// CloseAsync and disposal bypass confirmation; marshal native UI work to its owning thread.</remarks>
    public Func<CancellationToken, ValueTask<bool>>? ConfirmCloseAsync { get; init; }

    /// <summary>Gets the presentation: the platform's embedded WebView by default, or an installed browser.</summary>
    /// <remarks>On Linux, the embedded WebView requires <see cref="DesktopHostOptions.Linux"/> to select a toolkit.</remarks>
    public BrowserKind Browser { get; init; } = BrowserKind.Embedded;

    /// <summary>Gets whether the window may fall back to an installed browser when the embedded WebView cannot open.</summary>
    /// <remarks>
    /// Defaults to <see cref="DesktopPresentationPolicy.RequestedOnly"/>. With
    /// <see cref="DesktopPresentationPolicy.EmbeddedThenBrowser"/>, the embedded WebView is tried first and
    /// <see cref="Browser"/> selects the fallback browser, where <see cref="BrowserKind.Embedded"/> means
    /// <see cref="BrowserKind.Any"/>. The fallback opens without <see cref="AllowedPermissions"/>.
    /// </remarks>
    public DesktopPresentationPolicy PresentationPolicy { get; init; }

    /// <summary>Gets the sensitive permissions granted to the presentation; none by default.</summary>
    public DesktopPermissionGrant AllowedPermissions { get; init; }

    /// <summary>Gets the initial window width in pixels. Defaults to 800.</summary>
    public uint Width { get; init; } = 800;

    /// <summary>Gets the initial window height in pixels. Defaults to 600.</summary>
    public uint Height { get; init; } = 600;

    /// <summary>Gets the minimum window width in pixels, or <see langword="null"/> for no minimum.</summary>
    /// <remarks>Applies only together with <see cref="MinimumHeight"/>, and only to embedded windows.</remarks>
    public uint? MinimumWidth { get; init; }

    /// <summary>Gets the minimum window height in pixels, or <see langword="null"/> for no minimum.</summary>
    /// <remarks>Applies only together with <see cref="MinimumWidth"/>, and only to embedded windows.</remarks>
    public uint? MinimumHeight { get; init; }

    /// <summary>Gets the initial horizontal screen position in pixels, or <see langword="null"/> to let the platform choose.</summary>
    /// <remarks>Applies only together with <see cref="Y"/>; <see cref="Centered"/> takes precedence. Firefox ignores it, and installed browsers ignore a negative position.
    /// When unset, the Windows WebView2 window opens at (100, 100). Windows scales <see cref="Width"/> and
    /// <see cref="Height"/> for the monitor DPI, but not the position.</remarks>
    public int? X { get; init; }

    /// <summary>Gets the initial vertical screen position in pixels, or <see langword="null"/> to let the platform choose.</summary>
    /// <remarks>Applies only together with <see cref="X"/>; <see cref="Centered"/> takes precedence. Firefox ignores it, and installed browsers ignore a negative position.
    /// When unset, the Windows WebView2 window opens at (100, 100). Windows scales <see cref="Width"/> and
    /// <see cref="Height"/> for the monitor DPI, but not the position.</remarks>
    public int? Y { get; init; }

    /// <summary>Gets whether the embedded window opens centered on the screen, replacing <see cref="X"/> and <see cref="Y"/>.</summary>
    public bool Centered { get; init; }

    /// <summary>
    /// Gets a saved placement to restore, from <see cref="DesktopWindowState.Placement"/>, or <see langword="null"/>,
    /// the default, to open with <see cref="Width"/>, <see cref="Height"/>, <see cref="X"/>, <see cref="Y"/> and
    /// <see cref="Centered"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A placement replaces <see cref="Width"/>, <see cref="Height"/>, <see cref="X"/>, <see cref="Y"/> and
    /// <see cref="Centered"/>. An embedded window that reports <see cref="DesktopWindowCapabilities.State"/> keeps it on a
    /// monitor that exists now: when the saved bounds are not inside one monitor's work area (the screen without the
    /// taskbar, Dock or panels), for example because they were saved on an external monitor that is no longer
    /// connected, the window moves to the work area it overlaps most, or else to the nearest one, and shrinks to fit
    /// it. A placement without a position keeps the platform's choice of position and is only shrunk to fit. The
    /// window then opens maximized when <see cref="DesktopWindowPlacement.Maximized"/> is set, unless
    /// <see cref="Kiosk"/> is set, and restores to the saved bounds.
    /// </para>
    /// <para>
    /// A saved placement never fails the open: a zero width or height keeps <see cref="Width"/> or <see cref="Height"/>,
    /// a position with only one coordinate is ignored, and a size above 65535 is reduced to it. The GTK 4 window
    /// restores the size and maximized state and ignores the position, which GTK 4 cannot set. An installed browser
    /// opens with the saved size and position as it would with <see cref="Width"/>, <see cref="Height"/>,
    /// <see cref="X"/> and <see cref="Y"/>, without fitting them to a monitor or maximizing; <see cref="DesktopHost.Validate"/>
    /// reports the ignored maximized state.
    /// </para>
    /// </remarks>
    public DesktopWindowPlacement? Placement { get; init; }

    /// <summary>Gets whether the user can resize the embedded window. Defaults to <see langword="true"/>.</summary>
    public bool Resizable { get; init; } = true;

    /// <summary>Gets whether the embedded window opens without a title bar and border.</summary>
    public bool Frameless { get; init; }

    /// <summary>Gets whether the embedded window has a transparent background where the page does not paint.</summary>
    public bool Transparent { get; init; }

    /// <summary>Gets whether the presentation opens without a visible window.</summary>
    /// <remarks>Embedded windows are created hidden; installed browsers run headless.</remarks>
    public bool Hidden { get; init; }

    /// <summary>Gets whether the presentation opens in full-screen kiosk mode without window decorations.</summary>
    public bool Kiosk { get; init; }

    /// <summary>
    /// Gets whether the presentation reports a high-contrast theme to the page, or <see langword="null"/> to follow
    /// <see cref="DesktopPlatform.IsHighContrast"/>.
    /// </summary>
    public bool? HighContrast { get; init; }

    /// <summary>Gets the path of the embedded window's icon file, or <see langword="null"/> for the default icon.</summary>
    public string? IconFile { get; init; }

    /// <summary>Gets the name of an existing Firefox profile to open.</summary>
    /// <remarks>Only Firefox applies it, and only when <see cref="ProfilePath"/> is not set.</remarks>
    public string? ProfileName { get; init; }

    /// <summary>Gets the browser profile directory, created when missing.</summary>
    /// <remarks>
    /// Chromium-based browsers and WebView2 use it as their user-data directory, and Firefox as its profile.
    /// The macOS and GTK 3 embedded windows ignore it. When neither this nor <see cref="ProfileName"/> is set,
    /// a fresh temporary profile is generated for the presentation.
    /// </remarks>
    public string? ProfilePath { get; init; }

    /// <summary>Gets the proxy server passed to a Chromium-based browser, such as <c>http://proxy:8080</c>.</summary>
    /// <remarks>
    /// Without it, Chromium-based browsers start with no proxy unless <see cref="BrowserArguments"/> is set.
    /// Embedded windows use the system proxy, and Firefox ignores the option.
    /// </remarks>
    public string? ProxyServer { get; init; }

    /// <summary>Gets additional command-line arguments for the browser or WebView2 runtime.</summary>
    /// <remarks>
    /// The string is split like a command line and appended to the launch arguments. When set, installed browsers
    /// no longer receive Runic Desktop's default isolation flags. The macOS and GTK 3 embedded windows ignore it.
    /// </remarks>
    public string? BrowserArguments { get; init; }

    // The options a presentation opens with: a placement's values replace the size and position, sanitized so a
    // damaged settings file cannot fail the open. The placement stays set for the hosts that fit it to a monitor.
    internal DesktopWindowOptions WithPlacementApplied()
    {
        if (Placement is not { } placement)
        {
            return this;
        }
        var hasPosition = placement.X is not null && placement.Y is not null;
        return this with
        {
            Width = placement.Width == 0 ? Width : Math.Min(placement.Width, Internal.WindowPlacementClamp.MaximumDimension),
            Height = placement.Height == 0 ? Height : Math.Min(placement.Height, Internal.WindowPlacementClamp.MaximumDimension),
            X = hasPosition ? placement.X : null,
            Y = hasPosition ? placement.Y : null,
            Centered = false,
        };
    }
}
