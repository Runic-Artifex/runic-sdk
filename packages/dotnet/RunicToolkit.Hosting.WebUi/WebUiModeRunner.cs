using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace RunicToolkit.Hosting.WebUi;

/// <summary>Contains immutable browser and teardown configuration for UI mode.</summary>
public sealed record WebUiModeOptions
{
    /// <summary>Initializes required UI mode options.</summary>
    public WebUiModeOptions(
        BrowserHostOptions browserHost,
        BrowserWindowOptions browserWindow,
        TimeSpan sessionCloseTimeout,
        TimeSpan windowCloseTimeout)
    {
        BrowserHost = browserHost ?? throw new ArgumentNullException(nameof(browserHost));
        BrowserWindow = browserWindow ?? throw new ArgumentNullException(nameof(browserWindow));
        SessionCloseTimeout = RequirePositive(sessionCloseTimeout, nameof(sessionCloseTimeout));
        WindowCloseTimeout = RequirePositive(windowCloseTimeout, nameof(windowCloseTimeout));
    }

    /// <summary>Gets browser-runtime options.</summary>
    public BrowserHostOptions BrowserHost { get; }

    /// <summary>Gets browser-window options.</summary>
    public BrowserWindowOptions BrowserWindow { get; }

    /// <summary>Gets the root-session close bound.</summary>
    public TimeSpan SessionCloseTimeout { get; }

    /// <summary>Gets the window close bound.</summary>
    public TimeSpan WindowCloseTimeout { get; }

    private static TimeSpan RequirePositive(TimeSpan value, string parameterName) =>
        value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan
            ? throw new ArgumentOutOfRangeException(parameterName)
            : value;
}

/// <summary>
/// Executes the fixed runtime → window → root → navigation → show sequence and reverses
/// all acquired ownership after every injected failure.
/// </summary>
public sealed class WebUiModeRunner : IApplicationModeRunner
{
    private readonly IBrowserHostFactory _browserHostFactory;
    private readonly IRootSessionFactory _rootSessionFactory;
    private readonly FrontendAssetEndpoint _assets;
    private readonly IApplicationStopController _stopController;
    private readonly WebUiModeOptions _options;
    private readonly IWebUiWindowAttachment? _windowAttachment;

    /// <summary>Initializes the closed UI mode runner.</summary>
    public WebUiModeRunner(
        IBrowserHostFactory browserHostFactory,
        IRootSessionFactory rootSessionFactory,
        FrontendAssetEndpoint assets,
        IApplicationStopController stopController,
        WebUiModeOptions options)
        : this(
            browserHostFactory,
            rootSessionFactory,
            assets,
            stopController,
            options,
            windowAttachment: null)
    {
    }

    /// <summary>
    /// Initializes the UI runner with an optional lifecycle-bound desktop service attachment.
    /// </summary>
    public WebUiModeRunner(
        IBrowserHostFactory browserHostFactory,
        IRootSessionFactory rootSessionFactory,
        FrontendAssetEndpoint assets,
        IApplicationStopController stopController,
        WebUiModeOptions options,
        IWebUiWindowAttachment? windowAttachment)
    {
        _browserHostFactory = browserHostFactory
            ?? throw new ArgumentNullException(nameof(browserHostFactory));
        _rootSessionFactory = rootSessionFactory
            ?? throw new ArgumentNullException(nameof(rootSessionFactory));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _stopController = stopController ?? throw new ArgumentNullException(nameof(stopController));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _windowAttachment = windowAttachment;
    }

    /// <inheritdoc />
    public LaunchKind Kind => LaunchKind.UserInterface;

    /// <inheritdoc />
    public async Task<ApplicationRunResult> RunAsync(
        LaunchDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Kind != LaunchKind.UserInterface)
        {
            throw new ArgumentException("The WebUi runner accepts only UI launches.", nameof(decision));
        }

        IBrowserHost? browserHost = null;
        IBrowserWindow? window = null;
        IRootSession? rootSession = null;
        bool rootActive = false;
        ExceptionDispatchInfo? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        EventHandler? closeHandler = null;

        try
        {
            browserHost = await _browserHostFactory
                .CreateAsync(_options.BrowserHost, cancellationToken)
                .ConfigureAwait(false);
            await browserHost.Dispatcher
                .InvokeAsync(browserHost.InitializeAsync, cancellationToken)
                .ConfigureAwait(false);
            window = await browserHost.Dispatcher
                .InvokeAsync(
                    token => browserHost.CreateWindowAsync(_options.BrowserWindow, token),
                    cancellationToken)
                .ConfigureAwait(false);
            _windowAttachment?.Attach(browserHost, window);
            rootSession = await _rootSessionFactory
                .OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await rootSession.ActivateAsync(cancellationToken).ConfigureAwait(false);
            rootActive = true;

            IBrowserWindow callbackWindow = window;
            IBrowserHost callbackHost = browserHost;
            closeHandler = (_, _) => _ = SignalWindowCloseAsync(callbackHost.Dispatcher);
            callbackWindow.CloseRequested += closeHandler;

            await browserHost.Dispatcher
                .InvokeAsync(
                    token => callbackWindow.NavigateAsync(_assets.EntryPoint, token),
                    cancellationToken)
                .ConfigureAwait(false);
            await browserHost.Dispatcher
                .InvokeAsync(callbackWindow.ShowAsync, cancellationToken)
                .ConfigureAwait(false);
            await callbackWindow.WaitForCloseAsync(cancellationToken).ConfigureAwait(false);
            _stopController.RequestStop(StopReason.WindowClosed);
        }
        catch (OperationCanceledException) when (_stopController.Stopping.IsCancellationRequested)
        {
            // The kernel already selected the terminal stop source.
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            if (window is not null && closeHandler is not null)
            {
                window.CloseRequested -= closeHandler;
            }

            if (rootSession is not null)
            {
                if (rootActive)
                {
                    await CaptureCleanupAsync(
                        token => rootSession.DeactivateAsync(token),
                        _options.SessionCloseTimeout,
                        cleanupFailures).ConfigureAwait(false);
                }

                await CaptureCleanupAsync(
                    _ => rootSession.DisposeAsync(),
                    _options.SessionCloseTimeout,
                    cleanupFailures).ConfigureAwait(false);
            }

            if (window is not null)
            {
                if (_windowAttachment is not null)
                {
                    await CaptureCleanupAsync(
                        token => _windowAttachment.DetachAsync(window, token),
                        _options.WindowCloseTimeout,
                        cleanupFailures).ConfigureAwait(false);
                }

                if (browserHost is not null)
                {
                    await CaptureCleanupAsync(
                        token => browserHost.Dispatcher.InvokeAsync(window.CloseAsync, token),
                        _options.WindowCloseTimeout,
                        cleanupFailures).ConfigureAwait(false);
                }

                await CaptureCleanupAsync(
                    _ => window.DisposeAsync(),
                    _options.WindowCloseTimeout,
                    cleanupFailures).ConfigureAwait(false);
            }

            if (browserHost is not null)
            {
                await CaptureCleanupAsync(
                    _ => browserHost.DisposeAsync(),
                    _options.WindowCloseTimeout,
                    cleanupFailures).ConfigureAwait(false);
            }
        }

        primaryFailure?.Throw();
        if (cleanupFailures.Count != 0)
        {
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        }

        return ApplicationRunResult.FromExitCode(0);
    }

    private async Task SignalWindowCloseAsync(IUiDispatcher dispatcher)
    {
        try
        {
            await dispatcher.InvokeAsync(
                async token =>
                {
                    if (_windowAttachment is IWebUiNativeCloseNotification notification)
                    {
                        await notification.NativeWindowClosedAsync(token).ConfigureAwait(false);
                    }

                    _stopController.RequestStop(StopReason.WindowClosed);
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Native callback delivery cannot replace the lifecycle result.
        }
    }

    private static async Task CaptureCleanupAsync(
        Func<CancellationToken, ValueTask> operation,
        TimeSpan timeout,
        List<Exception> failures)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await operation(cancellation.Token).AsTask()
                .WaitAsync(timeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}
