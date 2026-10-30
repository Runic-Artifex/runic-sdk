using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Runic.Desktop.Internal;
using Runic.Platform;

namespace Runic.Desktop;

/// <summary>Owns listener resources and a set of isolated presentation surfaces.</summary>
public sealed class DesktopHost : IAsyncDisposable
{
    private readonly DesktopHostOptions _options;
    private readonly PresentationHostCore _core;
    private readonly ConcurrentDictionary<Guid, DesktopSurface> _surfaces = new();
    private readonly ILogger? _logger;
    // Not disposed: a late termination signal may still cancel it after the host is disposed.
    private readonly CancellationTokenSource _shutdown = new();
    private int _shutdownRequested;
    private int _signalQuitPending;
    private int _disposed;
    // The hosts alive in this process, in start order, which a quit from the platform asks.
    private static readonly Lock LiveGate = new();
    private static readonly List<DesktopHost> Live = [];

    private DesktopHost(DesktopHostOptions options)
    {
        ValidateOptions(options);
        _options = options;
        _core = CreateCore(options, options.Port);
        _logger = options.LoggerFactory?.CreateLogger(DesktopLog.Category);
        _menuBar = [.. options.MenuBar];
        _spellCheckingLanguages = [.. options.SpellCheckingLanguages];
        Monitors = CreateMonitors(options);
        if (options.StandardMenuTitles is { } titles) Internal.MacOsWkWebViewHost.SetStandardMenuTitles(titles);
        if (_logger is not null) Internal.MacOsWkWebViewHost.SetMenuLogger(_logger);
        if (_menuBar.Count > 0) Internal.MacOsWkWebViewHost.RequestMenuBar(_menuBar);
        if (options.MacOS.OpenRecentMenu) Internal.MacOsWkWebViewHost.RequestOpenRecentMenu();
        lock (LiveGate) Live.Add(this);
        if (OperatingSystem.IsMacOS()) MacOsApplication.HostsChanged();
    }

    internal static DesktopHost[] LiveHosts()
    {
        lock (LiveGate) return [.. Live];
    }

    internal DesktopHostOptions Options => _options;

    /// <summary>
    /// Occurs when the user asks to reopen the running application: on macOS by clicking its Dock icon or opening it
    /// again from Finder (<c>applicationShouldHandleReopen:hasVisibleWindows:</c>).
    /// </summary>
    /// <remarks>
    /// Raised on the thread pool while <see cref="DesktopEventLoop"/> runs. An application that keeps running without
    /// a window, for example after its last window closed, opens or shows its main window here when
    /// <see cref="DesktopReopenEventArgs.HasVisibleWindows"/> is <see langword="false"/>. AppKit then deminiaturizes a
    /// minimized window itself. Other platforms do not raise it; a second launch reaches the running instance as an open
    /// request instead. A handler that throws is logged as event 3020, <c>DesktopReopenHandlerFailed</c>; later handlers
    /// still run.
    /// </remarks>
    public event EventHandler<DesktopReopenEventArgs>? ReopenRequested;

    internal void RaiseReopenRequested(bool hasVisibleWindows)
    {
        if (Volatile.Read(ref _disposed) != 0 || ReopenRequested is not { } handlers) return;
        var args = new DesktopReopenEventArgs(hasVisibleWindows);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (EventHandler<DesktopReopenEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, args);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    if (_logger is { } logger) DesktopLog.ReopenHandlerFailed(logger, error, DesktopLog.ErrorType(error));
                    else System.Diagnostics.Trace.TraceError($"A DesktopHost.ReopenRequested handler failed with {error.GetType().FullName}.");
                }
            }
        });
    }

    /// <summary>
    /// Adds a document that the application opened or saved to the system's recent documents: on macOS the Open Recent
    /// menu (<see cref="MacOSDesktopOptions.OpenRecentMenu"/>) and the recent documents in the application's Dock menu.
    /// </summary>
    /// <param name="path">The document's fully qualified path.</param>
    /// <param name="cancellationToken">Cancels the queued native operation.</param>
    /// <returns>A task that completes when the document was noted.</returns>
    /// <remarks>
    /// <para>
    /// On macOS this calls <c>NSDocumentController.noteNewRecentDocumentURL:</c> on the main thread, which
    /// <see cref="DesktopEventLoop"/> serves: the most recent document comes first, a document noted again moves to the
    /// top, and the system keeps as many as the user chose in System Settings. Choosing one in Open Recent or the Dock
    /// delivers it as an open request (<c>IDesktopOpenRequests</c> of Runic.Platform.MacOS with
    /// <c>DesktopOpenOptions.IncludeFiles</c>), as opening it in the Finder does. Note a document after the user opened
    /// it and after Save As. AppKit keeps its own bookmark of the file, which needs access to it: in the App Sandbox,
    /// note the <c>LocalPath</c> of an open document lease (<c>IDocumentFileLease</c>). The sandbox grants no access to
    /// a document chosen in Runic's Open Recent: keep the lease's bookmark (<c>CreateBookmarkAsync</c>) with the path,
    /// and when the path arrives as an open request, reopen the document with
    /// <c>IDesktopFileBookmarks.OpenDocumentAsync</c>.
    /// </para>
    /// <para>Windows and Linux: the call checks its argument and does nothing.</para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty, not fully qualified, or contains a NUL character.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The host is disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask NoteRecentDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ValidateDocumentPath(path, nameof(path));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        return OperatingSystem.IsMacOS() ? MacOsWkWebViewHost.NoteRecentDocumentAsync(path, cancellationToken) : ValueTask.CompletedTask;
    }

    /// <summary>Empties the application's recent documents, as Clear Menu in Open Recent does.</summary>
    /// <param name="cancellationToken">Cancels the queued native operation.</param>
    /// <returns>A task that completes when the list is empty.</returns>
    /// <remarks>
    /// On macOS this calls <c>NSDocumentController.clearRecentDocuments:</c> on the main thread, which
    /// <see cref="DesktopEventLoop"/> serves. Windows and Linux: does nothing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The host is disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask ClearRecentDocumentsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        return OperatingSystem.IsMacOS() ? MacOsWkWebViewHost.ClearRecentDocumentsAsync(cancellationToken) : ValueTask.CompletedTask;
    }

    // A document's path for the platform's document conventions: fully qualified, without NUL.
    internal static void ValidateDocumentPath(string path, string parameterName)
    {
        if (path.Length == 0) throw new ArgumentException("The document path is empty.", parameterName);
        if (path.Contains('\0')) throw new ArgumentException("The document path contains a NUL character.", parameterName);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The document path is not fully qualified.", parameterName);
    }

    private IReadOnlyList<DesktopMenu> _menuBar;
    private IDesktopMenuCommandTarget? _menuCommands;

    /// <summary>Gets the menus of the menu bar: <see cref="DesktopHostOptions.MenuBar"/>, or those of the last <see cref="SetMenuBar"/>.</summary>
    public IReadOnlyList<DesktopMenu> MenuBar => Volatile.Read(ref _menuBar);

    /// <summary>Occurs after <see cref="SetMenuBar"/> has replaced the menus, so command sources and pages that render the bar can follow them.</summary>
    public event EventHandler? MenuBarChanged;

    /// <summary>
    /// Replaces the application's menus in every window's menu bar, for example with titles in a new language after a
    /// locale change, or with new checked state. The standard menus stay.
    /// </summary>
    /// <param name="menus">The new menus; empty removes the application's menus.</param>
    /// <exception cref="ArgumentException">A menu or item is incomplete; see <see cref="DesktopHostOptions.MenuBar"/>.</exception>
    /// <remarks>
    /// Each window's bar changes on its UI thread as soon as it can; on macOS that is the main thread, which
    /// <see cref="DesktopEventLoop"/> runs.
    /// </remarks>
    public void SetMenuBar(IReadOnlyList<DesktopMenu> menus)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(menus);
        _ = DesktopMenuBar.Check(menus, DesktopMenuPlanner.CurrentPlatform, DesktopMenuBarPresentation.Native, nativeBar: true);
        IReadOnlyList<DesktopMenu> copy = [.. menus];
        Volatile.Write(ref _menuBar, copy);
        Internal.MacOsWkWebViewHost.RequestMenuBar(copy);
        MenuBarChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Gets or sets the target of the menu commands that no key window runs, such as New or Settings…, also while no
    /// window is open; <see langword="null"/>, the default, for none.
    /// </summary>
    public IDesktopMenuCommandTarget? MenuCommands
    {
        get => Volatile.Read(ref _menuCommands);
        set
        {
            Volatile.Write(ref _menuCommands, value);
            Internal.MacOsWkWebViewHost.SetApplicationMenuCommands(value);
        }
    }

    private IReadOnlyList<string> _spellCheckingLanguages;

    /// <summary>
    /// Gets the languages the embedded windows' spell checker checks: <see cref="DesktopHostOptions.SpellCheckingLanguages"/>,
    /// or those of the last <see cref="SetSpellCheckingLanguagesAsync"/>; empty for the user's languages.
    /// </summary>
    public IReadOnlyList<string> SpellCheckingLanguages => Volatile.Read(ref _spellCheckingLanguages);

    /// <summary>
    /// Changes the languages the spell checker checks, in every open window and in the windows that open later, for
    /// example when the language of the open document changes.
    /// </summary>
    /// <param name="languages">BCP 47 language tags, such as <c>de-CH</c> and <c>en</c>; empty for the user's languages.</param>
    /// <param name="cancellationToken">Cancels waiting for the windows' native threads.</param>
    /// <returns>A task that completes when every open window has applied the languages.</returns>
    /// <remarks>
    /// The platforms apply the languages as <see cref="DesktopHostOptions.SpellCheckingLanguages"/> describes: WebKitGTK
    /// and macOS check with them in every window of the process, and WebView2 ignores them. New and edited text is
    /// checked with them; text checked before may keep its marks until it is edited. With
    /// <see cref="DesktopHostOptions.WindowHostFactory"/>, each window applies them when its host implements
    /// <see cref="IDesktopSpellCheckingWindowHost"/>, as the GTK 4 host does.
    /// </remarks>
    /// <exception cref="ArgumentException">A language is not a BCP 47 language tag.</exception>
    /// <exception cref="ObjectDisposedException">The host is disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async ValueTask SetSpellCheckingLanguagesAsync(IReadOnlyList<string> languages, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var copy = CheckSpellCheckingLanguages(languages, nameof(languages));
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _spellCheckingLanguages, copy);
        // A window that opens meanwhile reads the new languages, or applies them after it opened (WebUiWindow).
        foreach (var surface in _surfaces.Values.ToArray())
        {
            await surface.ApplySpellCheckingLanguagesAsync(copy, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string[] CheckSpellCheckingLanguages(IReadOnlyList<string> languages, string parameter)
    {
        ArgumentNullException.ThrowIfNull(languages, parameter);
        string[] copy = [.. languages];
        foreach (var language in copy)
        {
            if (language is null || !DesktopSurface.IsLanguageTag(language))
            {
                throw new ArgumentException(
                    "SpellCheckingLanguages must hold BCP 47 language tags such as de-CH or en: letters and digits in subtags of 1 to 8, joined by hyphens.",
                    parameter);
            }
        }
        return copy;
    }

    /// <summary>Gets the desktop's monitors, in the units of this host's windows.</summary>
    /// <remarks>
    /// <para>
    /// The built-in hosts list them on Windows, macOS and with GTK 3; on macOS the main thread must run
    /// <see cref="DesktopEventLoop"/> (or the call must come from it), as for windows. With
    /// <see cref="DesktopHostOptions.WindowHostFactory"/> they come from <see cref="IDesktopWindowHostFactory.Monitors"/>,
    /// which reports <see cref="PlatformUnavailableReason.BackendUnavailable"/> unless the factory provides them, as the GTK 4
    /// factory does. A monitor's <see cref="DesktopMonitor.WorkArea"/> can be passed to <see cref="DesktopWindow.MoveAsync"/>
    /// as it is, and its <see cref="DesktopMonitor.Id"/> to <see cref="DesktopWindow.MoveToMonitorAsync"/> and
    /// <see cref="DesktopWindowOptions.MonitorId"/>.
    /// </para>
    /// </remarks>
    /// <example><code>
    /// await foreach (var monitors in host.Monitors.WatchAsync(cancellationToken))
    ///     if (monitors is PlatformResult&lt;ImmutableArray&lt;DesktopMonitor&gt;&gt;.Success { Value: var list }) Show(list);
    /// </code></example>
    public IDesktopMonitors Monitors { get; }

    private static IDesktopMonitors CreateMonitors(DesktopHostOptions options)
    {
        if (options.WindowHostFactory is { } factory) return factory.Monitors;
        if (OperatingSystem.IsWindows()) return WindowsMonitors.Shared;
        if (OperatingSystem.IsMacOS()) return MacOsWkWebViewHost.Monitors;
        if (OperatingSystem.IsLinux() && options.Linux.EmbeddedBackend == LinuxEmbeddedBackend.Gtk3WebKit41) return LinuxWebKitGtkHost.Monitors;
        return DesktopMonitors.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    }

    /// <summary>Creates a host. Its listener binds when the first surface starts.</summary>
    public static ValueTask<DesktopHost> StartAsync(
        DesktopHostOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new DesktopHost(options ?? new DesktopHostOptions()));
    }

    /// <summary>Gets a token that is cancelled when the application is asked to quit or a termination signal asks it to stop.</summary>
    /// <remarks>
    /// On macOS, Quit (Command and Q), Quit in the Dock and logging out cancel it once
    /// <see cref="DesktopHostOptions.ConfirmQuitAsync"/> and the windows' close guards agreed, also with no window open.
    /// <see cref="DesktopEventLoop.Run(DesktopHostOptions, Func{DesktopHost, Task{int}})"/> cancels it on Ctrl+C,
    /// SIGTERM or a closed Windows console, then closes every open window without close confirmation, so
    /// <see cref="DesktopWindow.WaitForCloseAsync"/> returns and the application shuts down as if the user had closed its
    /// last window. Use the token to end application work that does not wait for a window, such as an application
    /// that runs behind a status icon, and set <see cref="DesktopHostOptions.HandlesShutdownWithoutWindow"/> so a
    /// signal that arrives while the host has no surface requests shutdown too.
    /// See <see cref="DesktopHostOptions.HandleShutdownSignals"/>.
    /// </remarks>
    public CancellationToken ShutdownRequested => _shutdown.Token;

    // Whether a shutdown request reaches the application: it presents a surface, in a native or browser window or
    // served at its URL (serve-only mode), which the shutdown closes or stops serving; or it declared that it ends on
    // ShutdownRequested without one. Otherwise a termination signal the host absorbed would leave the process running.
    // A surface is registered before its server starts and removed when it is disposed.
    internal bool CanShutDownInOrder => _options.HandlesShutdownWithoutWindow || !_surfaces.IsEmpty;

    internal bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    /// <summary>Gets the bound port, or the configured port before the first surface starts.</summary>
    public int Port => _core.Port;

    /// <summary>Evaluates one requested presentation and its explicit fallback policy without starting a browser or WebView.</summary>
    /// <remarks>
    /// The presentations come from <see cref="DesktopPlatform.GetAvailability"/> for this host's options, through
    /// <see cref="DesktopAvailabilityResult.GetPresentation"/>.
    /// <see cref="DesktopPresentationPreflight.OptionDiagnostics"/> reports each window option and permission grant
    /// the preferred presentation or its fallback rejects or ignores.
    /// </remarks>
    public DesktopPresentationPreflight GetPresentationPreflight(DesktopWindowOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var configured = (options ?? new DesktopWindowOptions()).WithPlacementApplied();
        DesktopSurface.ValidateWindowOptions(configured);
        var availability = DesktopPlatform.GetAvailability(_options);
        var preferredBrowser = configured.PresentationPolicy == DesktopPresentationPolicy.EmbeddedThenBrowser
            ? BrowserKind.Embedded
            : configured.Browser;
        var preferred = availability.GetPresentation(preferredBrowser);
        var optionDiagnostics = new List<DesktopDiagnostic>();
        DesktopWindowOptionValidation.AddPairChecks(configured, optionDiagnostics);
        if (preferredBrowser == BrowserKind.Embedded)
        {
            DesktopWindowOptionValidation.AddEmbeddedChecks(
                configured, _options.WindowHostFactory, _options.Linux.EmbeddedBackend, optionDiagnostics);
            // WebView2 has no setting for its spelling languages.
            if (OperatingSystem.IsWindows() && _options.WindowHostFactory is null && _options.SpellCheckingLanguages.Count > 0)
                optionDiagnostics.Add(DesktopWindowOptionValidation.SpellCheckingLanguagesIgnored());
        }
        else if (DesktopWindowOptionValidation.IsLaunchable(preferredBrowser))
        {
            DesktopWindowOptionValidation.AddBrowserChecks(
                configured, ConcreteBrowser(preferredBrowser, preferred), isFallback: false, optionDiagnostics);
        }

        DesktopPresentationAvailability? fallback = null;
        if (configured.PresentationPolicy == DesktopPresentationPolicy.EmbeddedThenBrowser)
        {
            var fallbackBrowser = configured.Browser == BrowserKind.Embedded ? BrowserKind.Any : configured.Browser;
            fallback = availability.GetPresentation(fallbackBrowser);
            if (DesktopWindowOptionValidation.IsLaunchable(fallbackBrowser))
            {
                DesktopWindowOptionValidation.AddBrowserChecks(
                    configured, ConcreteBrowser(fallbackBrowser, fallback), isFallback: true, optionDiagnostics);
            }
        }

        return new DesktopPresentationPreflight(
            configured.Browser,
            configured.PresentationPolicy,
            preferred,
            fallback)
        {
            OptionDiagnostics = optionDiagnostics,
        };
    }

    /// <summary>
    /// Checks a window request before it opens: the presentation and its prerequisites, the window options, and the
    /// permission grants. Each diagnostic is also logged through <see cref="DesktopHostOptions.LoggerFactory"/>.
    /// </summary>
    /// <remarks>
    /// Call it at startup and use <see cref="DesktopValidationResult.ThrowIfInvalid"/> to stop before any window
    /// opens. Opening the window repeats the checks it depends on; validation does not start a browser or WebView.
    /// </remarks>
    public DesktopValidationResult Validate(DesktopWindowOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        List<DesktopDiagnostic> diagnostics = [];
        DesktopPresentationPreflight? preflight = null;
        try
        {
            preflight = GetPresentationPreflight(options);
            diagnostics.AddRange(preflight.Diagnostics);
            diagnostics.AddRange(preflight.OptionDiagnostics);
        }
        catch (ArgumentException error)
        {
            diagnostics.Add(DesktopWindowOptionValidation.Invalid(error.Message));
        }
        // The menu bar's shortcuts that the platform keeps, its radio groups, and whether the window can show it.
        var platform = DesktopMenuPlanner.CurrentPlatform;
        var presentation = options?.MenuBarPresentation ?? DesktopMenuBarPresentation.Native;
        var nativeBar = platform == MenuPlatform.MacOS
            || preflight?.Preferred.Capabilities.HasFlag(DesktopWindowCapabilities.MenuBar) == true;
        diagnostics.AddRange(DesktopMenuBar.Check(MenuBar, platform, presentation, nativeBar));
        if (_options.StandardMenuTitles is { } titles) diagnostics.AddRange(titles.Check());

        if (_logger is { } logger)
        {
            foreach (var diagnostic in diagnostics)
            {
                DesktopLog.Diagnostic(logger, diagnostic);
            }
        }
        return new DesktopValidationResult(diagnostics);
    }

    private static BrowserKind? ConcreteBrowser(BrowserKind requested, DesktopPresentationAvailability availability) =>
        requested is BrowserKind.Any or BrowserKind.ChromiumBased
            ? availability.IsAvailable ? availability.Browser : null
            : requested;

    /// <summary>Creates and starts one isolated surface namespace.</summary>
    public ValueTask<DesktopSurface> CreateSurfaceAsync(
        DesktopSurfaceOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CreateSurfaceCoreAsync(options, windowHostFactory: null, cancellationToken);

    // A surface whose embedded window comes from its own factory instead of DesktopHostOptions.WindowHostFactory, such
    // as each RunicViewHost's RunicWebView on a DesktopHost the application shares between them. The surface's
    // availability checks consult that factory too.
    internal ValueTask<DesktopSurface> CreateSurfaceAsync(
        DesktopSurfaceOptions? options,
        IDesktopWindowHostFactory windowHostFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(windowHostFactory);
        if (windowHostFactory is ILinuxDesktopWindowHostFactory linux && _options.Linux.EmbeddedBackend != linux.Backend)
            throw new ArgumentException("Linux.EmbeddedBackend must match the surface's Linux window host factory.", nameof(windowHostFactory));
        return CreateSurfaceCoreAsync(options, windowHostFactory, cancellationToken);
    }

    private async ValueTask<DesktopSurface> CreateSurfaceCoreAsync(
        DesktopSurfaceOptions? options,
        IDesktopWindowHostFactory? windowHostFactory,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var configured = options ?? new DesktopSurfaceOptions();
        var content = configured.Content ?? throw new ArgumentException(
            "The surface content is required.", nameof(options));
        // Only directory content serves local files.
        string? rootFolder = null;
        if (content is DesktopContent.Directory directory)
        {
            rootFolder = Path.GetFullPath(directory.Root);
            if (!Directory.Exists(rootFolder))
            {
                throw new DirectoryNotFoundException($"The Desktop content root does not exist: {rootFolder}");
            }
            if (directory.Entry is { } entry && !File.Exists(Path.Combine(rootFolder, entry)))
            {
                throw DesktopContent.EntryNotFound(entry, rootFolder);
            }
        }

        var id = Guid.NewGuid();
        var path = configured.Path ?? $"surface-{id:N}";
        var isolatedCore = configured.UseIsolatedListener ? CreateCore(_options, port: 0) : null;
        var core = isolatedCore ?? _core;
        var security = ToCorePolicy(configured.Security ?? _options.Security);
        var surfaceFactory = windowHostFactory ?? _options.WindowHostFactory;
        IWebUiEmbeddedHostFactory embeddedFactory = surfaceFactory is null
            ? new WebUiEmbeddedHostFactory(_options.Linux.EmbeddedBackend)
            : new DesktopWindowHostFactoryAdapter(surfaceFactory);
        var runtime = new PresentationSurfaceRuntimeOptions(
            rootFolder,
            NormalizeOptionalPath(_options.BrowserFolder),
            embeddedFactory,
            _options.WaitForConnection,
            _options.ConnectionTimeout,
            _options.DiagnosticSink,
            _logger,
            _options.BrowserLaunchAttempts)
        {
            MenuHost = this,
            ContentSecurityPolicy = configured.ContentSecurityPolicy,
            GlobalMenu = _options.Linux.GlobalMenu,
        };
        var engine = new WebUiWindow(core, path, security, runtime);
        var surface = new DesktopSurface(this, id, engine, isolatedCore, windowHostFactory);
        if (!_surfaces.TryAdd(id, surface))
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            if (isolatedCore is not null)
            {
                await isolatedCore.DisposeAsync().ConfigureAwait(false);
            }
            throw new InvalidOperationException("The surface identifier could not be reserved.");
        }

        try
        {
            if (content is DesktopContent.Handler { Resolve: var handler })
            {
                engine.SetRequestHandler(Adapt(handler));
            }
            if (configured.Resources is { } resources)
            {
                engine.SetResourceHandler(Adapt(resources));
            }
            await engine.StartServerAsync(content, cancellationToken).ConfigureAwait(false);
            return surface;
        }
        catch
        {
            _surfaces.TryRemove(id, out _);
            await surface.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static Func<PresentationExchange, string, CancellationToken, ValueTask<WebUiContent?>> Adapt(
        ContentHandler handler) => async (context, requestPath, token) =>
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in context.RequestHeaders)
            {
                headers[header.Key] = header.Value;
            }
            var request = new ContentRequest(
                requestPath,
                context.Method,
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(headers),
                context.RequestServices!,
                context.Cancellation);
            var response = await handler(request, token).ConfigureAwait(false);
            return response?.ToCompatibilityContent();
        };

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }
        lock (LiveGate) Live.Remove(this);
        if (OperatingSystem.IsMacOS()) MacOsApplication.HostsChanged();

        var calledFromCallback = _surfaces.Values.Any(static surface => surface.IsExecutingCallback);
        var dispose = DisposeCoreAsync();
        GC.SuppressFinalize(this);
        return calledFromCallback ? ValueTask.CompletedTask : new ValueTask(dispose);
    }

    private async Task DisposeCoreAsync()
    {
        foreach (var surface in _surfaces.Values.ToArray())
        {
            await surface.DisposeFromHostAsync().ConfigureAwait(false);
        }
        _surfaces.Clear();
        await _core.DisposeAsync().ConfigureAwait(false);
    }

    internal void Detach(Guid id) => _surfaces.TryRemove(id, out _);

    // Starts the orderly shutdown a termination signal requests: cancels ShutdownRequested and force-closes every
    // open window. Each window host marshals its close to its own UI thread. The application then returns and its
    // owner disposes the host; this never disposes it. Returns false when shutdown was already requested.
    internal bool RequestShutdown(string reason, bool quit = false)
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0) return false;
        if (quit)
        {
            if (_logger is { } quitLogger) DesktopLog.QuitRequested(quitLogger, reason);
            else System.Diagnostics.Trace.TraceInformation($"{reason} quits the application; closing Desktop windows.");
        }
        else if (_logger is { } logger) DesktopLog.ShutdownRequested(logger, reason);
        else System.Diagnostics.Trace.TraceInformation($"{reason} received; closing Desktop windows. Send it again to terminate immediately.");
        // Signal handlers run on a runtime thread; keep it free.
        _ = Task.Run(ShutdownAsync);
        return true;
    }

    // A termination signal: without DesktopHostOptions.ConfirmQuitAsync the shutdown starts now, as in 0.11. With it, the
    // application decides first; a refusal calls refused so that a later signal asks again. Returns false when shutdown
    // was already requested or a decision is pending, as RequestShutdown does for a repeat.
    internal bool RequestShutdownForSignal(string reason, Action refused)
    {
        if (_options.ConfirmQuitAsync is null) return RequestShutdown(reason);
        if (IsShutdownRequested || Interlocked.Exchange(ref _signalQuitPending, 1) != 0) return false;
        _ = Task.Run(async () =>
        {
            bool confirmed = false;
            try { confirmed = await ConfirmQuitAsync(DesktopQuitSource.Signal).ConfigureAwait(false); }
            finally { Volatile.Write(ref _signalQuitPending, 0); }
            if (confirmed) RequestShutdown(reason);
            else refused();
        });
        return true;
    }

    // DesktopHostOptions.ConfirmQuitAsync. A failure is a refusal; a shutdown that started meanwhile goes ahead.
    internal async Task<bool> ConfirmQuitAsync(DesktopQuitSource source)
    {
        if (IsShutdownRequested) return true;
        if (_options.ConfirmQuitAsync is not { } confirm) return true;
        try
        {
            return await confirm(new DesktopQuitRequest(source), _shutdown.Token).ConfigureAwait(false) || IsShutdownRequested;
        }
        catch (OperationCanceledException) when (IsShutdownRequested)
        {
            return true;
        }
        catch (Exception error)
        {
            if (IsShutdownRequested) return true;
            // A failed prompt must never become implicit permission to discard application state.
            if (_logger is { } logger) DesktopLog.QuitConfirmationFailed(logger, error, DesktopLog.ErrorType(error));
            else System.Diagnostics.Trace.TraceError($"DesktopHostOptions.ConfirmQuitAsync failed; the application keeps running: {error}");
            return false;
        }
    }

    // Asks each window's close guard in turn; a window whose guard agrees closes. False when one keeps its window.
    internal async Task<bool> RequestWindowsCloseAsync()
    {
        foreach (var surface in _surfaces.Values.ToArray())
        {
            if (IsShutdownRequested) return true;
            try
            {
                if (!await surface.ConfirmCloseForQuitAsync(_shutdown.Token).ConfigureAwait(false)) return false;
            }
            catch (OperationCanceledException) when (IsShutdownRequested)
            {
                return true;
            }
            catch (ObjectDisposedException)
            {
                // A surface disposed meanwhile has no window to keep.
            }
        }
        return true;
    }

    private async Task ShutdownAsync()
    {
        try
        {
            _shutdown.Cancel();
        }
        catch (AggregateException error)
        {
            ReportShutdownFailure(error);
        }
        foreach (var surface in _surfaces.Values.ToArray())
        {
            try
            {
                await surface.CloseWindowForShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                ReportShutdownFailure(error);
            }
        }
    }

    private void ReportShutdownFailure(Exception error)
    {
        if (_logger is { } logger) DesktopLog.ShutdownFailed(logger, error, DesktopLog.ErrorType(error));
        else System.Diagnostics.Trace.TraceError($"Closing Desktop windows for shutdown failed: {error}");
    }

    internal void Report(DesktopDiagnostic diagnostic) => _options.DiagnosticSink?.Invoke(diagnostic);

    // Reports a configuration limitation found while opening, to the sink and the Desktop logger.
    internal void ReportConfiguration(DesktopDiagnostic diagnostic)
    {
        Report(diagnostic);
        if (_logger is { } logger) DesktopLog.Diagnostic(logger, diagnostic);
        else System.Diagnostics.Trace.TraceWarning($"{diagnostic.Code}: {diagnostic.Message}");
    }

    // The availability of a surface's presentation: with its own window host factory, that factory's.
    internal DesktopPresentationAvailability GetPresentation(BrowserKind browser, IDesktopWindowHostFactory? windowHostFactory = null) =>
        DesktopPlatform.GetAvailability(windowHostFactory is null ? _options : _options with { WindowHostFactory = windowHostFactory })
            .GetPresentation(browser);

    internal static DesktopPresentationAvailability Unavailable(BrowserKind browser) => new(
        browser,
        IsAvailable: false,
        ExecutablePath: null,
        DesktopWindowCapabilities.None,
        [!DesktopWindowOptionValidation.IsLaunchable(browser)
            ? DesktopWindowOptionValidation.BrowserUnsupported(browser)
            : new DesktopDiagnostic(
            DesktopErrorCategory.Unavailable,
            "presentation-unavailable",
            $"No supported presentation was discovered for {browser}.",
            Retryable: false,
            Remediation: "Install a supported browser or WebView runtime; DesktopHost.Validate(windowOptions) lists what is missing.")]);

    private static PresentationHostCore CreateCore(DesktopHostOptions options, int port) => new(
        new PresentationHostCoreOptions(
            port,
            options.NetworkExposure == DesktopNetworkExposure.AllInterfaces
                ? PresentationNetworkExposure.AllInterfaces
                : PresentationNetworkExposure.Loopback,
            options.ConfigureServices));

    private static PresentationSecurityPolicy ToCorePolicy(DesktopSecurityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var origins = policy.AdditionalOrigins
            .Select(PresentationSecurityPolicy.CanonicalOrigin)
            .ToHashSet(StringComparer.Ordinal);
        return new PresentationSecurityPolicy(
            policy.RequireSessionCredential,
            policy.AllowMissingOrigin,
            policy.ClientAdmission == DesktopClientAdmission.Multiple,
            UseClientCookies: true,
            origins);
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    private static void ValidateOptions(DesktopHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Linux);
        ArgumentNullException.ThrowIfNull(options.MacOS);
        ArgumentNullException.ThrowIfNull(options.MacOS.DockMenu, nameof(options));
        if (options.MacOS.DockMenu.Count > 0) DesktopMenuRules.ContextMenu([.. options.MacOS.DockMenu]);
        if (options.Linux.EmbeddedBackend is { } backend && !Enum.IsDefined(backend))
            throw new ArgumentOutOfRangeException(nameof(options), "Linux.EmbeddedBackend contains an undefined value; select Gtk3WebKit41 or Gtk4WebKit6.");
        if (options.WindowHostFactory is ILinuxDesktopWindowHostFactory linux && options.Linux.EmbeddedBackend != linux.Backend)
            throw new ArgumentException("Linux.EmbeddedBackend must match the configured Linux window host factory.", nameof(options));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Port, ushort.MaxValue);
        if (options.ConnectionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ConnectionTimeout must be positive.");
        }
        ArgumentNullException.ThrowIfNull(options.MenuBar);
        _ = DesktopMenuBar.Check(options.MenuBar, DesktopMenuPlanner.CurrentPlatform, DesktopMenuBarPresentation.Native, nativeBar: true);
        options.StandardMenuTitles?.Validate();
        _ = CheckSpellCheckingLanguages(options.SpellCheckingLanguages, nameof(options));
        if (options.BrowserLaunchAttempts is < 1 or > DesktopHostOptions.MaximumBrowserLaunchAttempts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"BrowserLaunchAttempts must be between 1 and {DesktopHostOptions.MaximumBrowserLaunchAttempts}.");
        }
        // The application must choose a policy; which instance it chose is irrelevant.
        if (options.NetworkExposure == DesktopNetworkExposure.AllInterfaces && !options.HasExplicitSecurity)
        {
            throw new ArgumentException(
                "Non-loopback binding requires an explicit security policy.",
                nameof(options));
        }
    }
}
