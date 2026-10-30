using CsWebUi;
using Runic.Platform;

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

    /// <summary>
    /// The largest Bridge input, in UTF-8 bytes, of Windows whose <see cref="RunicWindowOptions.MaximumInputBytes"/>
    /// is <see langword="null"/> (<see cref="WindowContentSessionOptions.MaximumInputBytes"/>), between 1 KiB and
    /// 64 MiB. <see langword="null"/>, the default, is 1 MiB. The host reads it when each Window opens.
    /// </summary>
    public int? MaximumInputBytes { get; init; }

    /// <summary>
    /// The menus of each window's menu bar, which the page renders, since a CS-WebUI window has no native bar; empty, the
    /// default, for none. <see cref="RunicCsWebUiHost.SetMenuBar"/> replaces them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The menus follow the rules of Runic Desktop's <c>DesktopHostOptions.MenuBar</c>, and the page reads them, as a
    /// Runic Desktop window with <c>DesktopMenuBarPresentation.Page</c> does, through <c>createMenuBarController</c> of
    /// <c>@runic-artifex/views</c> or the framework adapters' <c>useMenuBar</c> and <c>injectMenuBar</c>, with its own
    /// menubar component. Its <c>placement</c> is <c>"page"</c> while there are menus, on every platform.
    /// </para>
    /// <para>
    /// Each <see cref="DesktopMenuItem"/> names a command of the Window's root ViewModel
    /// (<see cref="IRunicMenuCommandSource"/>): the item is enabled while its <see cref="DesktopMenuItem.IsEnabled"/> and
    /// the command's <c>CanExecute(CommandParameter)</c> are <see langword="true"/>, following <c>CanExecuteChanged</c>, and
    /// runs <c>Execute(CommandParameter)</c> in the ViewModel's model turn. WebUI reports no keys to .NET, so the page
    /// matches the items' shortcuts (<c>shortcuts: "page"</c>). A failing command is logged as event 2102,
    /// <c>CsWebUiMenuCommandFailed</c>.
    /// </para>
    /// <para>
    /// Creating the host throws <see cref="ArgumentException"/> for a blank title or one longer than 128 characters, a
    /// command id outside 1 to 64 ASCII letters, digits, dots, underscores or hyphens or starting with <c>runic.</c>, or a
    /// second Application, File or Help menu.
    /// </para>
    /// </remarks>
    public IReadOnlyList<DesktopMenu> MenuBar { get; init; } = [];

    /// <summary>
    /// The frontend every Window serves in place of its web root, or <see langword="null"/>, the default, for the
    /// archive that the build embedded with <c>RunicApplicationFrontendDelivery=Embedded</c>, if any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Window serves this source, or the build's archive, unless its <see cref="RunicWindowOptions.ContentRoot"/> is
    /// set; nothing is extracted to disk. The window's <see cref="WebUiWindow.SetFileHandler"/> answers each request,
    /// and WebUI still serves <c>webui.js</c>. The source must declare <see cref="RunicWindowOptions.StartPage"/>, or
    /// opening the Window throws <see cref="FileNotFoundException"/>, and must hold everything the build puts in
    /// <c>www/</c>, including the host's <c>runic-views.js</c>. Paths resolve as <see cref="Runic.Assets.AssetRoutingOptions"/>
    /// defaults do, and a path the source does not declare is answered with 404, never from a folder. The build's
    /// archive is the entry assembly's manifest resource that the <c>Runic.Application.FrontendArchive</c> runtime
    /// option names, read once per process.
    /// </para>
    /// <para>
    /// WebUI turns its authentication cookie off for the whole process when a window installs a file handler; the host
    /// turns it back on after installing its own, so a window stays bound to the first browser that loaded its
    /// <c>webui.js</c>, as with a folder. An application that installs a file handler of its own through
    /// <see cref="ConfigureWindow"/> or <see cref="ShowWindow"/> replaces the host's.
    /// </para>
    /// <code>
    /// Assets = AssetArchive.ReadEmbedded(typeof(Program).Assembly, "Frontend.runic-assets").Subdirectory("settings")
    /// </code>
    /// </remarks>
    public Runic.Assets.IAssetSource? Assets { get; init; }
}
