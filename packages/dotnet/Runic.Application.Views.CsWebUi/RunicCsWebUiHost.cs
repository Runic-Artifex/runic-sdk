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
/// CS-WebUI is supported on a best-effort basis. Its windows have no native owner, so platform services report
/// <c>OwnerUnavailable</c>, and it reports closing for all of its windows at once.
/// When the <c>RUNIC_APPLICATION_SERVE_ONLY</c> environment variable is <c>1</c>, no browser is launched: each
/// window starts its local server, writes <c>RUNIC_APPLICATION_URL=&lt;url&gt;</c> to standard output, and keeps
/// serving until a line is read from standard input or the process ends. Test harnesses and remote browsers use
/// this without changing application code.
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
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    /// <inheritdoc />
    public IServiceProvider Services { get; }

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
    /// <see cref="Run(IServiceProvider, Func{RunicCsWebUiHost, Task{int}})"/> the window counts as shown, so
    /// <see cref="RunicWindow{TViewModel}.WaitForCloseAsync"/> completes when WebUI's wait returns: show the window or
    /// start its server here.
    /// </remarks>
    public Action<WebUiWindow, string>? ShowWindow { get; init; }

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
    /// </remarks>
    /// <param name="services">The application's service provider.</param>
    /// <param name="application">The application. Its result becomes the return value.</param>
    /// <returns>The value returned by <paramref name="application"/>.</returns>
    public static int Run(IServiceProvider services, Func<RunicCsWebUiHost, Task<int>> application)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(application);
        var host = new RunicCsWebUiHost(services);
        var loop = new MainThreadLoop();
        host._loop = loop;
        try
        {
            var work = Task.Run(() => application(host));
            loop.Run(work);
            return work.GetAwaiter().GetResult();
        }
        finally
        {
            host._loop = null;
            loop.Stop();
            WebUiApplication.Clean();
        }
    }

    IReadOnlyList<RunicWindowDiagnostic> IRunicWindowHost.Validate(RunicWindowOptions options) => [];

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
            window = new WebUiWindow();
            transport = window.CreateBridgeSession();
            content = new WindowContentSession(transport,
                scope.ServiceProvider.GetService<IRunicViewLocator>(), rootModel: viewModel,
                modelContext: scope.ServiceProvider.GetService<IRunicModelContext>(),
                loggerFactory: scope.ServiceProvider.GetService<ILoggerFactory>());
            var session = content;
            // Only the all-events binding receives disconnects. runic-cswebui.js
            // stops WebUI from also sending click events for elements with ids.
            connectionBinding = window.Bind("", e =>
            {
                if (e.EventType == WebUiEventType.Disconnected)
                    session.ReleaseConnection($"{e.ClientId}:{e.ConnectionId}");
            });
            host = new CsWebUiBridgeWindow<TViewModel>(scope, window, transport, content, connectionBinding, viewModel);
            var attachment = opening.AttachBridge(transport, content);
            try { host.Attach(attachment); }
            catch { attachment.Dispose(); throw; }

            var options = opening.Options;
            var native = window;
            var shown = host;
            void Show()
            {
                native.SetRootFolder(options.ResolveContentRoot());
                if (options.Width is { } width && options.Height is { } height) native.SetSize((uint)width, (uint)height);
                ConfigureWindow?.Invoke(native);
                shown.Show(options.StartPage, UseWebView, ShowWindow);
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
