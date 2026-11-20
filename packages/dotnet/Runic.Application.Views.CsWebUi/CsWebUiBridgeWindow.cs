using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;

namespace Runic.Application.Views.CsWebUi;

// The WebUI window of a CsWebUiBridgeWindow of any ViewModel type.
internal interface ICsWebUiNativeWindowSource
{
    WebUiWindow NativeWindow { get; }
}

/// <summary>
/// Owns one CS-WebUI window, its DI scope, and its Bridge attachments.
/// <see cref="RunicCsWebUiHost"/> creates it; <see cref="RunicWindow{TViewModel}.Host"/> returns it.
/// </summary>
/// <remarks>
/// Release it with <see cref="DisposeAsync"/> or <see cref="CloseAsync"/>. There is no
/// synchronous <c>Dispose</c>: accepted operations can keep the DI scope alive after
/// the visible window closes, as with every <see cref="IBridgeWindow"/> host.
/// </remarks>
public sealed class CsWebUiBridgeWindow<TViewModel> : IBridgeWindow, ICsWebUiNativeWindowSource where TViewModel : class
{
    private readonly object _closeGate = new();
    private readonly IServiceScope _scope;
    private readonly WebUiWindow _window;
    private readonly CsWebUiBridgeTransport _transport;
    private readonly WindowContentSession _content;
    private readonly IDisposable _connectionBinding;
    private Task? _webUiClosed;
    private readonly TaskCompletionSource _closeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? _attachment;
    private Task<RunicWindowCloseResult>? _closeAdmission;
    private Task? _closeCompletion;
    // RunicCloseDrain (sdk#1190): ends the visible phase of a drain early, and completes when it ended.
    private CancellationTokenSource? _endDrain;
    private readonly TaskCompletionSource _visibleEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _admissionReleased;
    private bool _finalized;

    internal CsWebUiBridgeWindow(
        IServiceScope scope,
        WebUiWindow window,
        CsWebUiBridgeTransport transport,
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

    // Completes when the main thread's WebUI wait that covers this window returns. Without
    // RunicCsWebUiHost.Run there is none, and WaitForCloseAsync polls WebUI instead.
    internal Task? WebUiClosed
    {
        get => Volatile.Read(ref _webUiClosed);
        set => Volatile.Write(ref _webUiClosed, value);
    }

    // sdk#1148: the guard of the user's close, when WebUI can keep this window open; the window's own close abandons it.
    internal CsWebUiUserClose? UserClose { get; set; }

    // sdk#1190: the window's drain when serving ends in serve-only mode (CsWebUiServeOnlyStop), released as it closes.
    internal IDisposable? ServeOnlyDrain { get; set; }

    // sdk#1149: the window's presentation, which reads its placement once more before the host closes the window.
    internal CsWebUiWindowPresentation? Presentation { get; set; }

    /// <summary>The window's scoped root ViewModel.</summary>
    public TViewModel ViewModel { get; }

    /// <summary>The underlying CS-WebUI window.</summary>
    public WebUiWindow Window => _window;

    WebUiWindow ICsWebUiNativeWindowSource.NativeWindow => _window;

    // Shows the content with the application's presentation, WebUI's recommended presentation (an installed
    // browser in app mode, then the system default browser, then the platform WebView) or only in the WebView.
    // The serve-only switch replaces each of them.
    internal void Show(string content, bool webView, Action<WebUiWindow, string>? show, Type? windowType = null)
    {
        if (TryServeOnly(content, windowType)) return;
        if (show is not null) show(_window, content);
        else if (webView) _window.ShowWebView(content);
        else _window.Show(content);
    }

    internal const string ServeOnlyEnvironmentVariable = "RUNIC_APPLICATION_SERVE_ONLY";

    private bool TryServeOnly(string content, Type? windowType)
    {
        if (Environment.GetEnvironmentVariable(ServeOnlyEnvironmentVariable) != "1") return false;
        // Zero waits for WebUiApplication.Exit rather than for a first browser
        // connection, so the server outlives connecting and closing clients.
        WebUiApplication.SetConnectionTimeout(0);
        var url = _window.StartServer(content);
        // One write, so another window's lines cannot come between the two (Runic.Application.Views.Desktop README,
        // "Automation"): the URL line older parsers read, then the window line that names the Window type.
        Console.Out.Write(ServeOnlyAnnouncement.Lines(url, windowType));
        Console.Out.Flush();
        // End of input (for example </dev/null) is not a stop request.
        // sdk#1190: the Windows with a close drain drain first (CsWebUiServeOnlyStop).
        new Thread(static () =>
        {
            if (Console.In.ReadLine() is not null) CsWebUiServeOnlyStop.Request();
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
    /// <param name="timeout">How long accepted operations may run before the window closes anyway.</param>
    /// <param name="cancellationToken">Stops this caller's wait; the close continues.</param>
    public ValueTask<RunicWindowCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        // A user close being decided ends: this close does not ask.
        UserClose?.Abandon();
        Task<RunicWindowCloseResult> admission;
        CancellationTokenSource? drain;
        lock (_closeGate)
        {
            drain = _endDrain;
            admission = _closeAdmission ??= BeginCloseAsync(timeout, draining: false, CancellationToken.None);
        }
        // The application's own close during a drain keeps its timeout: the visible window closes then at the latest.
        if (drain is not null && !_visibleEnded.Task.IsCompleted) EndDrainAfter(drain, timeout);
        else _closeStarted.TrySetResult();
        return new(admission.WaitAsync(cancellationToken));
    }

    private static void EndDrainAfter(CancellationTokenSource drain, TimeSpan timeout)
    {
        try
        {
            if (timeout == TimeSpan.Zero) drain.Cancel();
            else if (timeout != Timeout.InfiniteTimeSpan) drain.CancelAfter(timeout);
        }
        catch (ObjectDisposedException) { /* The drain ended meanwhile. */ }
    }

    // RunicCloseDrain on CS-WebUI (sdk#1190): the visible phase of a decided close, after the user's close that the
    // window refused (CsWebUiUserClose), RequestCloseAsync, or the end of serving in serve-only mode. Admission stops, the
    // pages get closing with draining: true and stay attached, and this returns once the Window's work has ended, the
    // timeout passed or endDrain was cancelled, and the window closed. A close of the application's own that started
    // first ends the visible window instead.
    internal Task DrainForCloseAsync(TimeSpan timeout, CancellationToken endDrain)
    {
        lock (_closeGate)
        {
            if (_closeAdmission is not null) return _closeStarted.Task;
            var end = _endDrain = CancellationTokenSource.CreateLinkedTokenSource(endDrain);
            _closeAdmission = BeginCloseAsync(timeout, draining: true, end.Token);
        }
        return _visibleEnded.Task;
    }

    // Whether a drain runs, between its decision and the end of its visible phase.
    internal bool IsDraining => Volatile.Read(ref _endDrain) is not null && !_visibleEnded.Task.IsCompleted;

    /// <summary>
    /// Completes when <see cref="CloseAsync"/> or <see cref="DisposeAsync"/> starts closing the window, or when WebUI
    /// reports that its windows have closed.
    /// </summary>
    /// <remarks>
    /// CS-WebUI reports closing for all of its windows at once: with several windows open, closing one browser
    /// window does not complete this wait until the others close too. With
    /// <see cref="RunicCsWebUiHost.Run(IServiceProvider, Func{RunicCsWebUiHost, Task{int}})"/> the process main thread
    /// waits for WebUI and reports it; otherwise this method polls <see cref="WebUiApplication.WaitAsync"/>.
    /// </remarks>
    /// <param name="cancellationToken">Stops the wait without closing the window.</param>
    public async ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var webUi = WebUiClosed?.WaitAsync(stop.Token) ?? WebUiApplication.WaitAsync(stop.Token);
        if (await Task.WhenAny(webUi, _closeStarted.Task).ConfigureAwait(false) == webUi)
        {
            await webUi.ConfigureAwait(false);
            return;
        }
        await stop.CancelAsync().ConfigureAwait(false);
        _ = webUi.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Closes the window immediately and waits until its resources are released.</summary>
    public async ValueTask DisposeAsync()
    {
        var result = await CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
        await result.Completion.ConfigureAwait(false);
    }

    // draining: the visible phase of a decided close with a drain (sdk#1190). The page stays attached while the window is
    // visible, so it receives the Window's state; without a drain it detaches at once.
    private async Task<RunicWindowCloseResult> BeginCloseAsync(TimeSpan timeout, bool draining, CancellationToken endDrain)
    {
        var errors = new List<Exception>();
        if (!draining) Capture(ReleaseAdmissionRoutes, errors);
        RunicWindowCloseResult result;
        try
        {
            try { result = await _content.DrainAsync(timeout, draining, endDrain).ConfigureAwait(false); }
            // The visible phase ended early: from here on a close without a timeout, which asks the remaining operations
            // to cancel, except durable ones.
            catch (OperationCanceledException) when (endDrain.IsCancellationRequested)
            {
                result = await _content.DrainAsync(TimeSpan.Zero, draining, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            errors.Add(error);
            if (draining) EndVisiblePhase(errors);
            errors.AddRange(await FinalizeResourcesAsync().ConfigureAwait(false));
            throw new AggregateException(errors);
        }
        if (draining) EndVisiblePhase(errors);
        if (result.Drained)
        {
            errors.AddRange(await FinalizeResourcesAsync().ConfigureAwait(false));
            return new(true, 0, CompletionFor(errors)) { PagesAcknowledged = result.PagesAcknowledged };
        }

        // A visible closed window must not leave command callbacks active.
        // The content session stays alive solely to own accepted operations.
        Capture(_transport.Dispose, errors);
        if (!draining)
        {
            Capture(EndPresentation, errors);
            Capture(_window.Close, errors);
        }
        Task completion;
        lock (_closeGate)
            completion = _closeCompletion ??= DrainThenFinalizeAsync(errors);
        return new(false, result.RemainingOperations, completion)
        {
            PagesAcknowledged = result.PagesAcknowledged,
            RemainingDurableWork = result.RemainingDurableWork,
        };
    }

    // The end of a drain's visible phase: the page detaches and the window closes through WebUiWindow.Close, which
    // CS-WebUI does not send through the close handler.
    private void EndVisiblePhase(List<Exception> errors)
    {
        Capture(ReleaseAdmissionRoutes, errors);
        Capture(EndPresentation, errors);
        Capture(_window.Close, errors);
        _closeStarted.TrySetResult();
        _visibleEnded.TrySetResult();
    }

    private async Task DrainThenFinalizeAsync(List<Exception> errors)
    {
        try
        {
            _ = await _content.DrainAsync(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
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
        if (ServeOnlyDrain is { } serveOnlyDrain) Capture(serveOnlyDrain.Dispose, errors);
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
        Capture(EndPresentation, errors);
        Capture(_window.Dispose, errors);
        if (_scope is IAsyncDisposable asynchronousScope)
            await CaptureAsync(asynchronousScope.DisposeAsync, errors).ConfigureAwait(false);
        else Capture(_scope.Dispose, errors);
        return errors;
    }

    // The placement as the window closes, then no more polling.
    private void EndPresentation()
    {
        if (Presentation is not { } presentation) return;
        presentation.Refresh();
        presentation.StopPolling();
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
