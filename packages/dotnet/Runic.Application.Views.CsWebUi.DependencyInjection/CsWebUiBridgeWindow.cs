using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;

namespace Runic.Application.Views.CsWebUi.DependencyInjection;

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

    public TViewModel ViewModel { get; }

    public WebUiWindow NativeWindow => _window;

    public void SetRootFolder(string path) => _window.SetRootFolder(path);

    public void SetSize(uint width, uint height) => _window.SetSize(width, height);

    public void SetPort(nuint port) => _window.SetPort(port);

    public void Show(string content) => _window.Show(content);

    public string StartServer(string content) => _window.StartServer(content);

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

    public void Dispose()
    {
        _ = CloseAsync(TimeSpan.Zero);
    }

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
            errors.AddRange(FinalizeResources());
            throw new AggregateException(errors);
        }
        if (result.Drained)
        {
            errors.AddRange(FinalizeResources());
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
            errors.AddRange(FinalizeResources());
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

    private List<Exception> FinalizeResources()
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
        Capture(_scope.Dispose, errors);
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
}

/// <summary>Admission result for a graceful CS-WebUI Bridge window close.</summary>
public sealed record CsWebUiBridgeCloseResult(bool Drained, int RemainingOperations, Task Completion);

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
        IDisposable? connectionBinding = null;
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
                scope.ServiceProvider.GetService<IRunicViewLocator>());
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
                        finally { scope.Dispose(); }
                    }
                }
            }
            throw;
        }
    }
}
