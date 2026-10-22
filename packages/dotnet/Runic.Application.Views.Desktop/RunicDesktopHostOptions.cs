using Runic.Desktop;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// The options of a <see cref="RunicDesktopHost"/>'s Windows: their Desktop window and surface options, resources and
/// saved placement. Pass them to
/// <see cref="RunicDesktopHost.Run(IServiceProvider, DesktopHostOptions, RunicDesktopHostOptions, Func{RunicDesktopHost, Task{int}})"/>
/// or the constructor.
/// </summary>
/// <remarks>
/// <code>
/// return RunicDesktopHost.Run(provider, new DesktopHostOptions(), new RunicDesktopHostOptions
/// {
///     Placement = window =&gt; window is MainWindow ? settings.MainWindowPlacement : null,
/// }, async host =&gt; { ... });
/// </code>
/// The host reads the options when it is created. Application menu bar commands are application-wide, not per host:
/// see <see cref="RunicDesktopHost.UseApplicationMenu"/>.
/// </remarks>
public sealed record RunicDesktopHostOptions
{
    /// <summary>
    /// The Desktop options of every window; <see cref="RunicDesktopHost.DefaultWindowOptions"/> by default.
    /// <see cref="RunicWindowOptions.Width"/> and <see cref="RunicWindowOptions.Height"/> replace their width and
    /// height when set.
    /// </summary>
    public DesktopWindowOptions WindowOptions { get; init; } = RunicDesktopHost.DefaultWindowOptions;

    /// <summary>
    /// The surface options of every window, or <see langword="null"/>, the default, to serve
    /// <see cref="RunicWindowOptions.ContentRoot"/> and open <see cref="RunicWindowOptions.StartPage"/>.
    /// </summary>
    public DesktopSurfaceOptions? SurfaceOptions { get; init; }

    /// <summary>
    /// Chooses the resources of each Window: application files outside the frontend build, such as icons from a
    /// cache, that the Window's page loads from its own address. <see langword="null"/>, the default, serves none.
    /// </summary>
    /// <remarks>
    /// Called with each Window as it opens, once its ViewModel is set (<see cref="RunicWindow{TViewModel}.ViewModel"/>)
    /// and before its surface is created. It returns the handler for the requests the Window's content does not serve,
    /// or <see langword="null"/> for none, and it becomes <see cref="DesktopSurfaceOptions.Resources"/> of the Window's
    /// surface, replacing the one of <see cref="SurfaceOptions"/>. The resources are served whether the Window is
    /// shown or served to a browser under <c>RUNIC_APPLICATION_SERVE_ONLY</c>, for as long as the Window is open.
    /// <code>
    /// new RunicDesktopHostOptions
    /// {
    ///     Resources = window =&gt; window is DashboardWindow ? icons.ServeAsync : null,
    /// }
    /// </code>
    /// A handler that throws fails the request with 500, and a callback that throws fails the open.
    /// </remarks>
    public Func<IRunicWindow, ContentHandler?>? Resources { get; init; }

    /// <summary>
    /// Chooses the saved placement each Window restores: its size, position and maximized state from the last time it
    /// closed. <see langword="null"/>, the default, restores none.
    /// </summary>
    /// <remarks>
    /// Called with each Window as it opens, once its ViewModel is set, before its native window opens. It returns
    /// the placement saved from <see cref="DesktopPlatformServices.GetWindowState(IRunicWindow)"/>, or
    /// <see langword="null"/> for none, and it becomes <see cref="DesktopWindowOptions.Placement"/> of the Window,
    /// replacing the size of <see cref="WindowOptions"/> and <see cref="RunicWindowOptions"/>. The embedded window
    /// keeps a restored placement on a monitor that exists now; see <see cref="DesktopWindowOptions.Placement"/>.
    /// <code>
    /// return RunicDesktopHost.Run(provider, new DesktopHostOptions(), new RunicDesktopHostOptions
    /// {
    ///     Placement = window =&gt; window is MainWindow ? settings.MainWindowPlacement : null,
    /// }, async host =&gt;
    /// {
    ///     await using var window = await host.OpenWindowAsync&lt;MainWindow&gt;();
    ///     await window.WaitForCloseAsync();
    ///     if (DesktopPlatformServices.GetWindowState(window) is PlatformResult&lt;DesktopWindowState&gt;.Success { Value: var state })
    ///         settings.MainWindowPlacement = state.Placement;
    ///     return 0;
    /// });
    /// </code>
    /// A placement saved on one machine or operating system is not meant for another; see
    /// <see cref="DesktopWindowPlacement"/>. A callback that throws fails the open.
    /// </remarks>
    public Func<IRunicWindow, DesktopWindowPlacement?>? Placement { get; init; }

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
    /// The <c>Content-Security-Policy</c> that every Window's surface sends with its HTML
    /// (<see cref="DesktopSurfaceOptions.ContentSecurityPolicy"/>), or <see langword="null"/>, the default, for none.
    /// </summary>
    /// <remarks>
    /// It applies to the default surface options and to <see cref="SurfaceOptions"/> that set no policy of their own.
    /// The policy is a header of its own next to the surface's <c>frame-ancestors</c> policy; both are enforced. The
    /// Bridge delivers its messages as host scripts, which the page evaluates with the <c>Function</c> constructor, so
    /// the policy must allow <c>'self'</c> scripts and connections and <c>'unsafe-eval'</c>.
    /// <code>
    /// ContentSecurityPolicy = "default-src 'self'; script-src 'self' 'unsafe-eval'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'"
    /// </code>
    /// </remarks>
    /// <exception cref="ArgumentException">The value is empty or contains a character that a header cannot carry.</exception>
    public string? ContentSecurityPolicy
    {
        get;
        init => field = new DesktopSurfaceOptions { ContentSecurityPolicy = value }.ContentSecurityPolicy;
    }
}
