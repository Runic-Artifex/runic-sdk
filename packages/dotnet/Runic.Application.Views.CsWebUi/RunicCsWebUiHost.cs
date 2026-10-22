using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Navigation;

namespace Runic.Application.Views.CsWebUi;

/// <summary>
/// Opens Runic Windows with CS-WebUI, in an installed browser in app mode or the platform WebView. Start it with
/// <see cref="Run(IServiceProvider, Func{RunicCsWebUiHost, Task{int}})"/> and open Windows with
/// <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/>:
/// <code>
/// return RunicCsWebUiHost.Run(provider, async host =&gt;
/// {
///     await using var window = await host.OpenWindowAsync&lt;MainWindow&gt;();
///     await window.WaitForCloseAsync();
///     return 0;
/// });
/// </code>
/// Replacing <c>RunicCsWebUiHost</c> with <c>RunicDesktopHost</c> (package <c>Runic.Application.Views.Desktop</c>) runs the
/// same application in a native window.
/// </summary>
/// <remarks>
/// As with <c>RunicDesktopHost</c>, the application runs as long as the body passed to <c>Run</c>, with or without
/// open Windows.
/// CS-WebUI is supported on a best-effort basis. Its windows have no native owner, so platform services report
/// <c>OwnerUnavailable</c>, and it reports closing for all of its windows at once. It cannot keep a window open, so a
/// Window with <see cref="RunicWindow{TViewModel}.GuardClose"/> opens unguarded and logs
/// <c>WindowCloseGuardUnavailable</c> (event 1053).
/// When the <c>RUNIC_APPLICATION_SERVE_ONLY</c> environment variable is <c>1</c>, no browser is launched: each
/// window starts its local server, writes <c>RUNIC_APPLICATION_URL=&lt;url&gt;</c> to standard output, and keeps
/// serving until a line is read from standard input or the process ends. Test harnesses and remote browsers use
/// this without changing application code.
/// <see cref="RunicWindowHostExtensions.ValidateWindow{TWindow}"/> reports a <c>CSWEBUI_BROWSER_PATH</c> that names
/// no file (<c>browser-not-found</c>). Whether WebUI finds another browser, the system default browser or the WebView
/// runtime shows only when a window opens, so <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/> reports
/// those failures.
/// </remarks>
public sealed class RunicCsWebUiHost : IRunicWindowHost
{
    private MainThreadLoop? _loop;

    /// <summary>Creates a host that opens Windows from <paramref name="services"/>.</summary>
    /// <remarks>
    /// <see cref="Run(IServiceProvider, Func{RunicCsWebUiHost, Task{int}})"/> creates the host for you and keeps the
    /// process main thread in WebUI's wait loop, as CS-WebUI expects. A host created here shows windows on the
    /// calling thread, and <see cref="RunicWindow{TViewModel}.WaitForCloseAsync"/> polls
    /// <see cref="WebUiApplication.WaitAsync"/>; call <see cref="WebUiApplication.Clean"/> after the last window is disposed.
    /// </remarks>
    public RunicCsWebUiHost(IServiceProvider services)
        : this(services, DefaultOptions)
    {
    }

    /// <summary>Creates a host that opens Windows from <paramref name="services"/> with <paramref name="options"/>.</summary>
    /// <param name="services">The application's service provider.</param>
    /// <param name="options">How the host shows each window.</param>
    /// <inheritdoc cref="RunicCsWebUiHost(IServiceProvider)" path="/remarks"/>
    public RunicCsWebUiHost(IServiceProvider services, RunicCsWebUiHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        Services = services;
        UseWebView = options.UseWebView;
        ConfigureWindow = options.ConfigureWindow;
        ShowWindow = options.ShowWindow;
        _includeFailureDetail = options.IncludeFailureDetail;
    }

    private readonly bool? _includeFailureDetail;

    private static readonly RunicCsWebUiHostOptions DefaultOptions = new();

    /// <inheritdoc />
    public IServiceProvider Services { get; }

    /// <summary>Whether windows open in the platform WebView: <see cref="RunicCsWebUiHostOptions.UseWebView"/>.</summary>
    public bool UseWebView { get; }

    /// <summary>Configures each native window before it is shown: <see cref="RunicCsWebUiHostOptions.ConfigureWindow"/>.</summary>
    public Action<WebUiWindow>? ConfigureWindow { get; }

    /// <summary>Shows each native window in place of the selected presentation: <see cref="RunicCsWebUiHostOptions.ShowWindow"/>.</summary>
    public Action<WebUiWindow, string>? ShowWindow { get; }

    /// <summary>
    /// Runs <paramref name="application"/> while the process main thread waits for WebUI, then releases WebUI's
    /// process-wide state with <see cref="WebUiApplication.Clean"/>.
    /// </summary>
    /// <remarks>
    /// Call it from the process entry point and return its result as the exit code. The application runs on the
    /// thread pool. The main thread shows the windows the application opens while it is idle, and blocks in
    /// <see cref="WebUiApplication.Wait"/> while windows are shown; when that returns, every shown window's
    /// <see cref="RunicWindow{TViewModel}.WaitForCloseAsync"/> completes. Dispose <paramref name="services"/> after
    /// this method returns.
    /// <para>
    /// A window that WebUI cannot show, for example because no browser is installed, the browser selected with
    /// <c>CSWEBUI_BROWSER_PATH</c> or <see cref="WebUiApplication.SetBrowserPath"/> does not exist or fails to start,
    /// or its page does not connect in time, fails <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/> with a
    /// <see cref="RunicWindowConfigurationException"/>. Its diagnostic names the browser, its path and the reason, with a
    /// code such as <c>browser-not-found</c>, and WebUI's exception is the inner exception. The failure is logged as
    /// event 2100 <c>CsWebUiWindowShowFailed</c>. When the application does not handle it, this method writes the
    /// message to standard error and returns 1.
    /// </para>
    /// </remarks>
    /// <param name="services">The application's service provider.</param>
    /// <param name="application">The application. Its result becomes the return value.</param>
    /// <returns>The value returned by <paramref name="application"/>, or 1 when it did not handle a window that could not be shown.</returns>
    public static int Run(IServiceProvider services, Func<RunicCsWebUiHost, Task<int>> application) =>
        Run(services, DefaultOptions, application);

    /// <summary>Runs <paramref name="application"/> with a host that shows each window as <paramref name="options"/> say.</summary>
    /// <param name="services">The application's service provider.</param>
    /// <param name="options">How the host shows each window, for example in the platform WebView.</param>
    /// <param name="application">The application. Its result becomes the return value.</param>
    /// <returns>The value returned by <paramref name="application"/>, or 1 when it did not handle a window that could not be shown.</returns>
    /// <inheritdoc cref="Run(IServiceProvider, Func{RunicCsWebUiHost, Task{int}})" path="/remarks"/>
    public static int Run(IServiceProvider services, RunicCsWebUiHostOptions options, Func<RunicCsWebUiHost, Task<int>> application)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(application);
        var host = new RunicCsWebUiHost(services, options);
        var loop = new MainThreadLoop();
        host._loop = loop;
        try
        {
            var work = Task.Run(() => application(host));
            loop.Run(work);
            return work.GetAwaiter().GetResult();
        }
        // Already logged as event 2100. An application that does not handle it exits with its message instead of
        // an unhandled exception.
        catch (RunicWindowConfigurationException error) when (error.InnerException is WebUiException)
        {
            Console.Error.WriteLine(error.Message);
            return ShowFailedExitCode;
        }
        finally
        {
            host._loop = null;
            loop.Stop();
            WebUiApplication.Clean();
        }
    }

    // The exit code of Run when a window could not be shown and the application did not handle it.
    internal const int ShowFailedExitCode = 1;

    // Checks what can be known before a window shows. Serve-only mode, ShowWindow and UseWebView launch no browser. A
    // browser selected with CSWEBUI_BROWSER_PATH is used without fallback, so one that names no file fails every open,
    // as dotnet runic doctor reports. WebUI's recommended presentation falls back from an installed browser to the
    // system default browser and then to the WebView, and the WebView runtime is loaded only when a window shows, so
    // neither can be checked without showing one: such an open still fails with a Window diagnostic.
    IReadOnlyList<RunicWindowDiagnostic> IRunicWindowHost.Validate(RunicWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Environment.GetEnvironmentVariable(ServeOnlyEnvironmentVariable) == "1" || ShowWindow is not null || UseWebView) return [];
        var configured = Environment.GetEnvironmentVariable(WebUiApplication.BrowserPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured)) return [];
        string path;
        try { path = Path.GetFullPath(configured); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { path = configured; }
        // On macOS the variable names the .app bundle, a directory.
        return File.Exists(path) || Directory.Exists(path)
            ? []
            : [new RunicWindowDiagnostic(BrowserNotFoundCode,
                $"{WebUiApplication.BrowserPathEnvironmentVariable} names the browser '{configured}', which does not exist.",
                $"Set {WebUiApplication.BrowserPathEnvironmentVariable} to an installed browser that WebUI can launch, or unset it.")];
    }

    private const string ServeOnlyEnvironmentVariable = "RUNIC_APPLICATION_SERVE_ONLY";
    private const string BrowserNotFoundCode = "browser-not-found";

    async ValueTask<IBridgeWindow> IRunicWindowHost.OpenAsync<TViewModel>(RunicWindowOpening<TViewModel> opening,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(opening);
        var scope = opening.Scope;
        var loop = _loop;
        WebUiWindow? window = null;
        RebindableBridgeTransport? transport = null;
        WindowContentSession? content = null;
        WebUiBinding? connectionBinding = null;
        CsWebUiBridgeWindow<TViewModel>? host = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var viewModel = opening.ViewModel;
            CsWebUiLog.WarnIfNativeLibraryUnchecked(scope.ServiceProvider);
            window = new WebUiWindow();
            transport = new RebindableBridgeTransport(new CsWebUiBridgeTransport(window));
            content = new WindowContentSession(transport, new WindowContentSessionOptions
            {
                ViewLocator = scope.ServiceProvider.GetService<IRunicViewLocator>(),
                RootModel = viewModel,
                ModelContext = scope.ServiceProvider.GetService<IRunicModelContext>(),
                LoggerFactory = scope.ServiceProvider.GetService<ILoggerFactory>(),
                IncludeFailureDetail = opening.Options.IncludeFailureDetail ?? _includeFailureDetail,
            });
            var session = content;
            // Only the all-events binding receives disconnects. runic-cswebui.js
            // stops WebUI from also sending click events for elements with ids.
            connectionBinding = window.Bind("", e =>
            {
                if (e.EventType == WebUiEventType.Disconnected)
                    session.ReleaseConnection($"{e.ClientId}:{e.ConnectionId}");
                // With this binding, WebUI cancels the page's navigations from each (re)connect until
                // runic-cswebui.js allows them before a Bridge call, and reports each one here; ignoring it dropped a
                // reload or a link before the first call (#196). CS-WebUI does not expose the destination of a
                // navigation event, so the page repeats the navigation it recorded, if it stays on its origin.
                else if (e.EventType == WebUiEventType.Navigation)
                    e.RunJavaScript(ContinueNavigationScript);
            });
            host = new CsWebUiBridgeWindow<TViewModel>(scope, window, transport, content, connectionBinding, viewModel);
            var attachment = opening.AttachBridge(transport, content);
            try { host.Attach(attachment); }
            catch { attachment.Dispose(); throw; }
            // A browser window closes without asking the application, and WebUI reports the close only after it
            // happened: a guarded Window opens unguarded, with a warning (D018.10).
            if (opening.CloseGuard is not null) opening.ReportCloseGuardUnavailable("CS-WebUI");

            var options = opening.Options;
            var windowName = opening.Window.GetType().Name;
            var native = window;
            var shown = host;
            void Show()
            {
                native.SetRootFolder(options.ResolveContentRoot());
                if (options.Width is { } width && options.Height is { } height) native.SetSize((uint)width, (uint)height);
                ConfigureWindow?.Invoke(native);
                // A browser that cannot be found, started or connected fails the open with its reason, logged as
                // event 2100, instead of WebUI's exception (#265).
                try { shown.Show(options.StartPage, UseWebView, ShowWindow); }
                catch (WebUiException error) { throw CsWebUiLog.ShowFailed(scope.ServiceProvider, windowName, error); }
            }
            if (loop is null) Show();
            else host.WebUiClosed = await loop.ShowAsync(Show, cancellationToken).ConfigureAwait(false);
            return host;
        }
        catch
        {
            if (host is not null) await host.DisposeAsync().ConfigureAwait(false);
            else
            {
                connectionBinding?.Dispose();
                content?.Dispose();
                transport?.Dispose();
                window?.Dispose();
                await scope.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    // runic-cswebui.js records each navigation WebUI cancels; this repeats the last one with navigation allowed.
    internal const string ContinueNavigationScript = "window.__runicContinueNavigation?.();";

    // The process main thread: shows windows while idle, then waits in WebUiApplication.Wait while any is shown.
    private sealed class MainThreadLoop
    {
        private readonly object _gate = new();
        private readonly Queue<(Action Show, TaskCompletionSource<Task> Done)> _pending = new();
        // Set when the application posts a window or finishes; guarded by _gate.
        private bool _signaled;
        // Completes when the next WebUI wait returns, which covers every window shown before it does.
        private TaskCompletionSource _webUiClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _waiting;
        private bool _stopped;
        private int _shown;

        public void Run(Task work)
        {
            _ = work.ContinueWith(_ =>
            {
                bool waiting;
                lock (_gate) waiting = _waiting;
                // The application returned while WebUI still reports a window, for example one it never released:
                // end WebUI's wait so Run can clean up and return.
                if (waiting) WebUiApplication.Exit();
                Signal();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            while (!work.IsCompleted)
            {
                RunPending();
                bool wait;
                lock (_gate) _waiting = wait = _shown > 0;
                if (!wait)
                {
                    // Wakes when the application posts a window or finishes.
                    lock (_gate)
                    {
                        while (!_signaled) Monitor.Wait(_gate);
                        _signaled = false;
                    }
                    continue;
                }
                try { WebUiApplication.Wait(); }
                finally { WebUiClosed(); }
            }
        }

        // On the main thread while it is idle, as WebUI expects; inline while it waits for shown windows.
        // Returns the task that completes when WebUI reports this window closed.
        public async Task<Task> ShowAsync(Action show, CancellationToken cancellationToken)
        {
            var done = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool inline;
            lock (_gate)
            {
                inline = _waiting || _stopped;
                if (!inline) _pending.Enqueue((show, done));
            }
            if (inline) return Shown(show);
            Signal();
            return await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private void Signal()
        {
            lock (_gate)
            {
                _signaled = true;
                Monitor.PulseAll(_gate);
            }
        }

        public void Stop()
        {
            lock (_gate) _stopped = true;
            RunPending();
        }

        private Task Shown(Action show)
        {
            show();
            lock (_gate)
            {
                _shown++;
                return _webUiClosed.Task;
            }
        }

        private void RunPending()
        {
            while (true)
            {
                (Action Show, TaskCompletionSource<Task> Done) item;
                lock (_gate)
                {
                    if (!_pending.TryDequeue(out item)) return;
                }
                try { item.Done.TrySetResult(Shown(item.Show)); }
                catch (Exception error) { item.Done.TrySetException(error); }
            }
        }

        // WebUI's wait returns once every shown window has closed.
        private void WebUiClosed()
        {
            TaskCompletionSource closed;
            lock (_gate)
            {
                _waiting = false;
                _shown = 0;
                closed = _webUiClosed;
                _webUiClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            closed.TrySetResult();
        }
    }
}
