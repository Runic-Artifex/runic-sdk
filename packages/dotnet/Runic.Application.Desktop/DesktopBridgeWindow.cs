using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Desktop;

namespace Runic.Application.Views.Desktop;

/// <summary>Owns one Desktop surface, presentation, ViewModel scope, and Views session.</summary>
public sealed class DesktopBridgeWindow<TViewModel> : IAsyncDisposable where TViewModel : class
{
    private readonly object _closeGate = new();
    private readonly AsyncServiceScope _scope;
    private readonly DesktopSurface _surface;
    private readonly WindowContentSession _content;
    private readonly IDisposable _connectionBinding;
    private IDisposable? _attachment;
    private DesktopWindow? _presentation;
    private Task<DesktopBridgeCloseResult>? _close;
    private Task? _completion;
    private bool _finalized;

    internal DesktopBridgeWindow(AsyncServiceScope scope, DesktopSurface surface,
        WindowContentSession content, IDisposable connectionBinding, TViewModel viewModel)
    {
        _scope = scope;
        _surface = surface;
        _content = content;
        _connectionBinding = connectionBinding;
        ViewModel = viewModel;
    }

    public TViewModel ViewModel { get; }
    public DesktopSurface Surface => _surface;
    public DesktopWindow Presentation => _presentation ??
        throw new InvalidOperationException("The Desktop presentation has not opened.");

    internal void Attach(IDisposable attachment)
    {
        lock (_closeGate)
        {
            if (_attachment is not null || _close is not null)
                throw new InvalidOperationException("The Window Bridge is already attached or closing.");
            _attachment = attachment;
        }
    }

    internal void SetPresentation(DesktopWindow presentation)
    {
        lock (_closeGate)
        {
            if (_presentation is not null || _close is not null)
                throw new InvalidOperationException("The Desktop presentation is already open or closing.");
            _presentation = presentation;
        }
    }

    /// <summary>Rejects new operations, then waits up to the requested drain timeout.</summary>
    public ValueTask<DesktopBridgeCloseResult> CloseAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_closeGate) return new(_close ??= BeginCloseAsync(timeout));
    }

    public async ValueTask DisposeAsync()
    {
        var result = await CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
        await result.Completion.ConfigureAwait(false);
    }

    private async Task<DesktopBridgeCloseResult> BeginCloseAsync(TimeSpan timeout)
    {
        var errors = new List<Exception>();
        Capture(_connectionBinding.Dispose, errors);
        if (_attachment is not null) Capture(_attachment.Dispose, errors);

        WindowContentSessionCloseResult result;
        try { result = await _content.BeginCloseAsync(timeout).ConfigureAwait(false); }
        catch (Exception error)
        {
            errors.Add(error);
            await FinalizeAsync(errors).ConfigureAwait(false);
            throw new AggregateException(errors);
        }

        if (result.Drained)
        {
            await FinalizeAsync(errors).ConfigureAwait(false);
            return new(true, 0, CompletionFor(errors));
        }

        // The visible presentation closes promptly, while the scope remains
        // alive until operations accepted before close reach a terminal result.
        if (_presentation is not null)
            await CaptureAsync(_presentation.CloseAsync, errors).ConfigureAwait(false);
        Task completion;
        lock (_closeGate) completion = _completion ??= DrainThenFinalizeAsync(errors);
        return new(false, result.RemainingOperations, completion);
    }

    private async Task DrainThenFinalizeAsync(List<Exception> errors)
    {
        try { _ = await _content.BeginCloseAsync(Timeout.InfiniteTimeSpan).ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        await FinalizeAsync(errors).ConfigureAwait(false);
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    private async Task FinalizeAsync(List<Exception> errors)
    {
        lock (_closeGate)
        {
            if (_finalized) return;
            _finalized = true;
        }
        Capture(_content.Dispose, errors);
        if (_presentation is not null)
            await CaptureAsync(_presentation.DisposeAsync, errors).ConfigureAwait(false);
        await CaptureAsync(_surface.DisposeAsync, errors).ConfigureAwait(false);
        await CaptureAsync(_scope.DisposeAsync, errors).ConfigureAwait(false);
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

    private static async Task CaptureAsync(Func<ValueTask> action, List<Exception> errors)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
    }
}

/// <summary>Admission result for a Desktop Views window close.</summary>
public sealed record DesktopBridgeCloseResult(bool Drained, int RemainingOperations, Task Completion);

public static class DesktopBridgeWindowExtensions
{
    /// <summary>Creates an application Window in a new scope, then opens its Desktop presentation.</summary>
    public static async ValueTask<TWindow> OpenDesktopWindowAsync<TWindow, TViewModel>(
        this IServiceProvider services,
        DesktopHost desktop,
        DesktopSurfaceOptions surfaceOptions,
        Func<DesktopBridgeWindow<TViewModel>, TWindow> createWindow,
        DesktopWindowOptions? windowOptions = null,
        CancellationToken cancellationToken = default)
        where TWindow : RunicWindow<TViewModel>, IAsyncDisposable
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(surfaceOptions);
        ArgumentNullException.ThrowIfNull(createWindow);

        var scope = services.CreateAsyncScope();
        DesktopSurface? surface = null;
        WindowContentSession? content = null;
        IDisposable? connectionBinding = null;
        DesktopBridgeWindow<TViewModel>? owner = null;
        TWindow? applicationWindow = null;
        try
        {
            var viewModel = scope.ServiceProvider.GetRequiredService<TViewModel>();
            var attachWithContent = scope.ServiceProvider.GetService<
                Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable>>();
            surface = await desktop.CreateSurfaceAsync(surfaceOptions, cancellationToken).ConfigureAwait(false);
            var transport = new DesktopBridgeTransport(surface);
            content = new WindowContentSession(transport,
                scope.ServiceProvider.GetService<IRunicViewLocator>());
            connectionBinding = surface.SubscribeConnectionEvents(invocation =>
            {
                if (invocation.Kind == PresentationEventKind.Disconnected)
                    content.ReleaseConnection(invocation.Session.Id.ToString(CultureInfo.InvariantCulture));
            });
            owner = new DesktopBridgeWindow<TViewModel>(scope, surface, content, connectionBinding, viewModel);
            applicationWindow = createWindow(owner)
                ?? throw new InvalidOperationException("The Window factory returned null.");
            if (!ReferenceEquals(applicationWindow.DataContext, viewModel))
                throw new InvalidOperationException("The application Window must use its scoped ViewModel as DataContext.");
            var attachment = attachWithContent is not null
                ? attachWithContent(transport, content, viewModel)
                : scope.ServiceProvider.GetRequiredService<
                    Func<IBridgeTransport, TViewModel, IDisposable>>()(transport, viewModel);
            try { owner.Attach(attachment); }
            catch { attachment.Dispose(); throw; }
            owner.SetPresentation(await surface.OpenWindowAsync(windowOptions, cancellationToken).ConfigureAwait(false));
            return applicationWindow;
        }
        catch
        {
            if (owner is not null)
            {
                try { if (applicationWindow is not null) await applicationWindow.DisposeAsync().ConfigureAwait(false); }
                finally { await owner.DisposeAsync().ConfigureAwait(false); }
            }
            else
            {
                connectionBinding?.Dispose();
                content?.Dispose();
                if (surface is not null) await surface.DisposeAsync().ConfigureAwait(false);
                await scope.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }
}
