using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Navigation;

namespace Runic.Application.Views.Wpf;

/// <summary>Owns one child presentation and Bridge session while borrowing an application's existing model and services.</summary>
[Experimental(WpfDiagnostics.Id)]
public sealed class WpfBridgeView<TViewModel> : IBridgeWindow where TViewModel : class
{
    private readonly object _gate = new();
    private readonly DesktopBridgeTransport _transport;
    private readonly WindowContentSession _content;
    private readonly IDisposable _connections;
    private readonly IDisposable _attachment;
    private DesktopWindow? _presentation;
    private Task<BridgeWindowCloseResult>? _close;
    private bool _opening;

    internal WpfBridgeView(DesktopSurface surface, DesktopBridgeTransport transport, WindowContentSession content,
        IDisposable connections, IDisposable attachment, TViewModel viewModel)
    {
        Surface = surface;
        ViewModel = viewModel;
        _transport = transport;
        _content = content;
        _connections = connections;
        _attachment = attachment;
        Surface.PresentationClosed += OnPresentationClosed;
    }

    /// <summary>The exact application-owned model supplied during creation.</summary>
    public TViewModel ViewModel { get; }
    /// <summary>The existing Desktop runtime surface serving this child presentation.</summary>
    public DesktopSurface Surface { get; }

    /// <summary>Opens the child presentation after its RunicWebView enters the loaded WPF visual tree.</summary>
    /// <remarks>Only the embedded presentation is accepted. Native window layout belongs to WPF.</remarks>
    public async ValueTask OpenAsync(DesktopWindowOptions? options = null, CancellationToken cancellationToken = default)
    {
        var configured = options ?? new DesktopWindowOptions { Browser = BrowserKind.Embedded };
        if (configured.Browser != BrowserKind.Embedded || configured.PresentationPolicy != DesktopPresentationPolicy.RequestedOnly)
            throw new ArgumentException("A WPF child view requires the embedded presentation without browser fallback.", nameof(options));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_close is not null, this);
            if (_opening || _presentation is not null) throw new InvalidOperationException("The child view is already opening or open.");
            _opening = true;
        }
        DesktopWindow presentation;
        try { presentation = await Surface.OpenWindowAsync(configured, cancellationToken).ConfigureAwait(false); }
        catch { lock (_gate) _opening = false; throw; }
        bool closing;
        lock (_gate)
        {
            _opening = false;
            closing = _close is not null;
            if (!closing) _presentation = presentation;
        }
        if (closing)
        {
            await presentation.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(WpfBridgeView<TViewModel>));
        }
    }

    private void OnPresentationClosed(object? sender, EventArgs args) => _ = CloseWhenUnloadedAsync();

    private async Task CloseWhenUnloadedAsync()
    {
        try
        {
            var result = await CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
            await result.Completion.ConfigureAwait(false);
        }
        catch (Exception error) { Trace.TraceError($"WPF child view cleanup failed: {error}"); }
    }

    /// <inheritdoc />
    public ValueTask<BridgeWindowCloseResult> CloseAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_gate) return new(_close ??= BeginCloseAsync(timeout));
    }

    /// <summary>Releases owned presentation resources after accepted operations finish; borrowed models and services stay alive.</summary>
    public async ValueTask DisposeAsync()
    {
        var result = await CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
        await result.Completion.ConfigureAwait(false);
    }

    private async Task<BridgeWindowCloseResult> BeginCloseAsync(TimeSpan timeout)
    {
        List<Exception> errors = [];
        Surface.PresentationClosed -= OnPresentationClosed;
        Capture(_connections.Dispose);
        Capture(_attachment.Dispose);
        BridgeWindowCloseResult result;
        try { result = await _content.BeginCloseAsync(timeout).ConfigureAwait(false); }
        catch (Exception error)
        {
            errors.Add(error);
            await FinalizeAsync().ConfigureAwait(false);
            throw new AggregateException(errors);
        }
        Capture(_transport.Dispose);
        if (_presentation is { } presentation)
            try { await presentation.CloseAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        var completion = CompleteAsync(result.Completion);
        return new(result.Drained, result.RemainingOperations, completion);

        void Capture(Action action)
        {
            try { action(); } catch (Exception error) { errors.Add(error); }
        }
        async Task FinalizeAsync()
        {
            Capture(_content.Dispose);
            Capture(_transport.Dispose);
            try { await Surface.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        }
        async Task CompleteAsync(Task drained)
        {
            try { await drained.ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
            await FinalizeAsync().ConfigureAwait(false);
            if (errors.Count > 0) throw new AggregateException(errors);
        }
    }
}

/// <summary>Creates child View sessions from the application's existing service provider.</summary>
[Experimental(WpfDiagnostics.Id)]
public static class WpfBridgeViewExtensions
{
    /// <summary>Attaches the generated Bridge to an existing model without creating a model, DI scope, or navigation engine.</summary>
    /// <remarks>The caller owns services, model, model context, and DesktopHost. This result owns only its surface and session.</remarks>
    public static async ValueTask<WpfBridgeView<TViewModel>> CreateWpfViewAsync<TViewModel>(
        this IServiceProvider services, DesktopHost desktop, DesktopSurfaceOptions surfaceOptions,
        TViewModel viewModel, IRunicModelContext? modelContext = null, CancellationToken cancellationToken = default)
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(surfaceOptions);
        ArgumentNullException.ThrowIfNull(viewModel);
        cancellationToken.ThrowIfCancellationRequested();
        // Resolve the Bridge factory before allocating a listener or model lease.
        var attachWithContent = services.GetService<Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable>>();
        var attach = attachWithContent is null ? services.GetService<Func<IBridgeTransport, TViewModel, IDisposable>>() : null;
        if (attachWithContent is null && attach is null)
            throw new InvalidOperationException($"No generated Bridge is registered for {typeof(TViewModel).FullName}. Call AddRunicViews().");
        DesktopSurface? surface = null;
        DesktopBridgeTransport? transport = null;
        WindowContentSession? content = null;
        IDisposable? connections = null;
        try
        {
            surface = await desktop.CreateSurfaceAsync(surfaceOptions, cancellationToken).ConfigureAwait(false);
            transport = new DesktopBridgeTransport(surface);
            content = new WindowContentSession(transport, new WindowContentSessionOptions
            {
                RootModel = viewModel,
                ModelContext = modelContext ?? services.GetService<IRunicModelContext>(),
                ViewLocator = services.GetService<IRunicViewLocator>(),
                LoggerFactory = services.GetService<ILoggerFactory>(),
            });
            var session = content;
            connections = surface.SubscribeConnectionEvents(invocation =>
            {
                if (invocation.Kind == PresentationEventKind.Disconnected)
                    session.ReleaseConnection(invocation.Session.Id.ToString(CultureInfo.InvariantCulture));
            });
            var attachment = attachWithContent is not null ? attachWithContent(transport, content, viewModel) : attach!(transport, viewModel);
            return new(surface, transport, content, connections, attachment, viewModel);
        }
        catch
        {
            try { connections?.Dispose(); }
            finally
            {
                try { content?.Dispose(); }
                finally
                {
                    try { transport?.Dispose(); }
                    finally { if (surface is not null) await surface.DisposeAsync().ConfigureAwait(false); }
                }
            }
            throw;
        }
    }
}
