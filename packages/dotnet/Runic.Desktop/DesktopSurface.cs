namespace Runic.Desktop;

/// <summary>Owns content, capabilities, sessions, requests, and optional presentations for one namespace.</summary>
public sealed class DesktopSurface : IAsyncDisposable
{
    private readonly DesktopHost _host;
    private readonly Guid _id;
    private readonly WebUiWindow _engine;
    private readonly Internal.PresentationHostCore? _isolatedCore;
    private readonly SemaphoreSlim _windowGate = new(1, 1);
    private DesktopWindow? _window;
    private int _disposed;

    internal DesktopSurface(
        DesktopHost host,
        Guid id,
        WebUiWindow engine,
        Internal.PresentationHostCore? isolatedCore)
    {
        _host = host;
        _id = id;
        _engine = engine;
        _isolatedCore = isolatedCore;
    }

    /// <summary>Gets the surface URL.</summary>
    public Uri Url => _engine.Url ?? throw new ObjectDisposedException(nameof(DesktopSurface));

    /// <summary>Registers or replaces a named presentation capability.</summary>
    public PresentationCapabilityRegistration RegisterCapability(
        string capability,
        PresentationCapabilityHandler handler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        ArgumentNullException.ThrowIfNull(handler);
        var binding = _engine.BindAsync(capability, async (webUiEvent, token) =>
        {
            var invocation = new PresentationInvocation(this, webUiEvent);
            try
            {
                return (await handler(invocation, token).ConfigureAwait(false))
                    .ToCompatibilityResult();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _host.Report(new DesktopDiagnostic(
                    DesktopErrorCategory.OperationFailed,
                    "capability-failed",
                    "The presentation capability failed.",
                    Retryable: false,
                    CorrelationId: invocation.CorrelationId));
                return WebUiResult.None;
            }
        });
        return new PresentationCapabilityRegistration(binding);
    }

    /// <summary>Opens this surface in one installed browser or embedded WebView.</summary>
    public async ValueTask<DesktopWindow> OpenWindowAsync(
        DesktopWindowOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var configured = options ?? new DesktopWindowOptions();
        await _windowGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_window is { IsOpen: true })
            {
                throw new InvalidOperationException("This surface already has an open window presentation.");
            }

            ValidateWindowOptions(configured);
            ApplyWindowOptions(configured);
            var requestedBrowser = configured.Browser;
            var actualBrowser = requestedBrowser;
            var fellBack = false;
            if (configured.PresentationPolicy == DesktopPresentationPolicy.EmbeddedThenBrowser)
            {
                try
                {
                    await OpenCheckedAsync(BrowserKind.Embedded, cancellationToken).ConfigureAwait(false);
                    actualBrowser = BrowserKind.Embedded;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    fellBack = true;
                    var correlationId = exception is DesktopException desktopException
                        ? desktopException.CorrelationId
                        : Guid.NewGuid().ToString("N");
                    _host.Report(new DesktopDiagnostic(
                        DesktopErrorCategory.Unavailable,
                        "embedded-presentation-fallback",
                        "The embedded presentation was unavailable; the explicit browser fallback will be used.",
                        Retryable: true,
                        correlationId,
                        "Install the platform WebView prerequisite to restore the preferred presentation."));
                    actualBrowser = requestedBrowser == BrowserKind.Embedded ? BrowserKind.Any : requestedBrowser;
                    await OpenCheckedAsync(actualBrowser, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await OpenCheckedAsync(requestedBrowser, cancellationToken).ConfigureAwait(false);
            }

            // A concrete installed browser was selected successfully once its
            // process launched. Its process can exit before this async method
            // observes CurrentBrowser, which is intentionally reset during
            // cleanup; keep the presentation snapshot observable instead of
            // degrading it to Any because of that scheduling race.
            if (actualBrowser is BrowserKind.Any or BrowserKind.ChromiumBased)
            {
                actualBrowser = FromCompatibilityBrowser(_engine.CurrentBrowser);
            }
            _window = new DesktopWindow(this, _engine, requestedBrowser, actualBrowser, fellBack);
            return _window;
        }
        finally
        {
            _windowGate.Release();
        }
    }

    /// <summary>Runs JavaScript in every authenticated presentation session.</summary>
    public Task RunJavaScriptAsync(string script, CancellationToken cancellationToken = default) =>
        _engine.RunJavaScriptAsync(script, cancellationToken);

    /// <summary>Runs JavaScript and returns its string result from an authenticated presentation session.</summary>
    public Task<string> ExecuteJavaScriptAsync(
        string script,
        TimeSpan? timeout = null,
        int responseBufferSize = 4 * 1024,
        CancellationToken cancellationToken = default) =>
        _engine.ExecuteJavaScriptAsync(script, timeout, responseBufferSize, cancellationToken);

    /// <summary>Navigates every authenticated presentation session.</summary>
    public Task NavigateAsync(string url, CancellationToken cancellationToken = default) =>
        _engine.NavigateAsync(url, cancellationToken);

    /// <summary>Sends opaque bytes to every authenticated presentation session.</summary>
    public Task SendAsync(
        string function,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default) =>
        _engine.SendRawAsync(function, data, cancellationToken);

    /// <summary>Closes the surface without stopping a listener shared by other surfaces.</summary>
    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        var close = DisposeCoreAsync(detach: true, cancellationToken);
        return _engine.IsExecutingCallback ? ValueTask.CompletedTask : new ValueTask(close);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisposeCoreAsync(detach: true, CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    internal ValueTask DisposeFromHostAsync() => new(DisposeCoreAsync(detach: false, CancellationToken.None));

    internal bool IsExecutingCallback => _engine.IsExecutingCallback;

    internal bool IsCurrentWindow(DesktopWindow window) => ReferenceEquals(Volatile.Read(ref _window), window);

    internal async ValueTask RunWindowOperationAsync(DesktopWindow window,
        DesktopWindowCapabilities capability, Func<ValueTask> operation, CancellationToken cancellationToken)
    {
        await _windowGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ObjectDisposedException.ThrowIf(!ReferenceEquals(_window, window) || !window.IsOpen, window);
            window.RequireCapability(capability);
            // Keep replacement behind the admitted native operation, including queued dispatch.
            await operation().ConfigureAwait(false);
        }
        finally { _windowGate.Release(); }
    }

    internal async ValueTask<bool> RequestCloseWindowAsync(DesktopWindow window, CancellationToken cancellationToken)
    {
        Task<bool> decision;
        await _windowGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(_window, window) || !window.IsOpen) return true;
            if (!_engine.HasCloseConfirmation)
            {
                await _engine.ClosePresentationAsync().ConfigureAwait(false);
                _window = null;
                return true;
            }
            decision = _engine.RequestCloseAsync(cancellationToken);
        }
        finally
        {
            _windowGate.Release();
        }
        // The application's decision must not hold the gate needed by forced shutdown.
        return await decision.ConfigureAwait(false);
    }

    internal async ValueTask CloseWindowAsync(DesktopWindow window)
    {
        await _windowGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_window, window))
            {
                await _engine.ClosePresentationAsync().ConfigureAwait(false);
                _window = null;
            }
        }
        finally
        {
            _windowGate.Release();
        }
    }

    private async Task DisposeCoreAsync(bool detach, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Reject new admission first, then drain admitted mutations before native teardown.
        // Native release callbacks use the owner's dispatcher directly and do not acquire this gate.
        await _windowGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _engine.CloseAsync(cancellationToken).ConfigureAwait(false);
            await _engine.DisposeAsync().ConfigureAwait(false);
            if (_isolatedCore is not null)
            {
                await _isolatedCore.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _window = null;
            // Queued callers still need to acquire, observe disposal, and release. This
            // managed semaphore has no allocated wait handle and can be collected with the surface.
            _windowGate.Release();
            if (detach)
            {
                _host.Detach(_id);
            }
        }
    }

    private void ApplyWindowOptions(DesktopWindowOptions options)
    {
        _engine.ResetDesktopPresentationOptions();
        _engine.SetSize(options.Width, options.Height);
        if (options.MinimumWidth is { } minimumWidth && options.MinimumHeight is { } minimumHeight)
        {
            _engine.SetMinimumSize(minimumWidth, minimumHeight);
        }
        if (options.X is { } x && options.Y is { } y)
        {
            _engine.SetPosition(x, y);
        }
        if (options.Centered)
        {
            _engine.Center();
        }
        _engine.SetResizable(options.Resizable);
        _engine.SetFrameless(options.Frameless);
        _engine.SetTransparent(options.Transparent);
        _engine.SetHidden(options.Hidden);
        _engine.SetKiosk(options.Kiosk);
        if (options.HighContrast is { } highContrast)
        {
            _engine.SetHighContrast(highContrast);
        }
        if (options.IconFile is { } iconFile)
        {
            _engine.SetIconFile(iconFile);
        }
        if (options.ProfileName is not null || options.ProfilePath is not null)
        {
            _engine.SetProfile(options.ProfileName ?? string.Empty, options.ProfilePath ?? string.Empty);
        }
        if (options.ProxyServer is { } proxy)
        {
            _engine.SetProxy(proxy);
        }
        if (options.BrowserArguments is { } arguments)
        {
            _engine.SetCustomParameters(arguments);
        }
        _engine.SetAllowedPermissions(options.AllowedPermissions);
        _engine.SetCloseConfirmation(options.ConfirmCloseAsync);
    }

    private async Task OpenCheckedAsync(BrowserKind browser, CancellationToken cancellationToken)
    {
        var availability = _host.FindAvailability(browser);
        if (availability is null || !availability.IsAvailable)
        {
            var diagnostic = availability?.Diagnostic ?? new DesktopDiagnostic(
                DesktopErrorCategory.Unavailable,
                "presentation-unavailable",
                $"No supported presentation was discovered for {browser}.",
                Retryable: false,
                Remediation: "Install a supported browser or WebView runtime and run DesktopPlatform.GetAvailability().");
            var correlationId = Guid.NewGuid().ToString("N");
            diagnostic = diagnostic with { CorrelationId = correlationId };
            _host.Report(diagnostic);
            throw new DesktopException(
                diagnostic.Category,
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.Retryable,
                correlationId: correlationId);
        }

        try
        {
            await _engine.OpenPresentationAsync(ToCompatibilityBrowser(browser), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DesktopException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            _host.Report(new DesktopDiagnostic(
                DesktopErrorCategory.Unavailable,
                "presentation-start-failed",
                "The selected presentation could not be started.",
                Retryable: true,
                correlationId,
                "Review DesktopPlatform.GetAvailability(), install missing prerequisites, or select another presentation."));
            throw new DesktopException(
                DesktopErrorCategory.Unavailable,
                "presentation-start-failed",
                "The selected presentation could not be started.",
                retryable: true,
                exception,
                correlationId);
        }
    }

    internal static void ValidateWindowOptions(DesktopWindowOptions options)
    {
        if (options.ConfirmCloseAsync is not null &&
            (options.Browser != BrowserKind.Embedded || options.PresentationPolicy == DesktopPresentationPolicy.EmbeddedThenBrowser))
        {
            throw new ArgumentException("Close confirmation requires BrowserKind.Embedded without browser fallback.", nameof(options));
        }
        const DesktopPermissionGrant supported = DesktopPermissionGrant.MediaCapture;
        if (!Enum.IsDefined(options.Browser))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Browser contains an unsupported value.");
        }
        if (!Enum.IsDefined(options.PresentationPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PresentationPolicy contains an unsupported value.");
        }
        if ((options.AllowedPermissions & ~supported) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "AllowedPermissions contains an unsupported value.");
        }
    }

    private static WebUiBrowser ToCompatibilityBrowser(BrowserKind browser) => browser switch
    {
        BrowserKind.Any => WebUiBrowser.AnyBrowser,
        BrowserKind.Chrome => WebUiBrowser.Chrome,
        BrowserKind.Firefox => WebUiBrowser.Firefox,
        BrowserKind.Edge => WebUiBrowser.Edge,
        BrowserKind.Safari => WebUiBrowser.Safari,
        BrowserKind.Chromium => WebUiBrowser.Chromium,
        BrowserKind.Opera => WebUiBrowser.Opera,
        BrowserKind.Brave => WebUiBrowser.Brave,
        BrowserKind.Vivaldi => WebUiBrowser.Vivaldi,
        BrowserKind.Epic => WebUiBrowser.Epic,
        BrowserKind.Yandex => WebUiBrowser.Yandex,
        BrowserKind.ChromiumBased => WebUiBrowser.ChromiumBased,
        BrowserKind.Embedded => WebUiBrowser.WebView,
        _ => throw new ArgumentOutOfRangeException(nameof(browser)),
    };

    private static BrowserKind FromCompatibilityBrowser(WebUiBrowser browser) => browser switch
    {
        WebUiBrowser.Chrome => BrowserKind.Chrome,
        WebUiBrowser.Firefox => BrowserKind.Firefox,
        WebUiBrowser.Edge => BrowserKind.Edge,
        WebUiBrowser.Safari => BrowserKind.Safari,
        WebUiBrowser.Chromium => BrowserKind.Chromium,
        WebUiBrowser.Opera => BrowserKind.Opera,
        WebUiBrowser.Brave => BrowserKind.Brave,
        WebUiBrowser.Vivaldi => BrowserKind.Vivaldi,
        WebUiBrowser.Epic => BrowserKind.Epic,
        WebUiBrowser.Yandex => BrowserKind.Yandex,
        WebUiBrowser.WebView => BrowserKind.Embedded,
        _ => BrowserKind.Any,
    };
}

/// <summary>Represents one installed-browser or embedded-WebView presentation of a surface.</summary>
public sealed class DesktopWindow : IAsyncDisposable
{
    private readonly DesktopSurface _surface;
    private readonly WebUiWindow _engine;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal DesktopWindow(
        DesktopSurface surface,
        WebUiWindow engine,
        BrowserKind requestedBrowser,
        BrowserKind browser,
        bool fellBack)
    {
        _surface = surface;
        _engine = engine;
        RequestedBrowser = requestedBrowser;
        Browser = browser;
        FellBack = fellBack;
    }

    public BrowserKind RequestedBrowser { get; }
    public BrowserKind Browser { get; }
    public bool FellBack { get; }
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _surface.IsCurrentWindow(this)
        && (Browser == BrowserKind.Embedded ? _engine.IsEmbeddedWindowOpen : _engine.IsShown);
    public ulong ProcessId => IsOpen ? _engine.BrowserProcessId : 0;
    public nint NativeHandle => IsOpen ? _engine.NativeWindowHandle : 0;

    /// <summary>Whether this live embedded owner supports native-thread callbacks.</summary>
    public bool SupportsNativeDispatch => IsOpen && _engine.SupportsNativeDispatch;

    /// <summary>Whether the caller is executing on the live native owner's thread.</summary>
    public bool CheckNativeAccess() => SupportsNativeDispatch && _engine.CheckNativeAccess();

    /// <summary>Runs native work on this owner's thread. Cancellation prevents queued work;
    /// a running callback is awaited to completion. No native handle may outlive this window.</summary>
    public ValueTask DispatchNativeAsync(Action<nint> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!SupportsNativeDispatch) throw new NotSupportedException("The presentation has no native dispatcher.");
        return _engine.DispatchNativeAsync(() =>
        {
            ObjectDisposedException.ThrowIf(!IsOpen || NativeHandle == 0, this);
            action(NativeHandle);
        }, cancellationToken);
    }
    public DesktopWindowCapabilities Capabilities => IsOpen && Browser == BrowserKind.Embedded
        ? _engine.WindowCapabilities |
          (_engine.SupportsCloseConfirmation ? DesktopWindowCapabilities.CloseConfirmation : DesktopWindowCapabilities.None)
        : DesktopWindowCapabilities.None;

    public Task FocusAsync(CancellationToken cancellationToken = default)
    {
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.Focus,
            () => new ValueTask(_engine.FocusAsync(cancellationToken)), cancellationToken).AsTask();
    }

    public Task MinimizeAsync(CancellationToken cancellationToken = default)
    {
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.Minimize,
            () => new ValueTask(_engine.MinimizeAsync(cancellationToken)), cancellationToken).AsTask();
    }

    public Task ToggleMaximizedAsync(CancellationToken cancellationToken = default)
    {
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.Maximize,
            () => new ValueTask(_engine.MaximizeAsync(cancellationToken)), cancellationToken).AsTask();
    }

    public ValueTask ResizeAsync(uint width, uint height, CancellationToken cancellationToken = default) =>
        RequireAndResize(width, height, cancellationToken);

    public ValueTask MoveAsync(uint x, uint y, CancellationToken cancellationToken = default) =>
        RequireAndMove(x, y, cancellationToken);

    /// <summary>Blocks until this presentation closes while servicing platform window events.</summary>
    public void WaitForClose(CancellationToken cancellationToken = default)
    {
        while ((Volatile.Read(ref _disposed) != 0 && !_closed.Task.IsCompleted)
            || IsOpen)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsMacOS())
            {
                Internal.MacOsWkWebViewHost.ProcessPendingMainThreadWork();
            }
            _engine.ProcessEmbeddedHostEvents();
            Thread.Sleep(10);
        }

        // Native destruction can precede asynchronously queued native release work.
        // Join that cleanup while the process main thread can still service it.
        var close = CloseAsync().AsTask();
        while (!close.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsMacOS())
            {
                Internal.MacOsWkWebViewHost.ProcessPendingMainThreadWork();
            }
            _engine.ProcessEmbeddedHostEvents();
            Thread.Sleep(10);
        }
        close.GetAwaiter().GetResult();
    }

    /// <summary>Requests closing, awaiting the same confirmation as the native close button.</summary>
    /// <remarks>Cancelling the caller stops its wait, without cancelling another caller's shared decision.</remarks>
    public ValueTask<bool> RequestCloseAsync(CancellationToken cancellationToken = default)
        => IsOpen ? _surface.RequestCloseWindowAsync(this, cancellationToken) : ValueTask.FromResult(true);

    /// <summary>Forcibly closes this window without awaiting confirmation. Use RequestCloseAsync for user actions.</summary>
    public async ValueTask CloseAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                await _surface.CloseWindowAsync(this).ConfigureAwait(false);
                _closed.TrySetResult();
            }
            catch (Exception exception)
            {
                _closed.TrySetException(exception);
                throw;
            }
        }
        else
        {
            await _closed.Task.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private ValueTask RequireAndResize(uint width, uint height, CancellationToken cancellationToken)
    {
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.Resize,
            () => _engine.ResizePresentationAsync(width, height, cancellationToken), cancellationToken);
    }

    private ValueTask RequireAndMove(uint x, uint y, CancellationToken cancellationToken)
    {
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.Move,
            () => _engine.MovePresentationAsync(x, y, cancellationToken), cancellationToken);
    }

    internal void RequireCapability(DesktopWindowCapabilities capability)
    {
        if ((Capabilities & capability) == 0)
        {
            throw new DesktopException(
                DesktopErrorCategory.Unavailable,
                "window-capability-unavailable",
                "The requested platform-window capability is unavailable.");
        }
    }
}
