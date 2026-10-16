using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Desktop;

namespace Runic.Application.Views.Desktop;

// The native owner of a DesktopBridgeWindow of any ViewModel type; null while it has no presentation, as in
// serve-only mode.
internal interface IDesktopNativeOwnerSource
{
    DesktopNativeOwner? NativeOwnerOrNull { get; }

    DesktopSurface Surface { get; }
}

/// <summary>Owns one Desktop surface, presentation, ViewModel scope, and Views session.</summary>
public sealed class DesktopBridgeWindow<TViewModel> : IBridgeWindow, IDesktopNativeOwnerSource where TViewModel : class
{
    private readonly object _closeGate = new();
    private readonly AsyncServiceScope _scope;
    private readonly DesktopSurface _surface;
    private readonly DesktopBridgeTransport _transport;
    private readonly WindowContentSession _content;
    private readonly IDisposable _connectionBinding;
    private IDisposable? _attachment;
    private DesktopWindow? _presentation;
    private DesktopNativeOwner? _nativeOwner;
    private DesktopWindowPlatformServices? _platformServices;
    private Task? _served;
    private Task<BridgeWindowCloseResult>? _close;
    private readonly TaskCompletionSource _closeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _completion;
    private bool _finalized;

    internal DesktopBridgeWindow(AsyncServiceScope scope, DesktopSurface surface, DesktopBridgeTransport transport,
        WindowContentSession content, IDisposable connectionBinding, TViewModel viewModel)
    {
        _scope = scope;
        _surface = surface;
        _transport = transport;
        _content = content;
        _connectionBinding = connectionBinding;
        ViewModel = viewModel;
    }

    /// <summary>The window's scoped root ViewModel.</summary>
    public TViewModel ViewModel { get; }
    /// <summary>The Desktop surface that hosts the window's web content.</summary>
    /// <remarks>
    /// <see cref="DesktopSurface.ExecuteJavaScriptAsync"/> runs script in the connected View, for example from an
    /// in-process test.
    /// </remarks>
    public DesktopSurface Surface => _surface;
    /// <summary>The opened Desktop window.</summary>
    /// <remarks>Under <c>RUNIC_APPLICATION_SERVE_ONLY=1</c> the surface serves without a presentation.</remarks>
    /// <exception cref="InvalidOperationException">The presentation has not opened yet.</exception>
    public DesktopWindow Presentation => Volatile.Read(ref _presentation) ??
        throw new InvalidOperationException("The Desktop presentation has not opened.");
    /// <summary>The native platform-service owner of the opened presentation.</summary>
    /// <remarks>
    /// Pass it to the platform provider for the window's backend for file dialogs, file launchers and clipboard
    /// access, or let <c>services.AddRunicPlatformServices()</c> bind <see cref="Runic.Platform.IWindowPlatformServices"/> to it.
    /// It is available while the embedded window is open. A presentation without native dispatch, such
    /// as an installed browser after an embedded-window fallback, still has an owner, but its
    /// <see cref="DesktopNativeOwner.IsAvailable"/> is <see langword="false"/>.
    /// Under <c>RUNIC_APPLICATION_SERVE_ONLY=1</c> the window has no presentation and so no owner; to bring a Window
    /// forward on any host and in either mode, use
    /// <see cref="PlatformServices.PresentAsync(IRunicWindow, Runic.Platform.DesktopActivationContext?, CancellationToken)"/>,
    /// which reports <see cref="Runic.Platform.PlatformUnavailableReason.OwnerUnavailable"/> without one.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The presentation has not opened, or the window serves without one.</exception>
    public DesktopNativeOwner NativeOwner => Volatile.Read(ref _nativeOwner) ??
        throw new InvalidOperationException(Volatile.Read(ref _served) is null
            ? "The Desktop presentation has not opened."
            : "The window serves without a presentation (RUNIC_APPLICATION_SERVE_ONLY), so it has no native owner.");

    DesktopNativeOwner? IDesktopNativeOwnerSource.NativeOwnerOrNull => Volatile.Read(ref _nativeOwner);

    internal void Attach(IDisposable attachment)
    {
        lock (_closeGate)
        {
            if (_attachment is not null || _close is not null)
                throw new InvalidOperationException("The Window Bridge is already attached or closing.");
            _attachment = attachment;
        }
    }

    // RUNIC_APPLICATION_SERVE_ONLY: the surface serves without a presentation, and WaitForCloseAsync also
    // returns when serving ends.
    internal void ServeUntil(Task stopped) => Volatile.Write(ref _served, stopped);

    internal void SetPresentation(DesktopWindow presentation)
    {
        lock (_closeGate)
        {
            if (_presentation is not null || _close is not null)
                throw new InvalidOperationException("The Desktop presentation is already open or closing.");
            var nativeOwner = new DesktopNativeOwner(presentation);
            // Lock-free readers see the owner no later than the presentation.
            Volatile.Write(ref _nativeOwner, nativeOwner);
            Volatile.Write(ref _presentation, presentation);
            // The scope's platform services, which its ViewModels may already hold, now follow this window.
            _platformServices = _scope.ServiceProvider.GetService<DesktopWindowPlatformServices>();
            _platformServices?.Bind(nativeOwner);
        }
    }

    /// <inheritdoc />
    public ValueTask<BridgeWindowCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        Task<BridgeWindowCloseResult> close;
        lock (_closeGate) close = _close ??= BeginCloseAsync(timeout);
        _closeStarted.TrySetResult();
        return new(close.WaitAsync(cancellationToken));
    }

    /// <inheritdoc />
    /// <remarks>
    /// On macOS and with GTK 4 the process main thread must run <see cref="DesktopEventLoop"/>, which services the
    /// native window while the application awaits; <c>RunicDesktopHost.Run</c> does.
    /// </remarks>
    public async ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _presentation) is not { } presentation)
        {
            var ended = Volatile.Read(ref _served) is { } served
                ? Task.WhenAny(_closeStarted.Task, served)
                : _closeStarted.Task;
            await ended.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var closed = presentation.WaitForCloseAsync(stop.Token).AsTask();
        if (await Task.WhenAny(closed, _closeStarted.Task).ConfigureAwait(false) == closed)
        {
            // The user closed the window, the wait failed, or the caller cancelled it.
            await closed.ConfigureAwait(false);
            return;
        }
        // This window's close started first: stop the presentation's poll.
        await stop.CancelAsync().ConfigureAwait(false);
        _ = closed.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
        Capture(_connectionBinding.Dispose, errors);
        if (_attachment is not null) Capture(_attachment.Dispose, errors);

        BridgeWindowCloseResult result;
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

        // A visible closed window must not leave command routes active. The
        // surface stays open solely so accepted operations can finish.
        Capture(_transport.Dispose, errors);
        // The visible presentation closes promptly, while the scope remains
        // alive until operations accepted before close reach a terminal result.
        // Platform services stop with the presentation: file access ends when
        // the window closes, even for operations that are still draining.
        await StopPlatformServicesAsync(errors).ConfigureAwait(false);
        if (_presentation is { } presentation)
            await CaptureAsync(() => presentation.CloseAsync(), errors).ConfigureAwait(false);
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
        Capture(_transport.Dispose, errors);
        // Release provider resources, such as owned clipboard data, while the native window may still be open.
        await StopPlatformServicesAsync(errors).ConfigureAwait(false);
        if (_presentation is not null)
            await CaptureAsync(_presentation.DisposeAsync, errors).ConfigureAwait(false);
        await CaptureAsync(_surface.DisposeAsync, errors).ConfigureAwait(false);
        await CaptureAsync(_scope.DisposeAsync, errors).ConfigureAwait(false);
    }

    // Idempotent: the platform services join their first shutdown.
    private Task StopPlatformServicesAsync(List<Exception> errors)
    {
        DesktopWindowPlatformServices? services;
        lock (_closeGate) services = _platformServices;
        return services is null ? Task.CompletedTask : CaptureAsync(services.DisposeAsync, errors);
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

