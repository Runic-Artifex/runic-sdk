using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Desktop;
using Runic.Navigation;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// Opens Runic Windows in native Runic Desktop windows with the embedded WebView, falling back to an installed
/// browser. Start it with <see cref="Run(IServiceProvider, DesktopHostOptions, RunicDesktopHostOptions, Func{RunicDesktopHost, Task{int}})"/>
/// and open Windows with <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/>:
/// <code>
/// return RunicDesktopHost.Run(provider, async host =&gt;
/// {
///     await using var window = await host.OpenWindowAsync&lt;MainWindow&gt;();
///     await window.WaitForCloseAsync();
///     return 0;
/// });
/// </code>
/// Replacing <c>RunicDesktopHost</c> with <c>RunicCsWebUiHost</c> (package <c>Runic.Application.Views.CsWebUi</c>) runs the
/// same application on CS-WebUI.
/// </summary>
/// <remarks>
/// The application runs as long as the body passed to <c>Run</c>: closing the last Window does not end it, and a body
/// may run without any Window, for example behind a status icon. To keep one copy running, claim
/// <c>ApplicationInstance.ClaimAsync</c> before <c>Run</c> and bring an open Window forward with
/// <see cref="DesktopPlatformServices.PresentAsync(IRunicWindow, Runic.Platform.DesktopActivationContext?, CancellationToken)"/> when another launch arrives, or open one for it with
/// <see cref="OpenWindowAsync{TWindow}(RunicWindowOptions?, DesktopActivationContext?, CancellationToken)"/>.
/// When the <c>RUNIC_APPLICATION_SERVE_ONLY</c> environment variable is <c>1</c>, as on CS-WebUI, no window or browser
/// is opened: each window creates its surface, writes <c>RUNIC_APPLICATION_URL=&lt;url&gt;</c> to standard output, and
/// keeps serving until a line is read from standard input, shutdown is requested or the process ends; then
/// <see cref="RunicWindow{TViewModel}.WaitForCloseAsync"/> returns. A line ends every Window serving when it is read; a
/// Window opened after it serves until the next line. The surface admits one browser, a guarded Window
/// opens unguarded (event 1053) and platform services, including
/// <see cref="DesktopPlatformServices.PresentAsync(IRunicWindow, Runic.Platform.DesktopActivationContext?, CancellationToken)"/>,
/// report <c>OwnerUnavailable</c>. This switch takes precedence
/// over <c>RUNIC_APPLICATION_CLOSE_AFTER_OPEN</c>. <see cref="RunicWindowHostExtensions.ValidateWindow{TWindow}"/>
/// makes the same decision: it skips the findings of <see cref="DesktopHost.Validate"/> about what this machine lacks
/// to present a window, such as <c>graphical-session-missing</c>, because no presentation opens, and still reports
/// configuration errors such as <c>window-option-invalid</c>.
/// When the <c>RUNIC_APPLICATION_CLOSE_AFTER_OPEN</c> environment variable is <c>1</c>, every window opened through
/// this host is closed as soon as it has opened (and, with <see cref="DesktopHostOptions.WaitForConnection"/>, its
/// bridge has connected). Before closing, a warning is written to standard error (and logged as event 2002 when an
/// <c>ILoggerFactory</c> is registered) and <c>RUNIC_APPLICATION_OPENED=&lt;presentation&gt;</c>, such as
/// <c>Embedded</c> or <c>Chrome</c>, is written to standard output. It is an automation switch for applications
/// shaped like the <c>runic-app</c> template, which exit when their one window's
/// <see cref="RunicWindow{TViewModel}.WaitForCloseAsync"/> returns; other applications may keep running or reopen windows.
/// When <c>dotnet runic dev</c> runs without a development server, it sets <c>RUNIC_APPLICATION_DEVELOPMENT_ASSETS</c> to
/// the frontend output directory. In a <c>Development</c> environment, and when <see cref="SurfaceOptions"/> is not
/// set, every window serves that directory before its content root and reloads when the directory changes.
/// </remarks>
public sealed class RunicDesktopHost : IRunicWindowHost
{
    /// <summary>
    /// The window options <see cref="WindowOptions"/> starts from: the embedded WebView, falling back to an installed
    /// browser (<see cref="DesktopPresentationPolicy.EmbeddedThenBrowser"/>).
    /// </summary>
    public static DesktopWindowOptions DefaultWindowOptions { get; } = new()
    {
        Browser = BrowserKind.Embedded,
        PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
    };

    /// <summary>Creates a host that opens Windows from <paramref name="services"/> on <paramref name="desktop"/>.</summary>
    /// <remarks>
    /// <see cref="Run(IServiceProvider, DesktopHostOptions, RunicDesktopHostOptions, Func{RunicDesktopHost, Task{int}})"/>
    /// creates the host for you. Use this constructor inside
    /// <see cref="DesktopEventLoop.Run(DesktopHostOptions, Func{DesktopHost, Task{int}})"/> or with a
    /// <see cref="DesktopHost"/> you started yourself, for example for a second host on the same Desktop host.
    /// </remarks>
    public RunicDesktopHost(IServiceProvider services, DesktopHost desktop)
        : this(services, desktop, DefaultOptions)
    {
    }

    /// <summary>
    /// Creates a host that opens Windows from <paramref name="services"/> on <paramref name="desktop"/> with
    /// <paramref name="options"/>.
    /// </summary>
    /// <param name="services">The application's service provider.</param>
    /// <param name="desktop">The Desktop host that presents the windows.</param>
    /// <param name="options">The window, surface, resource and placement options of this host's Windows.</param>
    /// <inheritdoc cref="RunicDesktopHost(IServiceProvider, DesktopHost)" path="/remarks"/>
    public RunicDesktopHost(IServiceProvider services, DesktopHost desktop, RunicDesktopHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.WindowOptions);
        Services = services;
        Desktop = desktop;
        WindowOptions = options.WindowOptions;
        SurfaceOptions = options.SurfaceOptions;
        Resources = options.Resources;
        Placement = options.Placement;
        _includeFailureDetail = options.IncludeFailureDetail;
        _contentSecurityPolicy = options.ContentSecurityPolicy;
        _assets = options.Assets;
        // The range of WindowContentSessionOptions.MaximumInputBytes, checked before a Window opens.
        if (options.MaximumInputBytes is < 1_024 or > 67_108_864)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaximumInputBytes,
                "MaximumInputBytes must be between 1 024 and 67 108 864 bytes.");
        _maximumInputBytes = options.MaximumInputBytes;
    }

    private readonly bool? _includeFailureDetail;
    private readonly int? _maximumInputBytes;
    private readonly string? _contentSecurityPolicy;
    private readonly Runic.Assets.IAssetSource? _assets;

    // A Window's own choice of Bridge failure detail, or else the application's default.
    internal bool? IncludeFailureDetailFor(RunicWindowOptions options) => options.IncludeFailureDetail ?? _includeFailureDetail;

    private static readonly RunicDesktopHostOptions DefaultOptions = new();

    /// <inheritdoc />
    public IServiceProvider Services { get; }

    /// <summary>The Runic Desktop host that presents the windows.</summary>
    public DesktopHost Desktop { get; }

    /// <summary>The Desktop options of every window: <see cref="RunicDesktopHostOptions.WindowOptions"/>.</summary>
    /// <remarks>
    /// <see cref="RunicWindowOptions.Width"/> and <see cref="RunicWindowOptions.Height"/> replace their width and
    /// height when set.
    /// </remarks>
    public DesktopWindowOptions WindowOptions { get; }

    /// <summary>The surface options of every window: <see cref="RunicDesktopHostOptions.SurfaceOptions"/>.</summary>
    public DesktopSurfaceOptions? SurfaceOptions { get; }

    /// <summary>Chooses the resources of each Window: <see cref="RunicDesktopHostOptions.Resources"/>.</summary>
    public Func<IRunicWindow, ContentHandler?>? Resources { get; }

    /// <summary>Chooses the saved placement each Window restores: <see cref="RunicDesktopHostOptions.Placement"/>.</summary>
    public Func<IRunicWindow, DesktopWindowPlacement?>? Placement { get; }

    private static readonly object ApplicationMenuGate = new();

    /// <summary>
    /// Runs the application's menu bar commands by command id, such as New or Settings…, when no key window's root
    /// ViewModel (<see cref="IRunicMenuCommandSource"/>) runs the id, until the returned object is disposed.
    /// </summary>
    /// <param name="commands">The commands by command id of <see cref="DesktopHost.MenuBar"/>.</param>
    /// <returns>Disposing it stops running the commands and releases their <c>CanExecuteChanged</c> subscriptions.</returns>
    /// <remarks>
    /// <para>
    /// The commands are application-wide: they become <see cref="DesktopHost.MenuCommands"/> of <see cref="Desktop"/>,
    /// which every host on that Desktop host shares. Each command's availability follows its
    /// <see cref="System.Windows.Input.ICommand.CanExecute"/> with a <see langword="null"/> parameter, and it runs on the
    /// thread pool: an application command has no ViewModel turn. A failure is logged (event 2004).
    /// </para>
    /// <code>
    /// return RunicDesktopHost.Run(provider, new DesktopHostOptions { MenuBar = menus }, async host =&gt;
    /// {
    ///     using var menu = host.UseApplicationMenu(new Dictionary&lt;string, ICommand&gt; { ["file.new"] = newWindowCommand });
    ///     await using var window = await host.OpenWindowAsync&lt;MainWindow&gt;();
    ///     await window.WaitForCloseAsync();
    ///     return 0;
    /// });
    /// </code>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Desktop"/> already has application menu commands, from another call that is not disposed yet or set
    /// directly on <see cref="DesktopHost.MenuCommands"/>.
    /// </exception>
    public IDisposable UseApplicationMenu(IReadOnlyDictionary<string, System.Windows.Input.ICommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var copy = new Dictionary<string, System.Windows.Input.ICommand>(commands, StringComparer.Ordinal);
        var logger = (Services.GetService<ILoggerFactory>()?.CreateLogger("Runic.Application.Views.Desktop"))
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        lock (ApplicationMenuGate)
        {
            if (Desktop.MenuCommands is not null)
            {
                throw new InvalidOperationException(
                    "The Desktop host already has application menu commands. Dispose the previous UseApplicationMenu result first.");
            }
            // Without a model context the commands resolve before this returns; nothing waits on the binding.
            var binding = MenuCommandBinding.Bind(Desktop, id => copy.GetValueOrDefault(id), logger);
            Desktop.MenuCommands = binding;
            return new ApplicationMenu(Desktop, binding);
        }
    }

    private sealed class ApplicationMenu(DesktopHost desktop, MenuCommandBinding binding) : IDisposable
    {
        public void Dispose()
        {
            lock (ApplicationMenuGate)
            {
                if (ReferenceEquals(desktop.MenuCommands, binding)) desktop.MenuCommands = null;
            }
            binding.Dispose();
        }
    }

    /// <summary>Gets the Desktop surface that serves an open Window of this host, or <see langword="null"/> for another host.</summary>
    /// <param name="window">The open Window.</param>
    /// <remarks>
    /// The surface is the same in a native window, in a browser after a fallback and under
    /// <c>RUNIC_APPLICATION_SERVE_ONLY</c>. Its <see cref="DesktopSurface.ConnectionCount"/> and
    /// <see cref="DesktopSurface.SubscribeConnectionEvents"/> tell when the last
    /// browser tab of a served Window closed, and <see cref="DesktopSurface.Url"/> is its address. It is the
    /// <see cref="DesktopBridgeWindow{TViewModel}.Surface"/> of <see cref="IRunicWindow.Host"/>, without naming the
    /// ViewModel type.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The Window has not opened.</exception>
    public static DesktopSurface? GetSurface(IRunicWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Host is IDesktopNativeOwnerSource source ? source.Surface : null;
    }

    /// <summary>Runs <paramref name="application"/> with the default <see cref="DesktopHostOptions"/>.</summary>
    /// <inheritdoc cref="Run(IServiceProvider, DesktopHostOptions, RunicDesktopHostOptions, Func{RunicDesktopHost, Task{int}})"/>
    public static int Run(IServiceProvider services, Func<RunicDesktopHost, Task<int>> application) =>
        Run(services, new DesktopHostOptions(), DefaultOptions, application);

    /// <summary>Runs <paramref name="application"/> with the default <see cref="RunicDesktopHostOptions"/>.</summary>
    /// <inheritdoc cref="Run(IServiceProvider, DesktopHostOptions, RunicDesktopHostOptions, Func{RunicDesktopHost, Task{int}})"/>
    public static int Run(IServiceProvider services, DesktopHostOptions options, Func<RunicDesktopHost, Task<int>> application) =>
        Run(services, options, DefaultOptions, application);

    /// <summary>
    /// Starts a Desktop host with <paramref name="options"/> and runs <paramref name="application"/> on the event
    /// loop this platform and backend need.
    /// </summary>
    /// <remarks>
    /// Call it from the process entry point before any <see langword="await"/> and return its result as the exit code;
    /// see <see cref="DesktopEventLoop.Run(DesktopHostOptions, Func{DesktopHost, Task{int}})"/>. Ctrl+C and SIGTERM close
    /// the open windows, so <see cref="RunicWindow{TViewModel}.WaitForCloseAsync"/> returns, and cancel
    /// <see cref="DesktopHost.ShutdownRequested"/>. A body that runs without a window, for example behind a status icon,
    /// sets <see cref="DesktopHostOptions.HandlesShutdownWithoutWindow"/> and ends when
    /// <c>host.Desktop.ShutdownRequested</c> is cancelled; without that option, a signal that arrives while no Window is
    /// open or served ends the process with its default action. Dispose <paramref name="services"/> after this method returns.
    /// When <see cref="DesktopHostOptions.LoggerFactory"/> is not set, the host logs through the
    /// <c>ILoggerFactory</c> registered in <paramref name="services"/>, if any, until this method returns; a host
    /// message after that, such as a browser profile that still cannot be deleted, goes to <c>Trace</c>.
    /// </remarks>
    /// <param name="services">The application's service provider.</param>
    /// <param name="options">The Desktop host options, for example <c>new DesktopHostOptions().WithGtk4()</c>.</param>
    /// <param name="hostOptions">The window, surface, resource and placement options of the host's Windows.</param>
    /// <param name="application">The application. Its result becomes the return value.</param>
    /// <returns>The value returned by <paramref name="application"/>.</returns>
    public static int Run(IServiceProvider services, DesktopHostOptions options, RunicDesktopHostOptions hostOptions,
        Func<RunicDesktopHost, Task<int>> application)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hostOptions);
        ArgumentNullException.ThrowIfNull(hostOptions.WindowOptions);
        ArgumentNullException.ThrowIfNull(application);
        // The Desktop host logs shutdown, close confirmation, configuration and browser-launch messages through
        // the application's logging, as the Views in its windows do; it writes them to Trace only without one.
        // The factory is lent for the run: later messages go to Trace, since the caller then disposes services.
        var lent = options.LoggerFactory is null ? LentLoggerFactory.From(services.GetService<ILoggerFactory>()) : null;
        if (lent is not null) options = options with { LoggerFactory = lent };
        try { return DesktopEventLoop.Run(options, desktop => application(new RunicDesktopHost(services, desktop, hostOptions))); }
        finally { lent?.Return(); }
    }

    /// <summary>
    /// Opens <typeparamref name="TWindow"/> as <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/> does,
    /// and asks the desktop to give the new window focus with <paramref name="activation"/>.
    /// </summary>
    /// <remarks>
    /// Open a Window for a launch with the launch's activation context, such as
    /// <see cref="ApplicationLaunch.PlatformContext"/> of a launch the running instance received. Wayland
    /// compositors such as KWin give a new window focus only with the activation token of the launch it answers;
    /// without it, the window opens behind the active one and only asks for attention. The window is presented with
    /// the context as soon as it is shown, as
    /// <see cref="DesktopPlatformServices.PresentAsync(DesktopNativeOwner, DesktopActivationContext?, CancellationToken)"/>
    /// does; the desktop decides whether to grant focus. Nothing is presented for a <see langword="null"/> context, a
    /// hidden window (<see cref="DesktopWindowOptions.Hidden"/>), a window served under
    /// <c>RUNIC_APPLICATION_SERVE_ONLY</c> or an installed browser after an embedded-window fallback. The activation
    /// follows the options, so a call with one argument still opens through
    /// <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/>.
    /// </remarks>
    /// <param name="options">The content and size; <see langword="null"/> opens <c>www/index.html</c> next to the application.</param>
    /// <param name="activation">The desktop's short-lived activation data. Use it once, promptly.</param>
    /// <param name="cancellationToken">Cancels opening. A cancelled open releases everything it created.</param>
    /// <returns>The open Window.</returns>
    /// <exception cref="RunicWindowConfigurationException">A check failed before anything opened.</exception>
    public async ValueTask<TWindow> OpenWindowAsync<TWindow>(RunicWindowOptions? options,
        DesktopActivationContext? activation, CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow, new()
    {
        var window = await RunicWindowHostExtensions.OpenWindowAsync<TWindow>(this, options, cancellationToken)
            .ConfigureAwait(false);
        if (activation is not null && !WindowOptions.Hidden &&
            window.Host is IDesktopNativeOwnerSource { NativeOwnerOrNull: { IsAvailable: true } owner })
        {
            // A presentation request the desktop may refuse; the open Window stands either way, so the result,
            // such as a window the user already closed, is not an open failure.
            try { _ = await Present(owner, activation, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        return window;
    }

    // DesktopPlatformServices.PresentAsync; tests observe the request instead.
    internal Func<DesktopNativeOwner, DesktopActivationContext?, CancellationToken, ValueTask<PlatformResult<PlatformUnit>>> Present
    { get; init; } = DesktopPlatformServices.PresentAsync;

    IReadOnlyList<RunicWindowDiagnostic> IRunicWindowHost.Validate(RunicWindowOptions options)
    {
        var windowOptions = WindowOptionsFor(options);
        if (!ServeOnlyRequested) return [.. Desktop.Validate(windowOptions).Diagnostics.Select(ToWindowDiagnostic)];
        // Validation makes the serve-only decision of OpenAsync (#238): a serve-only window has no presentation, so
        // what this machine lacks to present one does not apply. The configuration is still checked, since it fails
        // on every launch that presents a window. The preflight runs DesktopHost.Validate's checks without logging the
        // skipped findings as errors.
        try
        {
            var preflight = Desktop.GetPresentationPreflight(windowOptions);
            // A browser fallback that is missing only from this machine stands in for an installed one: with it, the
            // preferred presentation's findings, such as linux-embedded-backend-not-selected, would not be reported
            // either, and serve-only needs no browser.
            var presentation = preflight.Fallback is { } fallback &&
                fallback.Diagnostics.All(static diagnostic => PresentationEnvironmentCodes.Contains(diagnostic.Code))
                ? []
                : preflight.Diagnostics;
            return [.. presentation.Concat(preflight.OptionDiagnostics)
                .Where(static diagnostic => !PresentationEnvironmentCodes.Contains(diagnostic.Code))
                .Select(ToWindowDiagnostic)];
        }
        catch (ArgumentException error)
        {
            // DesktopHost.Validate reports an invalid option with this code.
            return [new RunicWindowDiagnostic(InvalidOptionCode, error.Message, "Correct the DesktopWindowOptions value.")];
        }
    }

    private const string InvalidOptionCode = "window-option-invalid";

    // The Runic Desktop codes for a presentation prerequisite this machine or process lacks: a runtime, a display, an
    // installed browser, the main thread. Everything else, such as an invalid option, an unsupported browser or a
    // missing provider reference, is the application's configuration.
    private static readonly System.Collections.Frozen.FrozenSet<string> PresentationEnvironmentCodes =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            "browser-not-found",
            "custom-window-host-unavailable",
            "embedded-platform-unsupported",
            "event-loop-unavailable",
            "graphical-session-missing",
            "gtk4-display-unavailable",
            "gtk4-runtime-missing",
            "linux-embedded-backend-conflict",
            "macos-main-thread-required",
            "presentation-unavailable",
            "webkit-runtime-missing",
            "webkitgtk-runtime-missing",
            "webkitgtk6-runtime-missing",
            "webview2-loader-unavailable",
            "webview2-runtime-missing",
        ], StringComparer.Ordinal);

    async ValueTask<IBridgeWindow> IRunicWindowHost.OpenAsync<TViewModel>(RunicWindowOpening<TViewModel> opening,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(opening);
        var scope = opening.Scope;
        DesktopSurface? surface = null;
        DesktopBridgeTransport? transport = null;
        WindowContentSession? content = null;
        IDisposable? connectionBinding = null;
        DesktopBridgeWindow<TViewModel>? owner = null;
        DevelopmentAssets.Window? development = null;
        try
        {
            var viewModel = opening.ViewModel;
            var loggerFactory = scope.ServiceProvider.GetService<ILoggerFactory>();
            var contentRoot = opening.Options.ResolveContentRoot();
            // dotnet runic dev without a development server: the frontend output directory, live (DevelopmentAssets).
            if (DevelopmentAssets.ResolveDirectory(ReadEnvironment, SurfaceOptions is not null) is { } live)
                development = _developmentAssets.Open(live, opening.Options.StartPage, contentRoot,
                    (loggerFactory?.CreateLogger("Runic.Application.Views.Desktop"))
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            // Then RunicDesktopHostOptions.Assets or the build's embedded frontend, unless the Window names a content root.
            var archive = SurfaceOptions is null && development is null && opening.Options.ContentRoot is null
                ? _assets ?? FrontendArchive.FromBuild
                : null;
            var surfaceOptions = SurfaceOptions ?? new DesktopSurfaceOptions
            {
                Content = development?.Content
                    ?? (archive is not null ? FrontendArchive.CreateContent(archive, opening.Options.StartPage) : null)
                    ?? new DesktopContent.Directory(contentRoot, opening.Options.StartPage),
            };
            if (Resources is { } chooseResources && chooseResources(opening.Window) is { } resources)
                surfaceOptions = surfaceOptions with { Resources = resources };
            if (surfaceOptions.ContentSecurityPolicy is null && _contentSecurityPolicy is not null)
                surfaceOptions = surfaceOptions with { ContentSecurityPolicy = _contentSecurityPolicy };
            surface = await Desktop.CreateSurfaceAsync(surfaceOptions, cancellationToken).ConfigureAwait(false);
            development?.Attach(surface);
            transport = new DesktopBridgeTransport(surface, loggerFactory?.CreateLogger(DesktopBridgeTransport.LogCategory));
            content = new WindowContentSession(transport, new WindowContentSessionOptions
            {
                ViewLocator = scope.ServiceProvider.GetService<IRunicViewLocator>(),
                RootModel = viewModel,
                ModelContext = scope.ServiceProvider.GetService<IRunicModelContext>(),
                LoggerFactory = loggerFactory,
                IncludeFailureDetail = IncludeFailureDetailFor(opening.Options),
                MaximumInputBytes = opening.Options.MaximumInputBytes ?? _maximumInputBytes,
            });
            var session = content;
            // The reserved routes of an in-page menu bar, bound before the page loads.
            var menuRoutes = new MenuBarPageRoutes(Desktop, transport, WindowOptions.MenuBarPresentation,
                (loggerFactory?.CreateLogger("Runic.Application.Views.Desktop"))
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            IDisposable connections;
            try
            {
                connections = surface.SubscribeConnectionEvents(connection =>
                {
                    if (connection.Kind != DesktopConnectionEventKind.Disconnected) return;
                    var key = connection.SessionId.ToString(CultureInfo.InvariantCulture);
                    session.ReleaseConnection(key);
                    menuRoutes.ReleaseConnection(key);
                });
            }
            catch
            {
                menuRoutes.Dispose();
                throw;
            }
            connections = new Bindings(connections, menuRoutes);
            // The window releases its live development content when it closes, with its connection events.
            connectionBinding = development is null ? connections : new Bindings(connections, development);
            development = null;
            owner = new DesktopBridgeWindow<TViewModel>(scope, surface, transport, content, connectionBinding, viewModel);
            var attachment = opening.AttachBridge(transport, content);
            try { owner.Attach(attachment); }
            catch { attachment.Dispose(); throw; }
            if (ServeOnlyRequested)
            {
                // Without a presentation, nothing can keep a window open (D018.10), as on CS-WebUI.
                if (opening.CloseGuard is not null) opening.ReportCloseGuardUnavailable("serve-only mode");
                owner.ServeUntil(ServeOnlyStopped());
                Console.Out.WriteLine($"RUNIC_APPLICATION_URL={surface.Url}");
                Console.Out.Flush();
                return owner;
            }
            var windowOptions = GuardedWindowOptions(opening, WindowOptionsFor(opening.Options));
            if (Placement is { } choosePlacement && choosePlacement(opening.Window) is { } placement)
                windowOptions = windowOptions with { Placement = placement };
            var presentation = await surface.OpenWindowAsync(windowOptions, cancellationToken).ConfigureAwait(false);
            // An EmbeddedThenBrowser fallback opens without close confirmation; Runic Desktop reports
            // close-confirmation-unavailable for the options, and this names the Window.
            var confirmsClose = windowOptions.ConfirmCloseAsync is not null &&
                presentation.Capabilities.HasFlag(DesktopWindowCapabilities.CloseConfirmation);
            if (opening.CloseGuard is not null && windowOptions.ConfirmCloseAsync is not null && !confirmsClose)
                opening.ReportCloseGuardUnavailable("the browser fallback");
            // RunicWindow.RequestCloseAsync joins the decision of the native close button.
            if (confirmsClose) opening.UseCloseRequest(presentation.RequestCloseAsync);
            owner.SetPresentation(presentation);
            MenuCommandBinding? menu = null;
            if (viewModel is IRunicMenuCommandSource menuSource)
            {
                var menuLogger = (loggerFactory?.CreateLogger("Runic.Application.Views.Desktop"))
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
                menu = await MenuCommandBinding.BindAsync(Desktop, content.ModelContext, menuSource.GetMenuCommand, menuLogger)
                    .ConfigureAwait(false);
                if (!owner.SetMenuCommands(menu)) menu = null;
            }
            menuRoutes.Attach(presentation, menu);
            await CloseAfterOpenIfRequestedAsync(presentation, loggerFactory).ConfigureAwait(false);
            return owner;
        }
        catch (Exception error)
        {
            if (owner is not null) await owner.DisposeAsync().ConfigureAwait(false);
            else
            {
                connectionBinding?.Dispose();
                development?.Dispose();
                content?.Dispose();
                transport?.Dispose();
                if (surface is not null) await surface.DisposeAsync().ConfigureAwait(false);
                await scope.DisposeAsync().ConfigureAwait(false);
            }
            // One configuration exception on every host; the Desktop one stays as the inner exception.
            if (error is DesktopConfigurationException configuration)
                throw new RunicWindowConfigurationException([.. configuration.Diagnostics.Select(ToWindowDiagnostic)], configuration);
            throw;
        }
    }

    private readonly DevelopmentAssets _developmentAssets = new();

    // The process environment; tests replace it.
    internal Func<string, string?> ReadEnvironment { get; init; } = Environment.GetEnvironmentVariable;

    private sealed class Bindings(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            try { first.Dispose(); }
            finally { second.Dispose(); }
        }
    }

    private DesktopWindowOptions WindowOptionsFor(RunicWindowOptions options) => WindowOptions with
    {
        Width = options.Width ?? WindowOptions.Width,
        Height = options.Height ?? WindowOptions.Height,
    };

    // A guarded Window confirms user closes through ConfirmCloseAsync: first its own guard, then the one of
    // WindowOptions, if any. A presentation that cannot keep its window open, an explicitly selected browser or an
    // embedded-window host without close confirmation, opens unguarded with a warning instead of failing, so the
    // same Window opens on every host.
    private DesktopWindowOptions GuardedWindowOptions<TViewModel>(RunicWindowOpening<TViewModel> opening,
        DesktopWindowOptions options) where TViewModel : class
    {
        if (opening.CloseGuard is not { } guard) return options;
        if (options.ConfirmCloseAsync is { } configured)
            return options with
            {
                ConfirmCloseAsync = async cancellationToken =>
                    await guard(cancellationToken).ConfigureAwait(false) &&
                    await configured(cancellationToken).ConfigureAwait(false),
            };
        var guarded = options with { ConfirmCloseAsync = guard };
        try
        {
            var unsupported = Desktop.GetPresentationPreflight(guarded).OptionDiagnostics.Any(static diagnostic =>
                diagnostic is { Severity: DesktopDiagnosticSeverity.Error, Option: ConfirmCloseOption });
            if (!unsupported) return guarded;
        }
        // Invalid options fail when the window opens, with the same error.
        catch (ArgumentException) { return guarded; }
        opening.ReportCloseGuardUnavailable(options.Browser == BrowserKind.Embedded
            ? "the configured embedded-window host"
            : $"the {options.Browser} browser");
        return options;
    }

    private const string ConfirmCloseOption = $"{nameof(DesktopWindowOptions)}.{nameof(DesktopWindowOptions.ConfirmCloseAsync)}";

    private static RunicWindowDiagnostic ToWindowDiagnostic(DesktopDiagnostic diagnostic) =>
        new(diagnostic.Code, diagnostic.Message, diagnostic.Remediation)
        {
            Severity = diagnostic.Severity == DesktopDiagnosticSeverity.Warning
                ? RunicWindowDiagnosticSeverity.Warning
                : RunicWindowDiagnosticSeverity.Error,
            Option = diagnostic.Option,
        };

    internal const string ServeOnlyEnvironmentVariable = "RUNIC_APPLICATION_SERVE_ONLY";

    private static bool ServeOnlyRequested => Environment.GetEnvironmentVariable(ServeOnlyEnvironmentVariable) == "1";

    private static readonly object s_serveOnlyGate = new();
    // Completes when the next line is read; replaced by each line, so a Window that opens later waits for a new one.
    private static TaskCompletionSource? s_serveOnlyLine;
    // The standard input a reader thread reads until its end; null when none does.
    private static TextReader? s_serveOnlyInput;

    // CS-WebUI's serve-only contract: serving ends when a line is read from standard input or the process ends.
    // A termination signal only requests shutdown, so it ends serving too.
    private Task<Task> ServeOnlyStopped() =>
        Task.WhenAny(NextServeOnlyLine(), Task.Delay(Timeout.Infinite, Desktop.ShutdownRequested));

    // One reader for the process: a line ends serving for every Window serving when it is read, and a Window opened
    // after it waits for the next line (#310). End of input (for example </dev/null) is not a stop request.
    internal static Task NextServeOnlyLine()
    {
        lock (s_serveOnlyGate)
        {
            var line = s_serveOnlyLine ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var input = Console.In;
            if (!ReferenceEquals(s_serveOnlyInput, input))
            {
                s_serveOnlyInput = input;
                new Thread(() => ReadServeOnlyInput(input)) { IsBackground = true, Name = "Runic serve-only input" }.Start();
            }
            return line.Task;
        }
    }

    private static void ReadServeOnlyInput(TextReader input)
    {
        while (input.ReadLine() is not null)
        {
            TaskCompletionSource? line;
            lock (s_serveOnlyGate) (line, s_serveOnlyLine) = (s_serveOnlyLine, null);
            line?.TrySetResult();
        }
        lock (s_serveOnlyGate)
            if (ReferenceEquals(s_serveOnlyInput, input)) s_serveOnlyInput = null;
    }

    internal const string CloseAfterOpenEnvironmentVariable = "RUNIC_APPLICATION_CLOSE_AFTER_OPEN";

    // An automation contract like RUNIC_APPLICATION_SERVE_ONLY: report the presentation that opened,
    // then close it so the application's own WaitForCloseAsync returns.
    // The variable is inherited by child processes, so an active switch is always announced on standard error
    // and, when the application registered an ILoggerFactory, logged as event 2002.
    private static async ValueTask CloseAfterOpenIfRequestedAsync(DesktopWindow presentation, ILoggerFactory? loggerFactory)
    {
        if (Environment.GetEnvironmentVariable(CloseAfterOpenEnvironmentVariable) != "1") return;
        Console.Error.WriteLine(
            $"warning: {CloseAfterOpenEnvironmentVariable}=1 is set; Runic is closing the Desktop window it just opened.");
        Console.Error.Flush();
        if (loggerFactory is not null)
            DesktopLog.CloseAfterOpen(loggerFactory.CreateLogger(DesktopBridgeTransport.LogCategory), CloseAfterOpenEnvironmentVariable);
        Console.Out.WriteLine($"RUNIC_APPLICATION_OPENED={presentation.Browser}");
        Console.Out.Flush();
        await presentation.CloseAsync().ConfigureAwait(false);
    }
}
