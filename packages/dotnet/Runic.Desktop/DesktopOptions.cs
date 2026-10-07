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
    /// </remarks>
    public int BrowserLaunchAttempts { get; init; } = DefaultBrowserLaunchAttempts;

    /// <summary>Gets an optional sink for redacted host diagnostics.</summary>
    public Action<DesktopDiagnostic>? DiagnosticSink { get; init; }

    /// <summary>
    /// Gets an optional logger factory for window failures, such as a failed close confirmation.
    /// When omitted, they are written to <see cref="System.Diagnostics.Trace"/>.
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

    /// <summary>Gets a surface-specific security policy, or the host default when omitted.</summary>
    public DesktopSecurityPolicy? Security { get; init; }

    /// <summary>Gets whether this surface owns a separate listener.</summary>
    public bool UseIsolatedListener { get; init; }
}

/// <summary>Identifies an installed or embedded browser presentation.</summary>
public enum BrowserKind
{
    Any,
    Chrome,
    Firefox,
    Edge,
    Safari,
    Chromium,
    Opera,
    Brave,
    Vivaldi,
    Epic,
    Yandex,
    ChromiumBased,
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
    None = 0,
    NativeHandle = 1 << 0,
    Focus = 1 << 1,
    Minimize = 1 << 2,
    Maximize = 1 << 3,
    Resize = 1 << 4,
    Move = 1 << 5,
    CloseConfirmation = 1 << 6,
}

/// <summary>Configures one browser or embedded-WebView presentation.</summary>
public sealed record DesktopWindowOptions
{
    /// <summary>Asynchronously approves a user close request. Requires an embedded host without browser fallback.</summary>
    /// <remarks>Runs on the thread pool. Return false to keep the window open. Exceptions and cancellation deny
    /// closing. Concurrent requests share a decision. The token is cancelled when the window is forcibly closed.
    /// CloseAsync and disposal bypass confirmation; marshal native UI work to its owning thread.</remarks>
    public Func<CancellationToken, ValueTask<bool>>? ConfirmCloseAsync { get; init; }

    /// <summary>Gets the presentation: the platform's embedded WebView by default, or an installed browser.</summary>
    /// <remarks>On Linux, the embedded WebView requires <see cref="DesktopHostOptions.Linux"/> to select a toolkit.</remarks>
    public BrowserKind Browser { get; init; } = BrowserKind.Embedded;
    public DesktopPresentationPolicy PresentationPolicy { get; init; }
    public DesktopPermissionGrant AllowedPermissions { get; init; }
    public uint Width { get; init; } = 800;
    public uint Height { get; init; } = 600;
    public uint? MinimumWidth { get; init; }
    public uint? MinimumHeight { get; init; }
    public uint? X { get; init; }
    public uint? Y { get; init; }
    public bool Centered { get; init; }
    public bool Resizable { get; init; } = true;
    public bool Frameless { get; init; }
    public bool Transparent { get; init; }
    public bool Hidden { get; init; }
    public bool Kiosk { get; init; }
    public bool? HighContrast { get; init; }
    public string? IconFile { get; init; }
    public string? ProfileName { get; init; }
    public string? ProfilePath { get; init; }
    public string? ProxyServer { get; init; }
    public string? BrowserArguments { get; init; }
}
