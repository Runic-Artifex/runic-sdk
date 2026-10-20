using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;

namespace Runic.Application.Views.CsWebUi;

/// <summary>
/// Owns one CS-WebUI window, its DI scope, and its Bridge attachments.
/// <see cref="RunicCsWebUiHost"/> creates it; <see cref="RunicWindow{TViewModel}.Host"/> returns it.
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
    private Task? _webUiClosed;
    private readonly TaskCompletionSource _closeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? _attachment;
    private Task<RunicWindowCloseResult>? _closeAdmission;
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

    // Completes when the main thread's WebUI wait that covers this window returns. Without
    // RunicCsWebUiHost.Run there is none, and WaitForCloseAsync polls WebUI instead.
    internal Task? WebUiClosed
    {
        get => Volatile.Read(ref _webUiClosed);
        set => Volatile.Write(ref _webUiClosed, value);
    }

    /// <summary>The window's scoped root ViewModel.</summary>
    public TViewModel ViewModel { get; }

    /// <summary>The underlying CS-WebUI window.</summary>
    public WebUiWindow NativeWindow => _window;

    // Shows the content with the application's presentation, WebUI's recommended presentation (an installed
    // browser in app mode, then the system default browser, then the platform WebView) or only in the WebView.
    // The serve-only switch replaces each of them.
    internal void Show(string content, bool webView, Action<WebUiWindow, string>? show)
    {
        if (TryServeOnly(content)) return;
        if (show is not null) show(_window, content);
        else if (webView) _window.ShowWebView(content);
        else _window.Show(content);
    }

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
    /// <param name="timeout">How long accepted operations may run before the window closes anyway.</param>
    /// <param name="cancellationToken">Stops this caller's wait; the close continues.</param>
    public ValueTask<RunicWindowCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        Task<RunicWindowCloseResult> admission;
        lock (_closeGate)
            admission = _closeAdmission ??= BeginCloseAsync(timeout);
        _closeStarted.TrySetResult();
        return new(admission.WaitAsync(cancellationToken));
    }

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

    private async Task<RunicWindowCloseResult> BeginCloseAsync(TimeSpan timeout)
    {
        var errors = new List<Exception>();
        Capture(ReleaseAdmissionRoutes, errors);
        RunicWindowCloseResult result;
        try
        {
            result = await _content.DrainAsync(timeout).ConfigureAwait(false);
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

