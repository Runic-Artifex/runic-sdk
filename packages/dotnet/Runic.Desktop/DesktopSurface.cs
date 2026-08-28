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
            try
            {
                return (await handler(new PresentationInvocation(this, webUiEvent), token).ConfigureAwait(false))
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
                    Retryable: false));
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
            if (_window is { IsOpen: true })
            {
                throw new InvalidOperationException("This surface already has an open window presentation.");
            }

            ApplyWindowOptions(configured);
            await _engine.OpenPresentationAsync(ToCompatibilityBrowser(configured.Browser), cancellationToken)
                .ConfigureAwait(false);
            _window = new DesktopWindow(this, _engine, configured.Browser);
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
            _windowGate.Dispose();
            if (detach)
            {
                _host.Detach(_id);
            }
        }
    }

    private void ApplyWindowOptions(DesktopWindowOptions options)
    {
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
}

/// <summary>Represents one installed-browser or embedded-WebView presentation of a surface.</summary>
public sealed class DesktopWindow : IAsyncDisposable
{
    private readonly DesktopSurface _surface;
    private readonly WebUiWindow _engine;
    private int _disposed;

    internal DesktopWindow(DesktopSurface surface, WebUiWindow engine, BrowserKind browser)
    {
        _surface = surface;
        _engine = engine;
        Browser = browser;
    }

    public BrowserKind Browser { get; }
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _engine.IsShown;
    public ulong ProcessId => _engine.BrowserProcessId;
    public nint NativeHandle => _engine.NativeWindowHandle;
    public DesktopWindowCapabilities Capabilities => Browser == BrowserKind.Embedded
        ? DesktopWindowCapabilities.NativeHandle |
          DesktopWindowCapabilities.Focus |
          DesktopWindowCapabilities.Minimize |
          DesktopWindowCapabilities.Maximize |
          DesktopWindowCapabilities.Resize |
          DesktopWindowCapabilities.Move
        : DesktopWindowCapabilities.None;

    public Task FocusAsync(CancellationToken cancellationToken = default)
    {
        RequireCapability(DesktopWindowCapabilities.Focus);
        return _engine.FocusAsync(cancellationToken);
    }

    public Task MinimizeAsync(CancellationToken cancellationToken = default)
    {
        RequireCapability(DesktopWindowCapabilities.Minimize);
        return _engine.MinimizeAsync(cancellationToken);
    }

    public Task ToggleMaximizedAsync(CancellationToken cancellationToken = default)
    {
        RequireCapability(DesktopWindowCapabilities.Maximize);
        return _engine.MaximizeAsync(cancellationToken);
    }

    public ValueTask ResizeAsync(uint width, uint height, CancellationToken cancellationToken = default) =>
        RequireAndResize(width, height, cancellationToken);

    public ValueTask MoveAsync(uint x, uint y, CancellationToken cancellationToken = default) =>
        RequireAndMove(x, y, cancellationToken);

    public async ValueTask CloseAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _surface.CloseWindowAsync(this).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private ValueTask RequireAndResize(uint width, uint height, CancellationToken cancellationToken)
    {
        RequireCapability(DesktopWindowCapabilities.Resize);
        return _engine.ResizePresentationAsync(width, height, cancellationToken);
    }

    private ValueTask RequireAndMove(uint x, uint y, CancellationToken cancellationToken)
    {
        RequireCapability(DesktopWindowCapabilities.Move);
        return _engine.MovePresentationAsync(x, y, cancellationToken);
    }

    private void RequireCapability(DesktopWindowCapabilities capability)
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
