using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;

namespace Runic.Application.Views.CsWebUi;

/// <summary>
/// Owns one CS-WebUI window, its DI scope, and its Bridge attachments.
/// </summary>
public sealed class CsWebUiBridgeWindow<TViewModel> : IDisposable, IAsyncDisposable where TViewModel : class
{
    private readonly object _closeGate = new();
    private readonly IServiceScope _scope;
    private readonly WebUiWindow _window;
    private readonly RebindableBridgeTransport _transport;
    private readonly WindowContentSession _content;
    private readonly IDisposable _connectionBinding;
    private IDisposable? _attachment;
    private Task<CsWebUiBridgeCloseResult>? _closeAdmission;
    private Task? _closeCompletion;
    private bool _admissionReleased;
    private bool _finalized;

    internal CsWebUiBridgeWindow(
        IServiceScope scope,
        WebUiWindow window,
        RebindableBridgeTransport transport,
        WindowContentSession content,
        IDisposable connectionBinding,
        TViewModel viewModel)
    {
        _scope = scope;
        _window = window;
        _transport = transport;
        _content = content;
        _connectionBinding = connectionBinding;
        ViewModel = viewModel;
    }

    /// <summary>The window's scoped root ViewModel.</summary>
    public TViewModel ViewModel { get; }

    /// <summary>The underlying CS-WebUI window.</summary>
    public WebUiWindow NativeWindow => _window;

    /// <inheritdoc cref="WebUiWindow.SetRootFolder(string)"/>
    public void SetRootFolder(string path) => _window.SetRootFolder(path);

    /// <inheritdoc cref="WebUiWindow.SetSize(uint, uint)"/>
    public void SetSize(uint width, uint height) => _window.SetSize(width, height);

    /// <inheritdoc cref="WebUiWindow.SetPort(nuint)"/>
    public void SetPort(nuint port) => _window.SetPort(port);

    /// <summary>
    /// Shows <paramref name="content"/> with WebUI's recommended presentation:
    /// an installed browser in app mode (Chromium-based browsers first), then
    /// the system default browser, then the platform WebView.
    /// </summary>
    /// <remarks>
    /// When the <c>RUNIC_APPLICATION_SERVE_ONLY</c> environment variable is
    /// <c>1</c>, no browser is launched. The window starts its local server,
    /// writes <c>RUNIC_APPLICATION_URL=&lt;url&gt;</c> to standard output, and
    /// keeps serving until a line is read from standard input or the process
    /// ends. Test harnesses and remote browsers use this without changing
    /// application code.
    /// </remarks>
    public void Show(string content)
    {
        if (!TryServeOnly(content)) _window.Show(content);
    }

    /// <summary>
    /// Shows <paramref name="content"/> in the platform WebView (WebView2,
    /// WebKitGTK or WKWebView) instead of a browser. The serve-only
    /// environment variable described on <see cref="Show"/> also applies.
    /// </summary>
    public void ShowWebView(string content)
    {
        if (!TryServeOnly(content)) _window.ShowWebView(content);
    }

    /// <inheritdoc cref="WebUiWindow.StartServer(string)"/>
    public string StartServer(string content) => _window.StartServer(content);

    internal const string ServeOnlyEnvironmentVariable = "RUNIC_APPLICATION_SERVE_ONLY";

    private bool TryServeOnly(string content)
    {
        if (Environment.GetEnvironmentVariable(ServeOnlyEnvironmentVariable) != "1") return false;
        // Zero waits for WebUiApplication.Exit rather than for a first browser
        // connection, so the server outlives connecting and closing clients.
        WebUiApplication.SetConnectionTimeout(0);
        var url = _window.StartServer(content);
        Console.Out.WriteLine($"RUNIC_APPLICATION_URL={url}");
        Console.Out.Flush();
        // End of input (for example </dev/null) is not a stop request.
        new Thread(static () =>
        {
            if (Console.In.ReadLine() is not null) WebUiApplication.Exit();
        })
        { IsBackground = true, Name = "Runic serve-only input" }.Start();
        return true;
    }

    internal void Attach(IDisposable attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        lock (_closeGate)
        {
            if (_attachment is not null || _closeAdmission is not null)
                throw new InvalidOperationException("The Window Bridge is already attached or closing.");
            _attachment = attachment;
        }
    }

    /// <summary>
    /// Stops new operation admission and waits up to <paramref name="timeout"/>
    /// for accepted work. A timeout closes the visible native UI and callback
    /// transport promptly, while this object retains its scope until the
    /// remaining operations finish.
    /// </summary>
    public ValueTask<CsWebUiBridgeCloseResult> CloseAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        Task<CsWebUiBridgeCloseResult> admission;
        lock (_closeGate)
            admission = _closeAdmission ??= BeginCloseAsync(timeout);
        return new(admission);
    }

    /// <summary>
    /// Starts an immediate close without waiting for accepted operations. Failures are
    /// traced; use <see cref="DisposeAsync"/> or <see cref="CloseAsync"/> to observe them.
    /// </summary>
    public void Dispose()
    {
        _ = ObserveCloseAsync();
    }

    private async Task ObserveCloseAsync()
    {
        try
        {
            var result = await CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
            await result.Completion.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError($"Closing the CS-WebUI Bridge window failed: {error}");
        }
    }

    /// <summary>Closes the window immediately and waits until its resources are released.</summary>
    public async ValueTask DisposeAsync()
    {
        var result = await CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
        await result.Completion.ConfigureAwait(false);
    }

    private async Task<CsWebUiBridgeCloseResult> BeginCloseAsync(TimeSpan timeout)
    {
        var errors = new List<Exception>();
        Capture(ReleaseAdmissionRoutes, errors);
        WindowContentSessionCloseResult result;
        try
        {
            result = await _content.BeginCloseAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
            errors.AddRange(await FinalizeResourcesAsync().ConfigureAwait(false));
            throw new AggregateException(errors);
        }
        if (result.Drained)
        {
            errors.AddRange(await FinalizeResourcesAsync().ConfigureAwait(false));
            return new(true, 0, CompletionFor(errors));
        }

        // A visible closed window must not leave command callbacks active.
        // The content session stays alive solely to own accepted operations.
        Capture(_transport.Dispose, errors);
        Capture(_window.Close, errors);
        Task completion;
        lock (_closeGate)
            completion = _closeCompletion ??= DrainThenFinalizeAsync(errors);
        return new(false, result.RemainingOperations, completion);
    }

    private async Task DrainThenFinalizeAsync(List<Exception> errors)
    {
        try
        {
            _ = await _content.BeginCloseAsync(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
        finally
        {
            errors.AddRange(await FinalizeResourcesAsync().ConfigureAwait(false));
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    private void ReleaseAdmissionRoutes()
    {
        lock (_closeGate)
        {
            if (_admissionReleased) return;
            _admissionReleased = true;
        }
        // Both releases are independent cleanup steps. In particular, an
        // application-provided attachment may throw from Dispose; that must
        // not retain the connection callback or prevent operation drain.
        var errors = new List<Exception>();
        Capture(_connectionBinding.Dispose, errors);
        if (_attachment is not null) Capture(_attachment.Dispose, errors);
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    private async Task<List<Exception>> FinalizeResourcesAsync()
    {
        lock (_closeGate)
        {
            if (_finalized) return [];
            _finalized = true;
        }
        var errors = new List<Exception>();
        Capture(_content.Dispose, errors);
        Capture(_transport.Dispose, errors);
        Capture(_window.Dispose, errors);
        if (_scope is IAsyncDisposable asynchronousScope)
            await CaptureAsync(asynchronousScope.DisposeAsync, errors).ConfigureAwait(false);
        else Capture(_scope.Dispose, errors);
        return errors;
    }

    private static Task CompletionFor(List<Exception> errors) => errors.Count switch
    {
        0 => Task.CompletedTask,
        1 => Task.FromException(errors[0]),
        _ => Task.FromException(new AggregateException(errors)),
    };

    private static void Capture(Action action, List<Exception> errors)
    {
        try { action(); }
        catch (Exception error) { errors.Add(error); }
    }

    private static async ValueTask CaptureAsync(Func<ValueTask> action, List<Exception> errors)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
    }
}

/// <summary>Admission result for a graceful CS-WebUI Bridge window close.</summary>
public sealed record CsWebUiBridgeCloseResult(bool Drained, int RemainingOperations, Task Completion);

/// <summary>Opens CS-WebUI Bridge windows from a service provider.</summary>
public static class CsWebUiBridgeWindowExtensions
{
    /// <summary>
    /// Creates the application Window in a new scope before attaching its
    /// generated ViewModel Bridge. The Window owns the host adapter lifetime.
    /// </summary>
    public static TWindow OpenWindow<TWindow, TViewModel>(this IServiceProvider services,
        Func<CsWebUiBridgeWindow<TViewModel>, TWindow> createWindow)
        where TWindow : RunicWindow<TViewModel>, IDisposable
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(createWindow);

        var scope = services.CreateScope();
        WebUiWindow? window = null;
        RebindableBridgeTransport? transport = null;
        WindowContentSession? content = null;
        WebUiBinding? connectionBinding = null;
        CsWebUiBridgeWindow<TViewModel>? host = null;
        TWindow? applicationWindow = null;
        try
        {
            var viewModel = scope.ServiceProvider.GetRequiredService<TViewModel>();
            var attachWithContent = scope.ServiceProvider.GetService<
                Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable>>();
            window = new WebUiWindow();
            transport = window.CreateBridgeSession();
            content = new WindowContentSession(transport,
                scope.ServiceProvider.GetService<IRunicViewLocator>(), rootModel: viewModel,
                modelContext: scope.ServiceProvider.GetService<IRunicModelContext>());
            connectionBinding = window.Bind("", e =>
            {
                if (e.EventType == WebUiEventType.Disconnected)
                    content.ReleaseConnection($"{e.ClientId}:{e.ConnectionId}");
            });
            host = new CsWebUiBridgeWindow<TViewModel>(
                scope, window, transport, content, connectionBinding, viewModel);
            applicationWindow = createWindow(host)
                ?? throw new InvalidOperationException("The Window factory returned null.");
            if (!ReferenceEquals(applicationWindow.DataContext, viewModel))
                throw new InvalidOperationException("The application Window must use its scoped ViewModel as DataContext.");
            var attachment = attachWithContent is not null
                ? attachWithContent(transport, content, viewModel)
                : scope.ServiceProvider.GetRequiredService<
                    Func<IBridgeTransport, TViewModel, IDisposable>>()(transport, viewModel);
            try { host.Attach(attachment); }
            catch
            {
                attachment.Dispose();
                throw;
            }
            return applicationWindow;
        }
        catch
        {
            if (host is not null)
            {
                try { applicationWindow?.Dispose(); }
                finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                throw;
            }
            try
            {
                connectionBinding?.Dispose();
            }
            finally
            {
                try { content?.Dispose(); }
                finally
                {
                    try { transport?.Dispose(); }
                    finally
                    {
                        try { window?.Dispose(); }
                        finally
                        {
                            if (scope is IAsyncDisposable asynchronousScope)
                                asynchronousScope.DisposeAsync().AsTask().GetAwaiter().GetResult();
                            else scope.Dispose();
                        }
                    }
                }
            }
            throw;
        }
    }
}
