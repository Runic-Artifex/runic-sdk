using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Platform;

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

    /// <summary>Gets the macOS application options: the Dock menu and an application delegate of the application's own.</summary>
    public MacOSDesktopOptions MacOS { get; init; } = new();

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
    /// <remarks>
    /// <para>
    /// The deadline counts from the launch while the page has requested nothing, and from its first request once it
    /// has, so a slow browser start that reaches the server just before the deadline still gets the whole timeout to
    /// finish authentication.
    /// </para>
    /// <para>
    /// An embedded WebView that has requested nothing yet but is still loading the page extends the deadline: it does
    /// not run out while the WebView's first navigation is in progress, and not within a whole timeout of the last
    /// startup step the WebView reported, up to four times this timeout from the launch (60 seconds with the default).
    /// A fresh WebView2 profile can take many seconds to start while Windows services the disk. The Windows WebView2
    /// windows report this progress; the other embedded windows and browsers keep the plain deadline. An extension logs
    /// event 3021, and a timeout at the limit says how long the WebView was still loading.
    /// </para>
    /// <para>
    /// A launch therefore waits at most twice this timeout, and an embedded WebView that requests its page just before
    /// the extended limit at most five times it.
    /// </para>
    /// </remarks>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(15);

    // The clock of the surfaces' connection deadlines and Bridge handshakes; tests replace it.
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

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
    /// Gets the application's decision whether to quit, or <see langword="null"/>, the default, to quit without asking.
    /// Returning <see langword="false"/> keeps the application running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On macOS every quit takes one path, also with no window open: Quit in the application menu (Command and Q) or
    /// another sender of <c>terminate:</c> (<see cref="DesktopQuitSource.Menu"/>), Quit in the Dock or a script's quit
    /// Apple event (<see cref="DesktopQuitSource.Dock"/>), and logging out, restarting or shutting down
    /// (<see cref="DesktopQuitSource.System"/>). Runic first asks this callback, then each window's close guard
    /// (<see cref="DesktopWindowOptions.ConfirmCloseAsync"/>) one at a time; a window whose guard agrees closes. When all
    /// agree, <see cref="DesktopHost.ShutdownRequested"/> is cancelled and the remaining windows close without asking, as
    /// for a signal, and AppKit's reply to the system waits until the application's work returns. A refusal at any step
    /// keeps the application running and, for a logout or restart, cancels it. Save documents here, or ask the user
    /// whether to; the callback runs on the thread pool.
    /// </para>
    /// <para>
    /// With <see cref="HandleShutdownSignals"/>, Ctrl+C, SIGTERM and SIGHUP ask it too
    /// (<see cref="DesktopQuitSource.Signal"/>), on every platform, before the shutdown that closes the windows without
    /// close guards; a refusal keeps the application running and a later signal asks again. A second Ctrl+C at least a
    /// second after the first still ends the process at once, also while the callback runs. On Windows a closed console,
    /// a logoff or a shutdown ends the process when Windows' own timeout expires, whatever the callback answers.
    /// </para>
    /// <para>
    /// The token is cancelled when the host shuts down for another reason while the callback runs, such as a signal;
    /// the shutdown then goes ahead. A callback that throws counts as a refusal and is logged as event 3019,
    /// <c>DesktopQuitConfirmationFailed</c>. Requests that arrive while the callback runs share its answer.
    /// </para>
    /// </remarks>
    public Func<DesktopQuitRequest, CancellationToken, ValueTask<bool>>? ConfirmQuitAsync { get; init; }

    /// <summary>
    /// Gets the application's menus for the menu bar: on macOS beside the standard application, File, Edit, View and
    /// Window menus, in a Windows window on their own; empty by default. The GTK windows show no native menu bar in this
    /// release.
    /// </summary>
    /// <remarks>
    /// Each <see cref="DesktopMenuItem"/> names a command that the key window's <see cref="DesktopWindow.MenuCommands"/>
    /// runs, or else <see cref="DesktopHost.MenuCommands"/>; an item is enabled while its
    /// <see cref="DesktopMenuItem.IsEnabled"/> is <see langword="true"/> and one of them can run it, and shows
    /// <see cref="DesktopMenuItem.IsChecked"/> as declared. Titles are user-visible text, so take them from the
    /// application's translations, and call <see cref="DesktopHost.SetMenuBar"/> to show the bar again after the language
    /// changes. Building the host throws <see cref="ArgumentException"/> for a blank title or one longer than 128
    /// characters, a command id outside 1 to 64 ASCII letters, digits, dots, underscores or hyphens, a command id starting
    /// with the reserved <c>runic.</c>, or a second Application, File or Help menu. See <see cref="DesktopHost.Validate"/>
    /// for the shortcuts it does not apply.
    /// </remarks>
    public IReadOnlyList<DesktopMenu> MenuBar { get; init; } = [];

    /// <summary>
    /// Gets the titles of the standard macOS menus and their items (application, File, Edit, View and Window), or
    /// <see langword="null"/>, the default, for the English titles.
    /// </summary>
    /// <remarks>
    /// Runic ships no translations: take the titles from the application's own, for example from its Runic Translations
    /// catalog for the user's language, as for <see cref="MenuBar"/>. They apply when the first window opens and to
    /// the menus Runic adds to an empty main menu; an application that builds its own main menu keeps its titles. Windows
    /// and Linux show no standard menus. Building the host throws <see cref="ArgumentException"/> for an invalid title,
    /// and <see cref="DesktopHost.Validate"/> reports it as <c>menu-title-invalid</c>; see
    /// <see cref="DesktopStandardMenuTitles"/>.
    /// </remarks>
    public DesktopStandardMenuTitles? StandardMenuTitles { get; init; }

    /// <summary>
    /// Gets the BCP 47 languages that the embedded windows' spell checker checks, such as <c>de-CH</c> and <c>en</c>;
    /// empty, the default, for the user's languages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The spell checkers of WebKitGTK and macOS belong to the process, not to a window, so the languages are the
    /// application's: every window checks with them, and <see cref="DesktopHost.SetSpellCheckingLanguagesAsync"/> changes
    /// them for all, for example when the language of the open document changes. A word is correct when one of the
    /// languages accepts it; the page's <c>lang</c> attribute does not choose a dictionary on any platform.
    /// <see cref="DesktopWindowOptions.SpellChecking"/> turns the checker off in a window.
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Linux (WebKitGTK): each language selects an installed hunspell dictionary through enchant. <c>de-CH</c> selects
    /// <c>de_CH</c>, or else a dictionary for <c>de</c> alone, never one for another region. A language without a region
    /// selects the region of the user's locale when it has the same language, such as <c>en_GB</c> for <c>en</c> with
    /// <c>LANG=en_GB.UTF-8</c>, else the language's most common region, else any installed region of it. Without languages,
    /// the dictionaries of the user's <c>LANGUAGE</c>, <c>LC_ALL</c>, <c>LC_MESSAGES</c> and <c>LANG</c> are used, and
    /// WebKitGTK chooses one when none of them is installed. A language without a dictionary is not checked, and the
    /// window reports <c>spell-checking-dictionary-missing</c> once. A desktop may have no dictionaries at all: install
    /// them with the distribution's hunspell packages, or with Nix through <c>buildRunicApplication</c>'s
    /// <c>spellingDictionaries</c>.
    /// </description></item>
    /// <item><description>
    /// macOS: WKWebView checks with the application's <c>NSSpellChecker</c>. One language selects that language
    /// (<c>setLanguage:</c>), matched against <c>availableLanguages</c> as on Linux. With several languages, or none,
    /// macOS identifies each paragraph's language among the user's spelling languages (System Settings › Keyboard ›
    /// Text Input), because <c>NSSpellChecker</c> cannot be limited to a set of languages.
    /// </description></item>
    /// <item><description>
    /// Windows: WebView2 has no setting for its spelling languages; it checks in its own language, which
    /// <see cref="DesktopWindowOptions.Language"/> sets. It ignores the option, and <see cref="DesktopHost.Validate"/>
    /// reports <c>window-option-unsupported</c>.
    /// </description></item>
    /// </list>
    /// <para>
    /// Installed browsers use their own settings. Two hosts in one process share the platform's checker, so the
    /// languages set last apply. Building the host throws <see cref="ArgumentException"/> for a value that is not a
    /// BCP 47 language tag.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> SpellCheckingLanguages { get; init; } = [];

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

    /// <summary>
    /// Gets the <c>Content-Security-Policy</c> that the surface sends with the HTML of its content, or
    /// <see langword="null"/>, the default, for none.
    /// </summary>
    /// <remarks>
    /// The policy applies to <c>text/html</c> responses of <see cref="DesktopContent.Directory"/>,
    /// <see cref="DesktopContent.Html"/> and <see cref="DesktopContent.Handler"/> content and of
    /// <see cref="Resources"/>. It is sent as a header of its own next to the surface's
    /// <c>frame-ancestors</c> policy, and next to a policy that a content handler sets: the browser or WebView enforces
    /// every one, so a page can do only what all of them allow. Every embedded WebView and installed browser applies
    /// it, since content is served over the surface's loopback listener. The surface's document-start scripts are
    /// not subject to page policies. The Bridge needs only <c>'self'</c>: its script is served by the surface, its
    /// WebSocket is a same-origin connection, and the Views Bridge's state arrives as data that the script parses. Only
    /// <see cref="DesktopSurface.ExecuteJavaScriptAsync"/> evaluates a string in the page, with the <c>Function</c>
    /// constructor, so it fails under a policy whose <c>script-src</c> (or <c>default-src</c>) lacks
    /// <c>'unsafe-eval'</c>. The value must be printable ASCII: a carriage return, line feed, NUL or other control
    /// character throws <see cref="ArgumentException"/>.
    /// <code>
    /// ContentSecurityPolicy = "default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'"
    /// </code>
    /// </remarks>
    /// <exception cref="ArgumentException">The value is empty or contains a character that a header cannot carry.</exception>
    public string? ContentSecurityPolicy
    {
        get;
        init => field = ValidateContentSecurityPolicy(value, nameof(ContentSecurityPolicy));
    }

    internal static string? ValidateContentSecurityPolicy(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A Content-Security-Policy must not be empty; use null for none.", parameterName);
        }

        foreach (var character in value)
        {
            if (character is < ' ' or > '~')
            {
                throw new ArgumentException(
                    "A Content-Security-Policy must be printable ASCII without line breaks or control characters.",
                    parameterName);
            }
        }

        return value;
    }
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

    /// <summary>Apple Safari: detection only.</summary>
    /// <remarks>
    /// Not a presentation: Runic Desktop neither discovers nor launches it, and <see cref="DesktopPlatform.GetAvailability"/>
    /// does not list it. Requesting it reports <c>browser-unsupported</c> naming this browser, so a configured value,
    /// such as one read from settings, is recognized and reported precisely instead of being rejected as unknown.
    /// </remarks>
    Safari,

    /// <summary>Chromium.</summary>
    Chromium,

    /// <summary>Opera: detection only.</summary>
    /// <remarks>
    /// Not a presentation: Runic Desktop neither discovers nor launches it, and <see cref="DesktopPlatform.GetAvailability"/>
    /// does not list it. Requesting it reports <c>browser-unsupported</c> naming this browser, so a configured value,
    /// such as one read from settings, is recognized and reported precisely instead of being rejected as unknown.
    /// </remarks>
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
    /// <remarks>
    /// Shorthand for <see cref="DesktopWindowOptions.Browser"/> = <see cref="BrowserKind.Embedded"/> with the selected
    /// browser as the only entry of <see cref="DesktopWindowOptions.Fallbacks"/>. A non-empty
    /// <see cref="DesktopWindowOptions.Fallbacks"/> replaces that one browser.
    /// </remarks>
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

/// <summary>What an embedded window does when its page leaves the application's own origins.</summary>
/// <remarks>The enum is closed.</remarks>
public enum DesktopExternalNavigation
{
    /// <summary>
    /// Keep the window on the application: cancel the navigation and, when the user started it (a click), open an
    /// <c>http</c>, <c>https</c> or <c>mailto</c> URL with the system's default handler, such as the default browser.
    /// Other schemes, and navigations a script starts without a user gesture, are cancelled. WKWebView reports a
    /// script's <c>click()</c> on a link as a link activation, so on macOS a page script can open such a URL too.
    /// </summary>
    OpenInBrowser,

    /// <summary>Cancel every navigation and new-window request outside the application's origins.</summary>
    Block,

    /// <summary>
    /// Load other origins in the window, as before 0.10. A new-window request for an <c>http</c> or <c>https</c> URL
    /// loads it in this window too; no embedded window ever opens a second WebView window.
    /// </summary>
    AllowInWindow,
}

/// <summary>
/// Controls what an embedded window (WebView2, WKWebView, WebKitGTK and the WPF <c>RunicWebView</c>) does with
/// navigation away from the application, new windows, downloads and developer tools.
/// </summary>
/// <remarks>
/// The application's origins are the origin of the presented surface and its
/// <see cref="DesktopSecurityPolicy.AdditionalOrigins"/>; their documents always load in the window, and iframes are
/// not affected. A new-window request (<c>target="_blank"</c>, <c>window.open</c>) never opens a second WebView
/// window: one for an application origin is cancelled, and other URLs follow <see cref="ExternalNavigation"/>. Open
/// further application windows from .NET. Installed browsers apply their own navigation and download behaviour.
/// </remarks>
public sealed record DesktopWebContentPolicy
{
    /// <summary>
    /// Gets the safe default: external links open in the default browser, downloads are refused, and developer tools
    /// are available only in Development.
    /// </summary>
    public static DesktopWebContentPolicy Default { get; } = new();

    /// <summary>Gets what a navigation or new-window request outside the application's origins does.</summary>
    public DesktopExternalNavigation ExternalNavigation { get; init; } = DesktopExternalNavigation.OpenInBrowser;

    /// <summary>Gets whether the page may download files. Downloads are cancelled by default.</summary>
    /// <remarks>
    /// When <see langword="true"/>, WebView2 and WebKitGTK download with their own behaviour: WebView2 shows its
    /// download dialog; WebKitGTK saves <c>&lt;a download&gt;</c> links and <c>Content-Disposition: attachment</c>
    /// responses to the user's download folder and still ignores other content it cannot show. The macOS window saves
    /// <c>&lt;a download&gt;</c> links, attachments and responses WKWebView cannot show to the user's Downloads folder
    /// under the suggested name, as <c>name (1).ext</c> rather than replace an existing file, and marks them with the
    /// quarantine of web downloads. A sandboxed macOS application needs the
    /// <c>com.apple.security.files.downloads.read-write</c> entitlement for that folder.
    /// </remarks>
    public bool AllowDownloads { get; init; }

    /// <summary>
    /// Gets whether the window's developer tools (F12, Inspect Element, Safari's Web Inspector) are available, or
    /// <see langword="null"/>, the default, for only when <c>DOTNET_ENVIRONMENT</c>, or else
    /// <c>ASPNETCORE_ENVIRONMENT</c>, is <c>Development</c>.
    /// </summary>
    /// <remarks>
    /// <c>dotnet runic dev</c> runs the application in Development. Developer tools give whoever uses them full access
    /// to the page and its Bridge. WKWebView supports them from macOS 13.3.
    /// </remarks>
    public bool? DevTools { get; init; }
}

/// <summary>Identifies a platform-window operation reported by a presentation.</summary>
/// <remarks>Open: a later minor release can add operations, so test for the flags you use.</remarks>
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

    /// <summary>
    /// The window can be hidden and shown again without closing it (<see cref="DesktopWindow.SetVisibleAsync"/>,
    /// <see cref="DesktopWindow.HideAsync"/> and <see cref="DesktopWindow.ShowAsync"/>). Every embedded window reports it.
    /// </summary>
    Visibility = 1 << 10,

    /// <summary>
    /// The window can show <see cref="DesktopHostOptions.MenuBar"/> natively, in the window or in a global menu bar
    /// (<see cref="DesktopWindow.MenuBarPlacement"/>). A window host factory that reports it in
    /// <see cref="IDesktopWindowHostFactory.Capabilities"/> keeps <see cref="DesktopHost.Validate"/> from reporting
    /// <c>menu-bar-unsupported</c>.
    /// </summary>
    MenuBar = 1 << 11,

    /// <summary>The window can show native context menus (<see cref="DesktopWindow.ShowContextMenuAsync"/>).</summary>
    ContextMenu = 1 << 12,

    /// <summary>
    /// The window can enter and leave full screen (<see cref="DesktopWindow.SetFullScreenAsync"/>), and opens full-screen
    /// from a <see cref="DesktopWindowPlacement.FullScreen"/> placement.
    /// </summary>
    FullScreen = 1 << 13,

    /// <summary>
    /// The window can be placed on a chosen monitor: <see cref="DesktopWindow.MoveToMonitorAsync"/> and
    /// <see cref="DesktopWindowOptions.MonitorId"/>. Wayland lets no application position its windows, so GTK windows
    /// there do not report it.
    /// </summary>
    MonitorPlacement = 1 << 14,

    /// <summary>
    /// The window can start native drags of files, text and URIs out of it (<see cref="DesktopWindow.StartDragAsync"/>).
    /// </summary>
    FileDrag = 1 << 15,
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

    /// <summary>
    /// Gets the presentations tried in order when the first one cannot open; empty by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window opens the first presentation, <see cref="Browser"/> (or the embedded WebView under
    /// <see cref="DesktopPresentationPolicy.EmbeddedThenBrowser"/>), and, when that is unavailable or fails to start,
    /// each entry of this list in turn, for example <c>[BrowserKind.ChromiumBased, BrowserKind.Firefox]</c> after the
    /// embedded WebView. Under <see cref="DesktopPresentationPolicy.EmbeddedThenBrowser"/> a non-empty list replaces
    /// the single fallback browser; <see cref="Browser"/> must then be <see cref="BrowserKind.Embedded"/>.
    /// </para>
    /// <para>
    /// A presentation that opened but whose page never connected (<c>connection-timeout</c>) ends the chain, because
    /// the next one would load the same frontend. When every presentation fails, the window fails with a
    /// <see cref="DesktopPresentationException"/> whose <see cref="DesktopPresentationException.Attempts"/> lists why
    /// each one failed. An installed browser after the first presentation opens without
    /// <see cref="AllowedPermissions"/> and without <see cref="ConfirmCloseAsync"/>, as the
    /// <see cref="DesktopPresentationPolicy.EmbeddedThenBrowser"/> fallback does; an embedded WebView keeps both.
    /// The other options apply to every presentation. An entry may not repeat the first presentation or another entry.
    /// </para>
    /// </remarks>
    public IReadOnlyList<BrowserKind> Fallbacks { get; init; } = [];

    /// <summary>Gets the sensitive permissions granted to the presentation; none by default.</summary>
    public DesktopPermissionGrant AllowedPermissions { get; init; }

    /// <summary>Gets the initial window width in pixels. Defaults to 800.</summary>
    public int Width { get; init; } = 800;

    /// <summary>Gets the initial window height in pixels. Defaults to 600.</summary>
    public int Height { get; init; } = 600;

    /// <summary>Gets the minimum window width in pixels, or <see langword="null"/> for no minimum.</summary>
    /// <remarks>Applies only together with <see cref="MinimumHeight"/>, and only to embedded windows.</remarks>
    public int? MinimumWidth { get; init; }

    /// <summary>Gets the minimum window height in pixels, or <see langword="null"/> for no minimum.</summary>
    /// <remarks>Applies only together with <see cref="MinimumWidth"/>, and only to embedded windows.</remarks>
    public int? MinimumHeight { get; init; }

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
    /// a position with only one coordinate is ignored, and a size above 65535 is reduced to it. On Linux the position
    /// is restored on X11 only: on Wayland the compositor places every window, so the GTK 3 and GTK 4 windows restore
    /// the size, maximized and full-screen state there and ignore the position, which Wayland cannot set; a placement
    /// saved on Wayland has none. An installed browser opens with the saved size and position as it would with
    /// <see cref="Width"/>, <see cref="Height"/>, <see cref="X"/> and <see cref="Y"/>, without fitting them to a monitor
    /// or maximizing; <see cref="DesktopHost.Validate"/> reports the ignored maximized state.
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
    /// <remarks>
    /// Embedded windows are created hidden; installed browsers run headless. <see cref="DesktopWindow.FocusAsync"/>
    /// shows a hidden embedded window and gives it focus; <see cref="DesktopWindow"/> has no other show or hide member.
    /// </remarks>
    public bool Hidden { get; init; }

    /// <summary>Gets whether the presentation opens in full-screen kiosk mode without window decorations.</summary>
    public bool Kiosk { get; init; }

    /// <summary>
    /// Gets whether the presentation reports a high-contrast theme to the page, or <see langword="null"/> to follow
    /// <see cref="DesktopPlatform.IsHighContrast"/>.
    /// </summary>
    public bool? HighContrast { get; init; }

    /// <summary>
    /// Gets the light or dark appearance of the embedded window, or <see cref="DesktopColorScheme.NoPreference"/>, the
    /// default, to follow the system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page's <c>prefers-color-scheme</c> and the window's own title bar and controls follow it. The macOS window sets
    /// <c>NSWindow.appearance</c> to Aqua or Dark Aqua. The Windows window sets the WebView2 profile's preferred color
    /// scheme and the title bar's dark mode; WebView2 applies the scheme to the whole profile, so windows that share a
    /// <see cref="ProfilePath"/> show the last one opened with a scheme. Without the option a Windows title bar stays light.
    /// </para>
    /// <para>
    /// The GTK windows and installed browsers cannot set it per window: they ignore it, and
    /// <see cref="DesktopHost.Validate"/> reports <c>color-scheme-unsupported</c>. A custom window host receives it in
    /// <see cref="DesktopWindowHostOptions.ColorScheme"/>.
    /// </para>
    /// </remarks>
    public DesktopColorScheme ColorScheme { get; init; }

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

    /// <summary>
    /// Gets what the embedded window does with navigation away from the application, new windows, downloads and
    /// developer tools. Installed browsers do not apply it.
    /// </summary>
    public DesktopWebContentPolicy WebContent { get; init; } = DesktopWebContentPolicy.Default;

    /// <summary>
    /// Gets where the window shows the menu bar of <see cref="DesktopHostOptions.MenuBar"/>:
    /// <see cref="DesktopMenuBarPresentation.Native"/>, the default, shows it natively.
    /// </summary>
    /// <remarks>
    /// <see cref="DesktopMenuBarPresentation.Page"/> leaves the bar to the page, which renders <see cref="DesktopHost.MenuBar"/>
    /// with its own components, and <see cref="DesktopMenuBarPresentation.None"/> shows no bar; with both the host
    /// still matches the items' shortcuts. A frameless or kiosk window shows no bar in the window.
    /// <see cref="DesktopWindow.MenuBarPlacement"/> reports where the bar went. An installed browser and a window host
    /// without <see cref="DesktopWindowCapabilities.MenuBar"/> show no native bar, and <see cref="DesktopHost.Validate"/>
    /// reports <c>menu-bar-unsupported</c> for a declared bar with this presentation.
    /// </remarks>
    public DesktopMenuBarPresentation MenuBarPresentation { get; init; }

    /// <summary>
    /// Gets the <see cref="Runic.Platform.DesktopMonitor.Id"/> of the monitor the embedded window opens on, centered on
    /// its work area, or <see langword="null"/>, the default, to let the platform choose.
    /// </summary>
    /// <remarks>
    /// Takes the place of <see cref="X"/>, <see cref="Y"/> and <see cref="Centered"/>. A <see cref="Placement"/> with a
    /// position decides instead, so a restored window opens where it was. A monitor that is not connected leaves the
    /// choice to the platform, and a window shrinks to fit the monitor's work area. Requires
    /// <see cref="DesktopWindowCapabilities.MonitorPlacement"/>; on Wayland the GTK 4 window still opens a
    /// <see cref="DesktopWindowPlacement.FullScreen"/> placement on this monitor. Installed browsers ignore it.
    /// </remarks>
    public string? MonitorId { get; init; }

    /// <summary>
    /// Gets the direction of the embedded window's native parts: <see cref="Runic.Platform.DesktopLayoutDirection.Default"/>,
    /// the default, keeps the platform's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Runic.Platform.DesktopLayoutDirection.RightToLeft"/> mirrors the Windows window: its title bar, caption
    /// buttons and system menu (<c>WS_EX_LAYOUTRTL</c>); the WebView2 page keeps its own layout, which it sets with
    /// <c>&lt;html dir="rtl"&gt;</c>. On Linux it sets the direction of the GTK window and of Runic's widgets in it. macOS
    /// ignores it: AppKit lays windows out for the application bundle's localization.
    /// </para>
    /// <para>
    /// Choose it with the application's language, for example
    /// <c>LayoutDirection = culture.TextInfo.IsRightToLeft ? DesktopLayoutDirection.RightToLeft : DesktopLayoutDirection.LeftToRight</c>.
    /// Installed browsers ignore it.
    /// </para>
    /// </remarks>
    public Runic.Platform.DesktopLayoutDirection LayoutDirection { get; init; }

    /// <summary>
    /// Gets the BCP 47 language of the embedded web view's own text, such as <c>ar-EG</c>, or <see langword="null"/>, the
    /// default, for the platform's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The language of the web view's built-in text, such as its context menu and form validation messages, its
    /// <c>navigator.language</c> and the <c>Accept-Language</c> it sends: WebView2's environment language on Windows and
    /// WebKitGTK's preferred languages on Linux. WebView2 takes the closest language it ships, such as <c>ar</c> for
    /// <c>ar-EG</c>, for its own text and <c>navigator.language</c>. macOS ignores it: WKWebView uses the application's
    /// preferred languages. A page still sets its own <c>lang</c>. Installed browsers ignore it.
    /// </para>
    /// <para>
    /// WebView2 requires every window sharing a <see cref="ProfilePath"/> to use the same language, as for
    /// <see cref="BrowserArguments"/>: a second window with another language fails to open. Windows without a profile
    /// path get their own temporary profile.
    /// </para>
    /// </remarks>
    public string? Language { get; init; }

    /// <summary>
    /// Gets whether the embedded window's spell checker marks misspelled words in the page's editable fields;
    /// <see langword="true"/>, the default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With the default, every platform checks the fields the HTML <c>spellcheck</c> attribute selects: text areas,
    /// text inputs and <c>contenteditable</c> elements, unless they or an ancestor have <c>spellcheck="false"</c>.
    /// WebView2 and WKWebView do so on their own; on Linux, Runic turns WebKitGTK's spell checker on. The languages are
    /// the application's, <see cref="DesktopHostOptions.SpellCheckingLanguages"/>.
    /// </para>
    /// <para>
    /// <see langword="false"/> keeps the native checker off in this window, for example for an editor that draws its own
    /// spelling marks: every document in the window starts with <c>spellcheck="false"</c> on its root element, which the
    /// page's fields inherit. An element with its own <c>spellcheck="true"</c> is still checked. WebView2, WKWebView and
    /// WebKitGTK have no switch for one window (the checkers of WebKitGTK and macOS belong to the process), so every
    /// platform applies the option this way. A window host applies it when it runs
    /// <see cref="DesktopWindowHostOptions.DocumentStartScript"/>, as every built-in host does. Installed browsers ignore it.
    /// </para>
    /// </remarks>
    public bool SpellChecking { get; init; } = true;

    /// <summary>Gets additional command-line arguments for the browser or WebView2 runtime.</summary>
    /// <remarks>
    /// The string is split like a command line and appended to the launch arguments. When set, installed browsers
    /// no longer receive Runic Desktop's default isolation flags. The macOS and GTK 3 embedded windows ignore it.
    /// </remarks>
    public string? BrowserArguments { get; init; }

    /// <summary>
    /// Gets the macOS window's title bar options: a hidden or transparent title bar that keeps the traffic lights, and
    /// where they sit. Other platforms and installed browsers ignore them.
    /// </summary>
    public MacOSWindowOptions MacOS { get; init; } = new();

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
            Width = placement.Width <= 0 ? Width : Math.Min(placement.Width, Internal.WindowPlacementClamp.MaximumDimension),
            Height = placement.Height <= 0 ? Height : Math.Min(placement.Height, Internal.WindowPlacementClamp.MaximumDimension),
            X = hasPosition ? placement.X : null,
            Y = hasPosition ? placement.Y : null,
            Centered = false,
            // A restored position decides where the window opens.
            MonitorId = hasPosition ? null : MonitorId,
        };
    }
}
