using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;

namespace Runic.Application.Views.CsWebUi;

/// <summary>
/// Owns one CS-WebUI window, its DI scope, and its Bridge attachments.
/// </summary>
/// <remarks>
/// Release it with <see cref="DisposeAsync"/> or <see cref="CloseAsync"/>. There is no
/// synchronous <c>Dispose</c>: accepted operations can keep the DI scope alive after
/// the visible window closes, as with every <see cref="IBridgeWindow"/> host.
/// </remarks>
public sealed class CsWebUiBridgeWindow<TViewModel> : IBridgeWindow where TViewModel : class
{
    private readonly object _closeGate = new();
    private readonly IServiceScope _scope;
    private readonly WebUiWindow _window;
    private readonly RebindableBridgeTransport _transport;
    private readonly WindowContentSession _content;
    private readonly IDisposable _connectionBinding;
    private IDisposable? _attachment;
    private Task<BridgeWindowCloseResult>? _closeAdmission;
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
    public ValueTask<BridgeWindowCloseResult> CloseAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        Task<BridgeWindowCloseResult> admission;
        lock (_closeGate)
            admission = _closeAdmission ??= BeginCloseAsync(timeout);
        return new(admission);
    }

    /// <summary>Closes the window immediately and waits until its resources are released.</summary>
    public async ValueTask DisposeAsync()
    {
        var result = await CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
        await result.Completion.ConfigureAwait(false);
    }

    private async Task<BridgeWindowCloseResult> BeginCloseAsync(TimeSpan timeout)
    {
        var errors = new List<Exception>();
        Capture(ReleaseAdmissionRoutes, errors);
        BridgeWindowCloseResult result;
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

/// <summary>Opens CS-WebUI Bridge windows from a service provider.</summary>
public static class CsWebUiBridgeWindowExtensions
{
    /// <summary>
    /// Creates the application Window in a new scope before attaching its
    /// generated ViewModel Bridge. The Window owns the host adapter lifetime.
    /// </summary>
    /// <exception cref="CsWebUiConfigurationException">
    /// No generated Bridge is registered for <typeparamref name="TViewModel"/>
    /// (<see cref="CsWebUiConfigurationException.BridgeNotRegisteredCode"/>). The check runs before the native
    /// window is created.
    /// </exception>
    public static TWindow OpenWindow<TWindow, TViewModel>(this IServiceProvider services,
        Func<CsWebUiBridgeWindow<TViewModel>, TWindow> createWindow)
        where TWindow : RunicWindow<TViewModel>, IAsyncDisposable
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(createWindow);
        // Fail before a scope or native window exists, naming the missing registration.
        if (IsBridgeRegistered<TViewModel>(services) == false)
            throw BridgeNotRegistered<TViewModel>(services);

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
            // Containers without IServiceProviderIsService are checked here, still before the native window.
            var attach = attachWithContent is null
                ? scope.ServiceProvider.GetService<Func<IBridgeTransport, TViewModel, IDisposable>>()
                    ?? throw BridgeNotRegistered<TViewModel>(services)
                : null;
            window = new WebUiWindow();
            transport = window.CreateBridgeSession();
            content = new WindowContentSession(transport,
                scope.ServiceProvider.GetService<IRunicViewLocator>(), rootModel: viewModel,
                modelContext: scope.ServiceProvider.GetService<IRunicModelContext>(),
                loggerFactory: scope.ServiceProvider.GetService<ILoggerFactory>());
            // Only the all-events binding receives disconnects. runic-cswebui.js
            // stops WebUI from also sending click events for elements with ids.
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
                : attach!(transport, viewModel);
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
                try { applicationWindow?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
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

    /// <summary>
    /// Checks at startup, before any window opens, that <paramref name="services"/> can open a Window for
    /// <typeparamref name="TViewModel"/>: its generated Bridge must be registered.
    /// </summary>
    /// <remarks>
    /// The check asks the container through <see cref="IServiceProviderIsService"/> and constructs nothing. A
    /// container without that service is not checked here; <see cref="OpenWindow{TWindow, TViewModel}"/> still
    /// reports a missing Bridge before it creates the native window. A failure is logged through the provider's
    /// <see cref="ILoggerFactory"/> (event 1050).
    /// </remarks>
    /// <exception cref="CsWebUiConfigurationException">
    /// No generated Bridge is registered (<see cref="CsWebUiConfigurationException.BridgeNotRegisteredCode"/>).
    /// </exception>
    public static void ValidateWindow<TViewModel>(this IServiceProvider services)
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(services);
        if (IsBridgeRegistered<TViewModel>(services) == false)
            throw BridgeNotRegistered<TViewModel>(services);
    }

    // Null when the container cannot answer without constructing services.
    private static bool? IsBridgeRegistered<TViewModel>(IServiceProvider services)
        where TViewModel : class
    {
        if (services.GetService(typeof(IServiceProviderIsService)) is not IServiceProviderIsService registered)
            return null;
        return registered.IsService(typeof(Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable>)) ||
            registered.IsService(typeof(Func<IBridgeTransport, TViewModel, IDisposable>));
    }

    private static CsWebUiConfigurationException BridgeNotRegistered<TViewModel>(IServiceProvider services)
        where TViewModel : class
    {
        var error = new CsWebUiConfigurationException(
            CsWebUiConfigurationException.BridgeNotRegisteredCode,
            $"No generated Bridge is registered for {typeof(TViewModel).FullName ?? typeof(TViewModel).Name}.",
            "Call services.AddRunicViews() (or AddRunicBridges()) from the generated " +
                "<Project>.RunicBridgeComposition class. If it is missing or has no Bridge for this ViewModel, " +
                "check that the project references Runic.Application.CsWebUi and that the ViewModel is a public, " +
                "top-level class implementing INotifyPropertyChanged.");
        if (services.GetService(typeof(ILoggerFactory)) is ILoggerFactory loggers)
        {
            CsWebUiLog.RegistrationMissing(loggers.CreateLogger(RunicViewsTelemetry.LogCategory),
                error.Code, error.DiagnosticMessage, error.Remediation);
        }
        return error;
    }
}
