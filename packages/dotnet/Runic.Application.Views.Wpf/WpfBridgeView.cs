using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Navigation;

namespace Runic.Application.Views.Wpf;

/// <summary>Owns one child presentation and Bridge session while borrowing an application's existing model and services.</summary>
[Experimental(WpfDiagnostics.Id, UrlFormat = WpfDiagnostics.UrlFormat)]
public sealed class WpfBridgeView<TViewModel> : IBridgeWindow, IWpfBridgeView where TViewModel : class
{
    private readonly object _gate = new();
    private readonly DesktopBridgeTransport _transport;
    private readonly WindowContentSession _content;
    private readonly IDisposable _connections;
    private readonly IDisposable _attachment;
    private readonly ILogger _logger;
    private DesktopWindow? _presentation;
    private Task<BridgeWindowCloseResult>? _close;
    private readonly TaskCompletionSource _closeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _opening;

    internal WpfBridgeView(DesktopSurface surface, DesktopBridgeTransport transport, WindowContentSession content,
        IDisposable connections, IDisposable attachment, TViewModel viewModel, ILogger? logger = null)
    {
        _logger = logger ?? TraceFallbackLogger.Instance;
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
    object IWpfBridgeView.ViewModel => ViewModel;

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
        catch (Exception error) { WpfViewsLog.ChildViewCleanupFailed(_logger, error, WpfViewsLog.ErrorType(error)); }
    }

    /// <inheritdoc />
    public ValueTask<BridgeWindowCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) throw new ArgumentOutOfRangeException(nameof(timeout));
        TaskCompletionSource<BridgeWindowCloseResult> close;
        lock (_gate)
        {
            if (_close is not null) return new(_close.WaitAsync(cancellationToken));
            // Publish the close before running any of it: disposal and WebView2/COM calls can
            // pump messages and re-enter CloseAsync or DisposeAsync on this thread.
            close = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _close = close.Task;
        }
        _closeStarted.TrySetResult();
        _ = RunCloseAsync(close, timeout);
        return new(close.Task.WaitAsync(cancellationToken));
    }

    /// <summary>Completes when this view starts closing, including when its presentation closes.</summary>
    /// <param name="cancellationToken">Stops the wait without closing the view.</param>
    public ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default) =>
        new(_closeStarted.Task.WaitAsync(cancellationToken));

    private async Task RunCloseAsync(TaskCompletionSource<BridgeWindowCloseResult> close, TimeSpan timeout)
    {
        try { close.TrySetResult(await BeginCloseAsync(timeout).ConfigureAwait(false)); }
        catch (Exception error) { close.TrySetException(error); }
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
            try { await presentation.CloseAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
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

// A child View session whose model type is known only at run time, as in RunicViewHost.
internal interface IWpfBridgeView : IAsyncDisposable
{
    object ViewModel { get; }
    DesktopSurface Surface { get; }
    ValueTask OpenAsync(DesktopWindowOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Creates child View sessions from the application's existing service provider.</summary>
[Experimental(WpfDiagnostics.Id, UrlFormat = WpfDiagnostics.UrlFormat)]
public static class WpfBridgeViewExtensions
{
    private static readonly MethodInfo CreateTypedMethod = typeof(WpfBridgeViewExtensions)
        .GetMethod(nameof(CreateTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    // The Bridge registered for the model's class or its nearest base class; the generic factory for that class.
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Runic.Application.Views.Wpf is not trimmed or AOT-compiled; WPF isn't.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Runic.Application.Views.Wpf is not trimmed or AOT-compiled; WPF isn't.")]
    internal static Task<IWpfBridgeView> CreateWpfViewForModelAsync(this IServiceProvider services, DesktopHost desktop,
        DesktopSurfaceOptions surfaceOptions, object viewModel, IRunicModelContext? modelContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(viewModel);
        for (var type = viewModel.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            if (services.GetService(BridgeFactoryType(typeof(Func<,,,>), type)) is null
                && services.GetService(BridgeFactoryType(typeof(Func<,,>), type)) is null) continue;
            return (Task<IWpfBridgeView>)CreateTypedMethod.MakeGenericMethod(type)
                .Invoke(null, [services, desktop, surfaceOptions, viewModel, modelContext, cancellationToken])!;
        }
        throw new InvalidOperationException(
            $"No generated Bridge is registered for {viewModel.GetType().FullName} or its base classes. Declare a RunicView for it and call AddRunicViews().");
    }

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Runic.Application.Views.Wpf is not trimmed or AOT-compiled; WPF isn't.")]
    private static Type BridgeFactoryType(Type definition, Type model) => definition.GetGenericArguments().Length == 4
        ? definition.MakeGenericType(typeof(IBridgeTransport), typeof(WindowContentSession), model, typeof(IDisposable))
        : definition.MakeGenericType(typeof(IBridgeTransport), model, typeof(IDisposable));

    private static async Task<IWpfBridgeView> CreateTypedAsync<TViewModel>(IServiceProvider services, DesktopHost desktop,
        DesktopSurfaceOptions surfaceOptions, object viewModel, IRunicModelContext? modelContext, CancellationToken cancellationToken)
        where TViewModel : class =>
        await services.CreateWpfViewAsync(desktop, surfaceOptions, (TViewModel)viewModel, modelContext, cancellationToken).ConfigureAwait(false);

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
            // Lent, so a release after the application disposed its services logs to Trace instead of throwing.
            var loggerFactory = LentLoggerFactory.From(services);
            surface = await desktop.CreateSurfaceAsync(surfaceOptions, cancellationToken).ConfigureAwait(false);
            transport = new DesktopBridgeTransport(surface, loggerFactory?.CreateLogger(DesktopBridgeTransport.LogCategory));
            content = new WindowContentSession(transport, new WindowContentSessionOptions
            {
                RootModel = viewModel,
                ModelContext = modelContext ?? services.GetService<IRunicModelContext>(),
                ViewLocator = services.GetService<IRunicViewLocator>(),
                LoggerFactory = loggerFactory,
            });
            var session = content;
            connections = surface.SubscribeConnectionEvents(invocation =>
            {
                if (invocation.Kind == PresentationEventKind.Disconnected)
                    session.ReleaseConnection(invocation.Session.Id.ToString(CultureInfo.InvariantCulture));
            });
            var attachment = attachWithContent is not null ? attachWithContent(transport, content, viewModel) : attach!(transport, viewModel);
            return new(surface, transport, content, connections, attachment, viewModel, WpfViewsLog.For(loggerFactory));
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
