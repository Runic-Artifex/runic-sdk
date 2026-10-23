using System.Collections.Immutable;
using System.Text;
using Runic.Platform;

namespace Runic.Desktop;

/// <summary>Owns content, capabilities, sessions, requests, and optional presentations for one namespace.</summary>
public sealed class DesktopSurface : IAsyncDisposable
{
    private const string ConnectionTimeoutCode = "presentation-connection-timeout";
    private const string ConnectionTimeoutMessage =
        "The presentation opened, but its page did not connect before the connection timeout.";

    private readonly DesktopHost _host;
    private readonly Guid _id;
    private readonly WebUiWindow _engine;
    private readonly Internal.PresentationHostCore? _isolatedCore;
    // The surface's own embedded-window factory, or null for the host's DesktopHostOptions.WindowHostFactory.
    private readonly IDesktopWindowHostFactory? _windowHostFactory;
    private readonly SemaphoreSlim _windowGate = new(1, 1);
    private DesktopWindow? _window;
    private int _disposed;

    internal DesktopSurface(
        DesktopHost host,
        Guid id,
        WebUiWindow engine,
        Internal.PresentationHostCore? isolatedCore,
        IDesktopWindowHostFactory? windowHostFactory = null)
    {
        _host = host;
        _id = id;
        _engine = engine;
        _isolatedCore = isolatedCore;
        _windowHostFactory = windowHostFactory;
    }

    /// <summary>Gets the surface URL.</summary>
    public Uri Url => _engine.Url ?? throw new ObjectDisposedException(nameof(DesktopSurface));

    /// <summary>Registers or replaces a named presentation capability.</summary>
    /// <remarks>
    /// Each browser connection admits one invocation or connection event at a time, but only until the
    /// handler first awaits an incomplete task. The rest of the handler can run concurrently with other
    /// invocations and connection events, so a long wait leaves Cancel and other control calls available.
    /// Invocations are not guaranteed to start in arrival order, and replies are sent as handlers complete.
    /// A handler that needs exclusive access across awaits must serialize itself, for example with a
    /// <see cref="SemaphoreSlim"/>. <see cref="PresentationEventKind.Disconnected"/> follows the completion
    /// of every invocation the connection accepted.
    /// </remarks>
    internal PresentationCapabilityRegistration RegisterCapability(
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

    /// <summary>Observes the pages that connect to this surface and disconnect from it.</summary>
    /// <param name="handler">Called for each event, on a thread-pool thread.</param>
    /// <returns>A subscription; dispose it to stop observing.</returns>
    /// <remarks>
    /// A page connects once it has authenticated with the surface's session credential. The events of one page run
    /// one at a time, <see cref="DesktopConnectionEventKind.Connected"/> before
    /// <see cref="DesktopConnectionEventKind.Disconnected"/>, and its disconnection follows every call of that page
    /// the surface accepted, such as a Bridge call of Runic Windows and Views. Every subscription receives every
    /// event, in subscription order. Subscribing does not change how the page navigates or reloads.
    /// </remarks>
    public IDisposable SubscribeConnectionEvents(Action<DesktopConnectionEvent> handler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(handler);
        return _engine.ObserveConnections(webUiEvent => handler(new DesktopConnectionEvent(this, webUiEvent)));
    }

    /// <summary>Gets the number of pages connected to this surface.</summary>
    /// <remarks>
    /// A page counts from its <see cref="DesktopConnectionEventKind.Connected"/> event until its
    /// <see cref="DesktopConnectionEventKind.Disconnected"/> event, and a handler of either event already sees the count
    /// with that change. A window's page, every tab of a served surface and each reload count separately. A reload
    /// can disconnect before or after the reloaded page connects, so the count can drop to zero for a moment: to
    /// end serving when the last browser tab closes, wait a short time after it reaches zero and check again.
    /// </remarks>
    public int ConnectionCount => _engine.ConnectedSessionCount;

    /// <summary>Opens this surface in one installed browser or embedded WebView.</summary>
    public async ValueTask<DesktopWindow> OpenWindowAsync(
        DesktopWindowOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var configured = (options ?? new DesktopWindowOptions()).WithPlacementApplied();
        DesktopWindow window;
        await _windowGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_window is { IsOpen: true })
            {
                throw new InvalidOperationException("This surface already has an open window presentation.");
            }

            ValidateWindowOptions(configured);
            RequireCloseConfirmationSupport(configured);
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
                // An embedded presentation that opened but never connected is not
                // unavailable: the browser would load the same frontend, so report it.
                catch (Exception exception) when (
                    exception is not OperationCanceledException
                    and not DesktopException { Code: ConnectionTimeoutCode })
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
                        FallbackRemediation(exception))
                    {
                        Severity = DesktopDiagnosticSeverity.Warning,
                    });
                    actualBrowser = requestedBrowser == BrowserKind.Embedded ? BrowserKind.Any : requestedBrowser;
                    WithholdGrantsFromFallback(configured, correlationId);
                    WithholdCloseConfirmationFromFallback(configured, correlationId);
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
            window = new DesktopWindow(this, _engine, requestedBrowser, actualBrowser, fellBack);
            _window = window;
        }
        finally
        {
            _windowGate.Release();
        }
        // A shutdown requested while this window opened closes it too. The gate release orders this check after
        // the window was published, so either the shutdown sees the window or the window sees the shutdown.
        if (_host.IsShutdownRequested) await window.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        return window;
    }

    /// <summary>Runs JavaScript in every authenticated presentation session.</summary>
    internal Task RunJavaScriptAsync(string script, CancellationToken cancellationToken = default) =>
        _engine.RunJavaScriptAsync(script, cancellationToken);

    /// <summary>Pushes a View's state to every authenticated presentation session.</summary>
    /// <remarks>
    /// The page's bridge parses the JSON and calls <c>globalThis["__{route}Changed"]</c> with it when that is a
    /// function. The state travels as data to a dispatcher in the served bridge script, not as script, so a policy
    /// without <c>'unsafe-eval'</c> allows it.
    /// </remarks>
    internal Task PushStateAsync(string route, string stateJson, CancellationToken cancellationToken = default) =>
        _engine.PushStateAsync(route, Encoding.UTF8.GetBytes(stateJson), cancellationToken);

    /// <summary><see cref="PushStateAsync(string, string, CancellationToken)"/> for state encoded as UTF-8. The packets
    /// are built before this returns.</summary>
    internal Task PushStateUtf8Async(string route, ReadOnlyMemory<byte> stateUtf8, CancellationToken cancellationToken = default) =>
        _engine.PushStateAsync(route, stateUtf8, cancellationToken);

    /// <summary>Runs JavaScript and returns its string result from an authenticated presentation session.</summary>
    /// <param name="script">The script; its <c>return</c> value is the result.</param>
    /// <param name="cancellationToken">Cancels waiting for the result. To wait for a limited time, pass a token from a
    /// <see cref="CancellationTokenSource"/> created with a delay.</param>
    /// <returns>The whole result.</returns>
    /// <remarks>
    /// The script runs in one authenticated session. While several are connected, such as browser tabs of a served
    /// surface, which one answers is not specified. A result is never cut: one larger than 16 MiB of UTF-8 fails
    /// instead. Typical callers are in-process tests of an embedded View and an application asking its page before
    /// it closes. The page evaluates the script with the <c>Function</c> constructor, so a
    /// <see cref="DesktopSurfaceOptions.ContentSecurityPolicy"/> without <c>'unsafe-eval'</c> refuses it, and the call
    /// fails with <see cref="InvalidOperationException"/>.
    /// </remarks>
    /// <exception cref="DesktopException">The result is larger than 16 MiB
    /// (<see cref="DesktopErrorCategory.LimitExceeded"/>, code <c>script-result-too-large</c>).</exception>
    /// <exception cref="InvalidOperationException">No authenticated session is connected, or the script failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask<string> ExecuteJavaScriptAsync(string script, CancellationToken cancellationToken = default) =>
        new(_engine.ExecuteJavaScriptAsync(script, timeout: null, MaximumScriptResultBytes, cancellationToken));

    // The largest result ExecuteJavaScriptAsync returns, in UTF-8 bytes; documented on the method.
    internal const int MaximumScriptResultBytes = 16 * 1024 * 1024;

    /// <summary>Navigates every authenticated presentation session.</summary>
    internal Task NavigateAsync(string url, CancellationToken cancellationToken = default) =>
        _engine.NavigateAsync(url, cancellationToken);

    /// <summary>
    /// Navigates the open embedded window of this surface through its native WebView, without a page connection.
    /// False when the surface has no open embedded window.
    /// </summary>
    internal Task<bool> NavigateWindowAsync(Uri url, CancellationToken cancellationToken = default) =>
        _engine.NavigateEmbeddedWindowAsync(url, cancellationToken);

    /// <summary>Sends opaque bytes to every authenticated presentation session.</summary>
    internal Task SendAsync(
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
    /// <remarks>
    /// From a capability or event callback, disposal continues after the callback returns:
    /// teardown waits for that callback's session to finish its disconnection event.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        var dispose = DisposeCoreAsync(detach: true, CancellationToken.None);
        GC.SuppressFinalize(this);
        return _engine.IsExecutingCallback ? ValueTask.CompletedTask : new ValueTask(dispose);
    }

    internal ValueTask DisposeFromHostAsync() => new(DisposeCoreAsync(detach: false, CancellationToken.None));

    internal bool IsExecutingCallback => _engine.IsExecutingCallback;

    // Child presentation adapters use this to retire their borrowed-model session on unload.
    internal event EventHandler? PresentationClosed
    {
        add => _engine.PresentationClosed += value;
        remove => _engine.PresentationClosed -= value;
    }

    internal bool IsCurrentWindow(DesktopWindow window) => ReferenceEquals(Volatile.Read(ref _window), window);

    // The monitors of the host that owns this surface.
    internal Runic.Platform.IDesktopMonitors Monitors => _host.Monitors;

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

    // Force-closes the current window for a host shutdown. It does not wait for the gate: a window that is still
    // opening checks the shutdown itself once it is published.
    internal ValueTask CloseWindowForShutdownAsync() =>
        Volatile.Read(ref _window) is { } window ? window.CloseAsync() : ValueTask.CompletedTask;

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

    // The native window size of a DesktopWindowOptions value: a positive number of pixels.
    private static uint WindowSize(int value, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, name);
        return (uint)value;
    }

    private void ApplyWindowOptions(DesktopWindowOptions options)
    {
        _engine.ResetDesktopPresentationOptions();
        _engine.SetSize(WindowSize(options.Width, nameof(options.Width)), WindowSize(options.Height, nameof(options.Height)));
        if (options.MinimumWidth is { } minimumWidth && options.MinimumHeight is { } minimumHeight)
        {
            _engine.SetMinimumSize(WindowSize(minimumWidth, nameof(options.MinimumWidth)),
                WindowSize(minimumHeight, nameof(options.MinimumHeight)));
        }
        if (options.X is { } x && options.Y is { } y)
        {
            _engine.SetPosition(x, y);
        }
        if (options.Centered)
        {
            _engine.Center();
        }
        if (options.Placement is { } placement)
        {
            _engine.SetPlacement(placement);
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
            _engine.SetBrowserArguments(arguments);
        }
        _engine.SetAllowedPermissions(options.AllowedPermissions);
        _engine.SetWebContent(options.WebContent);
        _engine.SetCloseConfirmation(options.ConfirmCloseAsync);
        _engine.SetMenuBarPresentation(options.MenuBarPresentation);
        _engine.SetMonitorAndLanguage(options.MonitorId, options.LayoutDirection, options.Language);
    }

    private static string FallbackRemediation(Exception exception) => exception is DesktopException { Code: var code }
        ? code switch
        {
            "linux-embedded-backend-not-selected" =>
                "Select a Linux toolkit with DesktopHostOptions.Linux.EmbeddedBackend to restore the preferred presentation.",
            "gtk4-provider-missing" =>
                "Call DesktopHostOptions.WithGtk4() from Runic.Desktop.Gtk4 to restore the GTK4 presentation.",
            _ => "Install the platform WebView prerequisite to restore the preferred presentation.",
        }
        : "Install the platform WebView prerequisite to restore the preferred presentation.";

    // A browser cannot limit a grant to the presented origin: Chromium's flag accepts capture for every
    // origin, and Firefox has no equivalent. The fallback therefore opens with the deny-by-default policy.
    private void WithholdGrantsFromFallback(DesktopWindowOptions options, string correlationId)
    {
        if (options.AllowedPermissions == DesktopPermissionGrant.None)
        {
            return;
        }
        _engine.SetAllowedPermissions(DesktopPermissionGrant.None);
        _host.ReportConfiguration(Internal.DesktopWindowOptionValidation.GrantWithheld("The browser fallback") with
        {
            CorrelationId = correlationId,
        });
    }

    // The embedded window confirms close requests; a browser fallback cannot, so it opens without confirmation.
    private void WithholdCloseConfirmationFromFallback(DesktopWindowOptions options, string correlationId)
    {
        if (options.ConfirmCloseAsync is null)
        {
            return;
        }
        _engine.SetCloseConfirmation(null);
        _host.ReportConfiguration(Internal.DesktopWindowOptionValidation.CloseConfirmationUnavailable(
            "The browser fallback", isFallback: true) with
        {
            CorrelationId = correlationId,
        });
    }

    // An explicitly selected browser cannot honour close confirmation; fail before anything opens.
    private void RequireCloseConfirmationSupport(DesktopWindowOptions options)
    {
        if (options.ConfirmCloseAsync is null || options.Browser == BrowserKind.Embedded ||
            options.PresentationPolicy == DesktopPresentationPolicy.EmbeddedThenBrowser)
        {
            return;
        }
        var diagnostic = Internal.DesktopWindowOptionValidation.CloseConfirmationUnavailable(
            $"The {options.Browser} browser", isFallback: false) with
        {
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
        _host.ReportConfiguration(diagnostic);
        throw new DesktopException(
            diagnostic.Category,
            diagnostic.Code,
            diagnostic.Message + " " + diagnostic.Remediation,
            diagnostic.Retryable,
            correlationId: diagnostic.CorrelationId);
    }

    private async Task OpenCheckedAsync(BrowserKind browser, CancellationToken cancellationToken)
    {
        var availability = _host.GetPresentation(browser, _windowHostFactory);
        if (!availability.IsAvailable)
        {
            var diagnostic = availability is { Diagnostics: [var first, ..] } ? first : DesktopHost.Unavailable(browser).Diagnostics[0];
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
        catch (TimeoutException exception)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            _host.Report(new DesktopDiagnostic(
                DesktopErrorCategory.TimedOut,
                ConnectionTimeoutCode,
                ConnectionTimeoutMessage,
                Retryable: true,
                correlationId,
                "Check that the page loads its Runic client script, or increase DesktopHostOptions.ConnectionTimeout."));
            throw new DesktopException(
                DesktopErrorCategory.TimedOut,
                ConnectionTimeoutCode,
                ConnectionTimeoutMessage,
                retryable: true,
                exception,
                correlationId);
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
                "Run DesktopHost.Validate(windowOptions) for the missing prerequisites, install them, or select another presentation."));
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
        const DesktopPermissionGrant supported = DesktopPermissionGrant.MediaCapture;
        if (!Enum.IsDefined(options.Browser))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Browser contains an unsupported value.");
        }
        if (!Enum.IsDefined(options.PresentationPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "PresentationPolicy contains an unsupported value.");
        }
        if (!Enum.IsDefined(options.MenuBarPresentation))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MenuBarPresentation contains an unsupported value.");
        }
        if ((options.AllowedPermissions & ~supported) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "AllowedPermissions contains an unsupported value.");
        }
        if (options.WebContent is null)
        {
            throw new ArgumentNullException(nameof(options), "WebContent must not be null.");
        }
        if (!Enum.IsDefined(options.WebContent.ExternalNavigation))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "WebContent.ExternalNavigation contains an unsupported value.");
        }
        if (options.Width <= 0 || options.Height <= 0 || options.MinimumWidth <= 0 || options.MinimumHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                "Width, Height, MinimumWidth and MinimumHeight must be positive numbers of pixels.");
        }
        if (!Enum.IsDefined(options.LayoutDirection))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "LayoutDirection contains an unsupported value.");
        }
        if (options.MonitorId is { } monitor && (monitor.Length == 0 || monitor.Contains('\0')))
        {
            throw new ArgumentException("Monitor must be a monitor identifier from DesktopHost.Monitors, or null.", nameof(options));
        }
        if (options.Language is { } language && !IsLanguageTag(language))
        {
            throw new ArgumentException(
                "Language must be a BCP 47 language tag such as en-US or ar-EG: letters and digits in subtags of 1 to 8, joined by hyphens.",
                nameof(options));
        }
    }

    // The shape of a BCP 47 tag: a 2-3 or 5-8 letter primary subtag (or i/x) and subtags of 1 to 8 letters and digits.
    internal static bool IsLanguageTag(string tag)
    {
        if (tag.Length is 0 or > 85) return false;
        var subtags = tag.Split('-');
        for (var index = 0; index < subtags.Length; index++)
        {
            var subtag = subtags[index];
            if (subtag.Length is 0 or > 8 || !subtag.All(char.IsAsciiLetterOrDigit)) return false;
            if (index == 0 && (!subtag.All(char.IsAsciiLetter) || subtag.Length is 4 || subtag.Length == 1 && subtag is not ("i" or "x" or "I" or "X")))
                return false;
        }
        return true;
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
    private DesktopWindowState? _state;
    private DesktopWindowState? _raisedState;
    private int _stateSignaled;
    private int _stateDispatching;
    private readonly object? _stateSource;

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
        IsFallback = fellBack;
        if (browser == BrowserKind.Embedded)
        {
            // The window may have reported its state while it opened, before this object existed.
            _stateSource = _engine.EmbeddedHostIdentity;
            _engine.EmbeddedWindowStateChanged += OnEngineStateChanged;
            _engine.EmbeddedFilesDropped += OnEngineFilesDropped;
            _engine.EmbeddedMenuBarPlacementChanged += OnEngineMenuBarPlacementChanged;
            _state = _engine.EmbeddedWindowState;
            _raisedState = _state;
        }
    }

    /// <summary>
    /// Gets the window's bounds, maximized and full-screen state as the window last reported them, also after it has
    /// closed; <see langword="null"/> when the presentation reports none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An embedded window whose <see cref="Capabilities"/> include <see cref="DesktopWindowCapabilities.State"/>
    /// reports its state when it opens and after every move, resize, maximize, restore and full-screen change. An
    /// installed browser and a custom window host that does not implement <see cref="IDesktopWindowStateHost"/> report
    /// none. Reading it is cheap and does not reach the native window.
    /// </para>
    /// <para>
    /// The value stays readable after the window has closed and is then the last state before closing, so an
    /// application saves <see cref="DesktopWindowState.Placement"/> after <see cref="WaitForCloseAsync"/> returns and
    /// restores it with <see cref="DesktopWindowOptions.Placement"/>. The units differ per platform; see
    /// <see cref="DesktopWindowState"/>.
    /// </para>
    /// </remarks>
    public DesktopWindowState? State => Volatile.Read(ref _state);

    /// <summary>Occurs after the window's <see cref="State"/> has changed.</summary>
    /// <remarks>
    /// Raised on the thread pool, never on the window's native thread and never concurrently. While the user drags or
    /// resizes the window, changes that arrive faster than the handlers run are combined, so a handler sees the latest
    /// state, not every intermediate one. A handler that throws is logged (event 3011, <c>WindowStateHandlerFailed</c>)
    /// and does not stop later events. No event is raised after the window has closed or was replaced.
    /// </remarks>
    public event EventHandler<DesktopWindowStateChangedEventArgs>? StateChanged;

    private bool AcceptsState => Volatile.Read(ref _disposed) == 0 && _surface.IsCurrentWindow(this);

    private void OnEngineStateChanged(object host, DesktopWindowState state)
    {
        // The engine lives as long as the surface and may later present a replacement window; this object keeps
        // the last state of its own window only.
        if (!ReferenceEquals(host, _stateSource))
        {
            _engine.EmbeddedWindowStateChanged -= OnEngineStateChanged;
            _engine.EmbeddedFilesDropped -= OnEngineFilesDropped;
            _engine.EmbeddedMenuBarPlacementChanged -= OnEngineMenuBarPlacementChanged;
            return;
        }
        Volatile.Write(ref _state, state);
        if (Interlocked.Exchange(ref _stateSignaled, 1) == 0 && Interlocked.CompareExchange(ref _stateDispatching, 1, 0) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static window => window.DispatchStateChanged(), this, preferLocal: false);
        }
    }

    private IDesktopMenuCommandTarget? _menuCommands;

    /// <summary>
    /// Gets or sets the target of the menu bar's commands while this window is the key window; <see langword="null"/>,
    /// the default, leaves them to <see cref="DesktopHost.MenuCommands"/>.
    /// </summary>
    /// <remarks>
    /// An item of <see cref="DesktopHostOptions.MenuBar"/> asks the window's target first: on macOS the key window's.
    /// It is enabled while <see cref="IDesktopMenuCommandTarget.CanExecute"/> returns <see langword="true"/> and runs
    /// <see cref="IDesktopMenuCommandTarget.Execute"/> when chosen, when its shortcut is pressed, or through
    /// <see cref="TryExecuteMenuItemAsync"/>. Runic Windows set it for a root ViewModel that implements
    /// <c>IRunicMenuCommandSource</c>.
    /// </remarks>
    public IDesktopMenuCommandTarget? MenuCommands
    {
        get => Volatile.Read(ref _menuCommands);
        set
        {
            Volatile.Write(ref _menuCommands, value);
            if (AcceptsState) _engine.SetEmbeddedMenuCommands(value);
        }
    }

    /// <summary>Gets where the window shows the menu bar of <see cref="DesktopHostOptions.MenuBar"/> now.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="DesktopMenuBarPlacement.Global"/> on macOS, where the application menu bar holds the declared menus for
    /// every presentation; <see cref="DesktopMenuBarPlacement.Window"/> for a bar in the window, and on Linux
    /// <see cref="DesktopMenuBarPlacement.Global"/> while the desktop's global menu shows it. A window with
    /// <see cref="DesktopMenuBarPresentation.Page"/> elsewhere reports <see cref="DesktopMenuBarPlacement.Page"/> once its
    /// host supports the in-page bar, and <see cref="DesktopMenuBarPlacement.None"/> before that. An installed browser, a
    /// window host without <see cref="DesktopWindowCapabilities.MenuBar"/>, the <see cref="DesktopMenuBarPresentation.None"/>
    /// presentation and a closed window report <see cref="DesktopMenuBarPlacement.None"/>.
    /// </para>
    /// <para>A page that renders the bar itself does so only while the placement is <see cref="DesktopMenuBarPlacement.Page"/>.</para>
    /// </remarks>
    public DesktopMenuBarPlacement MenuBarPlacement => IsOpen && Browser == BrowserKind.Embedded
        ? _engine.EmbeddedMenuBarPlacement
        : DesktopMenuBarPlacement.None;

    /// <summary>Occurs after <see cref="MenuBarPlacement"/> has changed, for example when a Linux desktop's global menu appeared or went away.</summary>
    /// <remarks>
    /// Raised on the thread pool. A handler that throws is logged (event 3017, <c>MenuBarPlacementHandlerFailed</c>) and
    /// does not stop later events.
    /// </remarks>
    public event EventHandler? MenuBarPlacementChanged;

    private void OnEngineMenuBarPlacementChanged(object host)
    {
        if (!ReferenceEquals(host, _stateSource))
        {
            _engine.EmbeddedMenuBarPlacementChanged -= OnEngineMenuBarPlacementChanged;
            return;
        }
        ThreadPool.UnsafeQueueUserWorkItem(static window =>
        {
            if (!window.AcceptsState || window.MenuBarPlacementChanged is not { } handlers) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(window, EventArgs.Empty);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    if (window._engine.Logger is { } logger)
                        Internal.DesktopLog.MenuBarPlacementHandlerFailed(logger, error, Internal.DesktopLog.ErrorType(error));
                    else
                        System.Diagnostics.Trace.TraceError($"A DesktopWindow.MenuBarPlacementChanged handler failed with {error.GetType().FullName}.");
                }
            }
        }, this, preferLocal: false);
    }

    /// <summary>Runs a menu item's command as the native menu bar does, and returns whether a command ran.</summary>
    /// <param name="item">The item, usually one of <see cref="DesktopHost.MenuBar"/>, such as the one a page's menu bar chose.</param>
    /// <param name="cancellationToken">Cancels waiting for the window's UI thread.</param>
    /// <returns>
    /// <see langword="true"/> when a target ran the item; <see langword="false"/> when the item is disabled
    /// (<see cref="DesktopMenuItem.IsEnabled"/>) or neither <see cref="MenuCommands"/> nor
    /// <see cref="DesktopHost.MenuCommands"/> can run it now.
    /// </returns>
    /// <remarks>
    /// The one command path of the native bar, a global menu, shortcuts and an in-page bar: the window's target, then the
    /// application's, each asked <see cref="IDesktopMenuCommandTarget.CanExecute"/> before
    /// <see cref="IDesktopMenuCommandTarget.Execute"/>, on the window's UI thread where the window has one. A target that
    /// throws is logged (event 3014) and does not reach the caller. It works for every presentation, including an
    /// installed browser.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="item"/> breaks a menu item rule.</exception>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask<bool> TryExecuteMenuItemAsync(DesktopMenuItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        DesktopMenuRules.Title(item.Title, "The menu item");
        DesktopMenuRules.Item(item, $"the menu item '{item.Title}'");
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(!IsOpen, this);
        return new ValueTask<bool>(_engine.TryExecuteMenuItemAsync(item, MenuCommands, cancellationToken));
    }

    // The in-page menu bar (Runic.Application.Views.Desktop): whether each item can run now, asked as
    // TryExecuteMenuItemAsync asks, on the window's UI thread where it has one.
    internal Task<bool[]> CanExecuteMenuItemsAsync(IReadOnlyList<DesktopMenuItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ObjectDisposedException.ThrowIf(!IsOpen, this);
        return _engine.CanExecuteMenuItemsAsync(items, MenuCommands, cancellationToken);
    }

    // The in-page menu bar: a page that matches the items' shortcuts itself suspends the host's own matching.
    internal void SuspendHostMenuShortcuts(bool suspended)
    {
        if (AcceptsState) _engine.SetEmbeddedMenuShortcutsSuspended(suspended);
    }

    /// <summary>Shows a native context menu over the window and returns the item the user chose.</summary>
    /// <param name="items">
    /// The menu's entries: the rules of <see cref="DesktopMenuItem"/> and <see cref="DesktopMenu"/>, at most four nested
    /// submenus and 256 entries, and at least one entry. Command ids may repeat; <see cref="DesktopMenu.Role"/> is ignored.
    /// </param>
    /// <param name="options">Where the menu opens, in CSS pixels of the page's viewport; <see langword="null"/> opens it at the pointer.</param>
    /// <param name="cancellationToken">Cancels waiting for the window, and closes the open menu.</param>
    /// <returns>
    /// The chosen item, one of <paramref name="items"/>, or <see langword="null"/> when the user dismissed the menu, a
    /// later context menu of the window replaced it, or the window closed.
    /// </returns>
    /// <remarks>
    /// Requires <see cref="DesktopWindowCapabilities.ContextMenu"/>. The menu is modal for the window. Disabled items and
    /// the items of disabled submenus cannot be chosen, and a shortcut is shown as a hint without being registered. The
    /// caller runs the chosen item's command, for example with its <see cref="DesktopMenuItem.CommandParameter"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="items"/> is empty or breaks a menu rule, or <paramref name="options"/> sets only one coordinate or one
    /// that is not finite.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">The window lacks the required capability (<c>window-capability-unavailable</c>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async ValueTask<DesktopMenuItem?> ShowContextMenuAsync(ImmutableArray<DesktopMenuEntry> items,
        DesktopContextMenuOptions? options = null, CancellationToken cancellationToken = default)
    {
        WindowPlatformServices.CheckContextMenu(items, options, cancellationToken);
        Task<DesktopMenuItem?>? menu = null;
        // Admitted like every window operation, but awaited outside the window gate: the menu stays open while other
        // operations, a closing window or the next context menu run.
        await _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.ContextMenu, () =>
        {
            menu = _engine.ShowContextMenuAsync(items, options ?? new DesktopContextMenuOptions(), cancellationToken);
            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return await menu!.ConfigureAwait(false);
    }

    /// <summary>Starts a native drag of files, text or URIs out of the window, and returns the effect of the drop.</summary>
    /// <param name="data">What to drag; the rules of <see cref="DesktopDragData"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the window before the drag starts; a running drag ends with the pointer.</param>
    /// <returns>
    /// The effect the drop target chose, one of <see cref="DesktopDragData.AllowedEffects"/>, or
    /// <see cref="DesktopDragEffects.None"/> when the user cancelled the drag (Escape), dropped it where nothing accepted
    /// it, or no mouse button was held when the call reached the window.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Requires <see cref="DesktopWindowCapabilities.FileDrag"/>. Call it while the user holds the primary mouse button
    /// on the window. The page asks for the drag from its <c>dragstart</c> event through an ordinary command, after
    /// calling <c>event.preventDefault()</c> so the web view starts no HTML5 drag of its own; the application builds
    /// <paramref name="data"/> from its own state, never from paths the page supplies:
    /// </para>
    /// <code>
    /// row.addEventListener("dragstart", event =&gt; { event.preventDefault(); client.commands.dragItem(row.dataset.key); });
    /// </code>
    /// <para>
    /// The window offers <see cref="DesktopDragData.Paths"/> as the platform's file list (<c>CF_HDROP</c>,
    /// <c>public.file-url</c>, <c>text/uri-list</c>), <see cref="DesktopDragData.Text"/> as plain text and
    /// <see cref="DesktopDragData.Uris"/> as links. <see cref="DesktopDragEffects.Move"/> means the target asked for a
    /// move: the files may already be gone (an optimized move on Windows, a file manager on macOS or Linux), so delete
    /// what remains.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="data"/> breaks a rule of <see cref="DesktopDragData"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">The window lacks the required capability (<c>window-capability-unavailable</c>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async ValueTask<DesktopDragEffects> StartDragAsync(DesktopDragData data, CancellationToken cancellationToken = default)
    {
        DesktopDragRules.Check(data);
        cancellationToken.ThrowIfCancellationRequested();
        Task<DesktopDragEffects>? drag = null;
        // Admitted like every window operation, but awaited outside the window gate, as a context menu is: the drag's
        // modal loop runs while other operations or a closing window proceed.
        await _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.FileDrag, () =>
        {
            drag = _engine.StartDragAsync(data, cancellationToken);
            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return DesktopDragRules.Single(await drag!.ConfigureAwait(false), data.AllowedEffects);
    }

    /// <summary>Occurs when the user drops files onto the embedded window, with their native paths.</summary>
    /// <remarks>
    /// <para>
    /// An embedded window whose <see cref="Capabilities"/> include <see cref="DesktopWindowCapabilities.FileDrop"/>
    /// raises it once per drop, with the absolute local paths the platform reported: WebView2 from the
    /// <c>File</c> objects of the page's drop event, WKWebView from the dragging pasteboard, WebKitGTK from the drag's
    /// <c>text/uri-list</c> or GTK 4 file list. The page never supplies a path; on WebView2 page script can at most
    /// hand over <c>File</c> objects the user gave it by dropping or choosing them. The page still receives its own
    /// <c>dragenter</c>, <c>dragover</c> and <c>drop</c> events to show a drop target, and dropping a file never
    /// navigates the window. <see cref="DesktopFileDropEventArgs.Target"/> is the page's untrusted routing label.
    /// </para>
    /// <para>
    /// Raised on the thread pool, one drop at a time and in drop order. A handler that throws is logged (event 3012,
    /// <c>FileDropHandlerFailed</c>). An installed browser reports no drops: its page only has HTML5 <c>File</c>
    /// objects without paths.
    /// </para>
    /// </remarks>
    public event EventHandler<DesktopFileDropEventArgs>? FilesDropped;

    private readonly System.Collections.Concurrent.ConcurrentQueue<DesktopFileDropEventArgs> _drops = new();
    private int _dropDispatching;

    private void OnEngineFilesDropped(object host, DesktopFileDropEventArgs drop)
    {
        if (!ReferenceEquals(host, _stateSource))
        {
            _engine.EmbeddedFilesDropped -= OnEngineFilesDropped;
            return;
        }
        _drops.Enqueue(drop);
        if (Interlocked.CompareExchange(ref _dropDispatching, 1, 0) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static window => window.DispatchDrops(), this, preferLocal: false);
        }
    }

    private void DispatchDrops()
    {
        while (true)
        {
            while (_drops.TryDequeue(out var drop))
            {
                if (!AcceptsState || FilesDropped is not { } handlers)
                {
                    // Nobody can read the staged copies of promised files: delete them.
                    foreach (var file in drop.PromisedFiles) _ = file.DisposeAsync().AsTask().ContinueWith(static task => _ = task.Exception,
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    continue;
                }
                foreach (EventHandler<DesktopFileDropEventArgs> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(this, drop);
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        if (_engine.Logger is { } logger)
                            Internal.DesktopLog.FileDropHandlerFailed(logger, error, Internal.DesktopLog.ErrorType(error));
                        else
                            System.Diagnostics.Trace.TraceError($"A DesktopWindow.FilesDropped handler failed with {error.GetType().FullName}.");
                    }
                }
            }
            Volatile.Write(ref _dropDispatching, 0);
            if (_drops.IsEmpty || Interlocked.CompareExchange(ref _dropDispatching, 1, 0) != 0)
            {
                return;
            }
        }
    }

    private void DispatchStateChanged()
    {
        while (true)
        {
            Volatile.Write(ref _stateSignaled, 0);
            var state = Volatile.Read(ref _state);
            if (state is not null && !Equals(state, _raisedState) && AcceptsState)
            {
                _raisedState = state;
                if (StateChanged is { } handlers)
                {
                    var args = new DesktopWindowStateChangedEventArgs(state);
                    foreach (EventHandler<DesktopWindowStateChangedEventArgs> handler in handlers.GetInvocationList())
                    {
                        try
                        {
                            handler(this, args);
                        }
                        catch (Exception error) when (error is not OutOfMemoryException)
                        {
                            if (_engine.Logger is { } logger)
                                Internal.DesktopLog.WindowStateHandlerFailed(logger, error, Internal.DesktopLog.ErrorType(error));
                            else
                                System.Diagnostics.Trace.TraceError($"A DesktopWindow.StateChanged handler failed with {error.GetType().FullName}.");
                        }
                    }
                }
            }
            Volatile.Write(ref _stateDispatching, 0);
            if (Volatile.Read(ref _stateSignaled) == 0 || Interlocked.CompareExchange(ref _stateDispatching, 1, 0) != 0)
            {
                return;
            }
        }
    }

    /// <summary>Gets the presentation requested by <see cref="DesktopWindowOptions.Browser"/>.</summary>
    public BrowserKind RequestedBrowser { get; }

    /// <summary>Gets the presentation that opened.</summary>
    /// <remarks>
    /// For a <see cref="BrowserKind.Any"/> or <see cref="BrowserKind.ChromiumBased"/> request this is the concrete
    /// browser that was launched, or <see cref="BrowserKind.Any"/> when it could not be identified.
    /// </remarks>
    public BrowserKind Browser { get; }

    /// <summary>Gets whether the embedded WebView was unavailable and the browser fallback opened instead.</summary>
    public bool IsFallback { get; }

    /// <summary>Gets whether this is the surface's current window and its browser process or embedded window is still open.</summary>
    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && _surface.IsCurrentWindow(this)
        && (Browser == BrowserKind.Embedded ? _engine.IsEmbeddedWindowOpen : _engine.IsShown);

    /// <summary>Gets the installed browser's process identifier, or zero for an embedded or closed window.</summary>
    public int ProcessId => IsOpen ? (int)_engine.BrowserProcessId : 0;

    /// <summary>Gets the native top-level handle of an open embedded window, or zero for an installed browser or closed window.</summary>
    /// <remarks>
    /// <para>The handle kind depends on the window host:</para>
    /// <list type="table">
    /// <listheader><term>Host</term><description>Handle</description></listheader>
    /// <item><term>Windows, WebView2</term><description>The top-level window's <c>HWND</c>.</description></item>
    /// <item><term>macOS, WKWebView</term><description>An <c>NSWindow*</c>.</description></item>
    /// <item><term>Linux, GTK 3 and WebKitGTK 4.1</term><description>A GTK 3 <c>GtkWindow*</c>.</description></item>
    /// <item><term>Linux, GTK 4 (<c>Runic.Desktop.Gtk4</c>)</term><description>A GTK 4 <c>GtkWindow*</c>.</description></item>
    /// <item><term>A custom window host</term><description>Whatever its <see cref="IDesktopWindowHost.NativeHandle"/> reports.</description></item>
    /// </list>
    /// <para>
    /// Reading the value on another thread is for identity only, such as comparing windows. AppKit and GTK objects may be
    /// used only on their UI thread: pass any call on the handle to <see cref="InvokeNativeAsync"/>, which runs it there
    /// with the handle. The handle is invalid once the window closes.
    /// </para>
    /// </remarks>
    public nint NativeHandle => IsOpen ? _engine.NativeWindowHandle : 0;

    /// <summary>Whether this live embedded owner supports native-thread callbacks.</summary>
    public bool SupportsNativeDispatch => IsOpen && _engine.SupportsNativeDispatch;

    /// <summary>Whether the caller is executing on the live native owner's thread.</summary>
    public bool CheckNativeAccess() => SupportsNativeDispatch && _engine.CheckNativeAccess();

    /// <summary>Runs native work on this owner's thread. Cancellation prevents queued work;
    /// a running callback is awaited to completion. No native handle may outlive this window.</summary>
    public ValueTask InvokeNativeAsync(Action<nint> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!SupportsNativeDispatch) throw new NotSupportedException("The presentation has no native dispatcher.");
        return _engine.InvokeNativeAsync(() =>
        {
            ObjectDisposedException.ThrowIf(!IsOpen || NativeHandle == 0, this);
            action(NativeHandle);
        }, cancellationToken);
    }

    /// <summary>Gets the platform-window operations the open window supports.</summary>
    /// <remarks>Only embedded windows report capabilities; installed browsers and closed windows report <see cref="DesktopWindowCapabilities.None"/>.</remarks>
    public DesktopWindowCapabilities Capabilities => IsOpen && Browser == BrowserKind.Embedded
        ? _engine.WindowCapabilities | DesktopWindowCapabilities.Visibility |
          (_engine.SupportsCloseConfirmation ? DesktopWindowCapabilities.CloseConfirmation : DesktopWindowCapabilities.None)
        : DesktopWindowCapabilities.None;

    /// <summary>Activates the window and gives it keyboard focus.</summary>
    /// <param name="cancellationToken">Cancels waiting for the window and the queued native operation.</param>
    /// <returns>A task that completes when the window has been focused.</returns>
    /// <remarks>
    /// Requires <see cref="DesktopWindowCapabilities.Focus"/>. It also shows a window opened with
    /// <see cref="DesktopWindowOptions.Hidden"/>: every built-in embedded host shows the window before it activates it,
    /// and this is the way to reveal a hidden window.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">The window lacks the required capability (<c>window-capability-unavailable</c>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask FocusAsync(CancellationToken cancellationToken = default)
    {
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.Focus,
            () => new ValueTask(_engine.FocusAsync(cancellationToken)), cancellationToken);
    }

    /// <summary>Shows or hides the window without closing it.</summary>
    /// <param name="visible"><see langword="true"/> to show the window; <see langword="false"/> to hide it.</param>
    /// <param name="cancellationToken">Cancels waiting for the window and the queued native operation.</param>
    /// <returns>A task that completes when the native window has been shown or hidden.</returns>
    /// <remarks>
    /// <para>
    /// Requires <see cref="DesktopWindowCapabilities.Visibility"/>, which every embedded window reports; an installed
    /// browser cannot be hidden. A hidden window stays open: <see cref="IsOpen"/> stays <see langword="true"/>, its page
    /// keeps running and its Bridge stays connected, and <see cref="WaitForCloseAsync"/> keeps waiting. A window opened
    /// with <see cref="DesktopWindowOptions.Hidden"/> is shown the same way. Whether showing also activates the window is
    /// up to the platform and window manager; <see cref="FocusAsync"/> shows and activates it everywhere.
    /// </para>
    /// <para>
    /// Only the application can show a hidden window again, so keep a way to reach it, such as a status icon or a second
    /// launch that calls <see cref="FocusAsync"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">The window lacks the required capability (<c>window-capability-unavailable</c>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.Visibility,
            () => _engine.SetEmbeddedVisibleAsync(visible, cancellationToken), cancellationToken);
    }

    /// <summary>Hides the window without closing it, as <see cref="SetVisibleAsync"/> with <see langword="false"/>.</summary>
    /// <param name="cancellationToken">Cancels waiting for the window and the queued native operation.</param>
    /// <returns>A task that completes when the native window has been hidden.</returns>
    /// <remarks>Requires <see cref="DesktopWindowCapabilities.Visibility"/>. <see cref="ShowAsync"/> or <see cref="FocusAsync"/> shows it again.</remarks>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">The window lacks the required capability (<c>window-capability-unavailable</c>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask HideAsync(CancellationToken cancellationToken = default) => SetVisibleAsync(false, cancellationToken);

    /// <summary>
    /// Shows a hidden window, as <see cref="SetVisibleAsync"/> with <see langword="true"/>. A window that is shown stays
    /// as it is.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the window and the queued native operation.</param>
    /// <returns>A task that completes when the native window has been shown.</returns>
    /// <remarks>
    /// Requires <see cref="DesktopWindowCapabilities.Visibility"/>. Whether showing also activates the window is up to the
    /// platform and window manager; <see cref="FocusAsync"/> shows and activates it everywhere.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">The window lacks the required capability (<c>window-capability-unavailable</c>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask ShowAsync(CancellationToken cancellationToken = default) => SetVisibleAsync(true, cancellationToken);

    /// <summary>Asks the screen reader to announce a message, such as the result of an action.</summary>
    /// <param name="message">The text to speak, in the page's language.</param>
    /// <param name="priority">
    /// <see cref="DesktopAnnouncementPriority.Polite"/>, the default, waits for what the screen reader is saying;
    /// <see cref="DesktopAnnouncementPriority.Assertive"/> interrupts it.
    /// </param>
    /// <param name="cancellationToken">Cancels waiting for the window and the queued native operation.</param>
    /// <returns>A task that completes when the message has been passed to the platform's accessibility API.</returns>
    /// <remarks>
    /// Requires <see cref="DesktopWindowCapabilities.Announcements"/>, which the embedded window reports on every
    /// platform. It is the native counterpart of an ARIA live region, which WebKitGTK does not report to Orca: on
    /// Linux the window's accessible object emits the AT-SPI <c>Announcement</c> event (ATK's <c>notification</c>
    /// signal with GTK 3, <c>gtk_accessible_announce</c> with GTK 4.14 or later), on Windows the window raises a
    /// UI Automation notification event, or, while the page has the keyboard focus, the page's Bridge says the message
    /// in a live region that it adds to the page, and on macOS it posts
    /// <c>NSAccessibilityAnnouncementRequestedNotification</c>. Whether and when the message is spoken is up to the
    /// screen reader. A browser cannot be asked: an installed browser, including one opened after a fallback, reports
    /// no such capability, so keep a live region in the page for it.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="message"/> is empty or white space.</exception>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">The window lacks the required capability (<c>window-capability-unavailable</c>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask AnnounceAsync(string message, DesktopAnnouncementPriority priority = DesktopAnnouncementPriority.Polite,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.Announcements,
            () => new ValueTask(_engine.AnnounceAsync(message, priority, cancellationToken)), cancellationToken);
    }

    /// <summary>Minimizes the window.</summary>
    /// <param name="cancellationToken">Cancels waiting for the window and the queued native operation.</param>
    /// <returns>A task that completes when the window has been minimized.</returns>
    /// <remarks>Requires <see cref="DesktopWindowCapabilities.Minimize"/>.</remarks>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">The window lacks the required capability (<c>window-capability-unavailable</c>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask MinimizeAsync(CancellationToken cancellationToken = default)
    {
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.Minimize,
            () => new ValueTask(_engine.MinimizeAsync(cancellationToken)), cancellationToken);
    }

    /// <summary>Maximizes the window, or restores it when it is already maximized.</summary>
    /// <param name="cancellationToken">Cancels waiting for the window and the queued native operation.</param>
    /// <returns>A task that completes when the window state has changed.</returns>
    /// <remarks>Requires <see cref="DesktopWindowCapabilities.Maximize"/>.</remarks>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">The window lacks the required capability (<c>window-capability-unavailable</c>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default)
    {
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.Maximize,
            () => new ValueTask(_engine.MaximizeAsync(cancellationToken)), cancellationToken);
    }

    /// <summary>Enters or leaves full screen.</summary>
    /// <param name="fullScreen"><see langword="true"/> to fill the window's monitor without a frame; <see langword="false"/> to return to its bounds before.</param>
    /// <param name="cancellationToken">Cancels waiting for the window and the queued native operation.</param>
    /// <returns>A task that completes when the window has been asked to change; <see cref="StateChanged"/> reports the change.</returns>
    /// <remarks>
    /// Requires <see cref="DesktopWindowCapabilities.FullScreen"/>. Does nothing when the window already is, or is not,
    /// full-screen. On macOS the window moves into its own full-screen space with AppKit's animation, so
    /// <see cref="DesktopWindowState.IsFullScreen"/> changes when the animation ends. <see cref="DesktopWindowState.Placement"/>
    /// records the state, so a window closed full-screen opens full-screen again.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">The window lacks the required capability (<c>window-capability-unavailable</c>).</exception>
    /// <exception cref="InvalidOperationException"><paramref name="fullScreen"/> is <see langword="false"/> for a <see cref="DesktopWindowOptions.Kiosk"/> window, which stays full-screen.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default)
    {
        if (!fullScreen && IsOpen && _engine.IsKiosk)
        {
            throw new InvalidOperationException("A kiosk window stays full-screen.");
        }
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.FullScreen,
            () => _engine.SetFullScreenAsync(fullScreen, cancellationToken), cancellationToken);
    }

    /// <summary>Moves the window to a monitor, centered on its work area and shrunk to fit it.</summary>
    /// <param name="monitorId">The <see cref="Runic.Platform.DesktopMonitor.Id"/> of a connected monitor, from <see cref="DesktopHost.Monitors"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for the window and the queued native operation.</param>
    /// <returns>A task that completes when the window has moved.</returns>
    /// <remarks>
    /// Requires <see cref="DesktopWindowCapabilities.MonitorPlacement"/>. A maximized window moves maximized. A
    /// full-screen window moves full-screen on Windows; macOS keeps a full-screen window in its space, and moves it
    /// when it leaves full screen.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="monitorId"/> is empty, or no connected monitor has this identifier.</exception>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">The window lacks the required capability (<c>window-capability-unavailable</c>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask MoveToMonitorAsync(string monitorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(monitorId);
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.MonitorPlacement,
            () => _engine.MoveToMonitorAsync(monitorId, cancellationToken), cancellationToken);
    }

    /// <summary>Resizes the window.</summary>
    /// <param name="width">The new width in pixels.</param>
    /// <param name="height">The new height in pixels.</param>
    /// <param name="cancellationToken">Cancels waiting for the window and the queued native operation.</param>
    /// <returns>A task that completes when the window has been resized.</returns>
    /// <remarks>Requires <see cref="DesktopWindowCapabilities.Resize"/>.</remarks>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">The window lacks the required capability (<c>window-capability-unavailable</c>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return RequireAndResize((uint)width, (uint)height, cancellationToken);
    }

    /// <summary>Moves the window to a screen position.</summary>
    /// <param name="x">The new horizontal position in pixels; negative values are left of the primary screen's origin.</param>
    /// <param name="y">The new vertical position in pixels; negative values are above the primary screen's origin.</param>
    /// <param name="cancellationToken">Cancels waiting for the window and the queued native operation.</param>
    /// <returns>A task that completes when the window has been moved.</returns>
    /// <remarks>Requires <see cref="DesktopWindowCapabilities.Move"/>.</remarks>
    /// <exception cref="ObjectDisposedException">The surface is disposed, or this window is closed or was replaced.</exception>
    /// <exception cref="DesktopException">The window lacks the required capability (<c>window-capability-unavailable</c>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) =>
        RequireAndMove(x, y, cancellationToken);

    private readonly object _closedGate = new();
    private EventHandler? _closedHandlers;
    private int _closedMonitor;
    private int _closedRaised;

    /// <summary>Occurs once after the window has closed.</summary>
    /// <remarks>
    /// <para>
    /// Raised whatever closed the window: the user, <see cref="RequestCloseAsync"/>, <see cref="CloseAsync"/>, disposing
    /// the surface or host, a host shutdown, or the user closing an installed browser's window or the browser itself. It
    /// is the event counterpart of <see cref="WaitForCloseAsync"/>, like <c>Window.Closed</c> in WPF and Avalonia:
    /// <see cref="IsOpen"/> is then <see langword="false"/> and <see cref="State"/> holds the last state to save. The
    /// native window's resources may still be being released; <see cref="WaitForCloseAsync"/> waits for that too.
    /// </para>
    /// <para>
    /// Raised on the thread pool, never on the window's native thread. Watching starts when the first handler is added,
    /// so a handler added after the window closed is still called once if the event has not been raised yet; a handler
    /// added after the event was raised is not called. A handler that throws is logged (event 3013,
    /// <c>WindowClosedHandlerFailed</c>) and does not stop the other handlers. An installed browser's window is noticed
    /// within a fraction of a second, as <see cref="WaitForCloseAsync"/> notices it. On macOS and with the GTK 4 host the
    /// process main thread must run <see cref="DesktopEventLoop.Run(DesktopHostOptions, Func{DesktopHost, Task{int}})"/>.
    /// </para>
    /// </remarks>
    public event EventHandler? Closed
    {
        add
        {
            if (value is null) return;
            lock (_closedGate) _closedHandlers += value;
            if (Interlocked.Exchange(ref _closedMonitor, 1) == 0)
            {
                _ = Task.Run(MonitorCloseAsync);
            }
        }
        remove
        {
            lock (_closedGate) _closedHandlers -= value;
        }
    }

    private static readonly TimeSpan ClosePollInterval = TimeSpan.FromMilliseconds(100);

    private async Task MonitorCloseAsync()
    {
        // The embedded host reports its close at once; a browser presentation is polled like WaitForCloseAsync polls it.
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPresentationClosed(object? sender, EventArgs args) => signal.TrySetResult();
        _engine.PresentationClosed += OnPresentationClosed;
        try
        {
            while (IsOpen && !signal.Task.IsCompleted && !_closed.Task.IsCompleted)
            {
                await Task.WhenAny(signal.Task, _closed.Task, Task.Delay(ClosePollInterval)).ConfigureAwait(false);
            }
        }
        finally
        {
            _engine.PresentationClosed -= OnPresentationClosed;
        }
        // The native release that follows may need the thread that owns the window; WaitForCloseAsync joins it.
        RaiseClosed();
    }

    private void RaiseClosed()
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) != 0) return;
        EventHandler? handlers;
        lock (_closedGate) handlers = _closedHandlers;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (_engine.Logger is { } logger)
                    Internal.DesktopLog.WindowClosedHandlerFailed(logger, error, Internal.DesktopLog.ErrorType(error));
                else
                    System.Diagnostics.Trace.TraceError($"A DesktopWindow.Closed handler failed with {error.GetType().FullName}.");
            }
        }
    }

    /// <summary>Waits until this presentation closes while servicing platform window events.</summary>
    /// <param name="cancellationToken">Stops this caller's wait; it does not close the window.</param>
    /// <returns>A task that completes when the window has closed and its native resources have been released.</returns>
    /// <remarks>
    /// On macOS and with the GTK 4 host the process main thread must run <see cref="DesktopEventLoop.Run(DesktopHostOptions, Func{DesktopHost, Task{int}})"/>,
    /// which services native window events while the application awaits this method.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default)
    {
        while ((Volatile.Read(ref _disposed) != 0 && !_closed.Task.IsCompleted)
            || IsOpen)
        {
            ServiceNativeEvents();
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        // Native destruction can precede asynchronously queued native release work.
        // Join that cleanup while the process main thread can still service it.
        var close = CloseAsync(CancellationToken.None).AsTask();
        while (!close.IsCompleted)
        {
            ServiceNativeEvents();
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
        await close.ConfigureAwait(false);
    }

    // Both calls are no-ops away from the thread that owns the native event loop.
    private void ServiceNativeEvents()
    {
        if (OperatingSystem.IsMacOS())
        {
            Internal.MacOsWkWebViewHost.ProcessPendingMainThreadWork();
        }
        _engine.ProcessEmbeddedHostEvents();
    }

    /// <summary>Requests closing, awaiting the same confirmation as the native close button.</summary>
    /// <remarks>Cancelling the caller stops its wait, without cancelling another caller's shared decision.</remarks>
    public ValueTask<bool> RequestCloseAsync(CancellationToken cancellationToken = default)
        => IsOpen ? _surface.RequestCloseWindowAsync(this, cancellationToken) : ValueTask.FromResult(true);

    /// <summary>Forcibly closes this window without awaiting confirmation. Use RequestCloseAsync for user actions.</summary>
    /// <param name="cancellationToken">Stops this caller's wait. The close itself always proceeds and is shared by every caller.</param>
    /// <returns>A task that completes when the window has closed.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the close completed.</exception>
    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ = CloseCoreAsync();
        }
        return new ValueTask(_closed.Task.WaitAsync(cancellationToken));
    }

    private async Task CloseCoreAsync()
    {
        try
        {
            await _surface.CloseWindowAsync(this).ConfigureAwait(false);
            _engine.EmbeddedWindowStateChanged -= OnEngineStateChanged;
            _closed.TrySetResult();
        }
        catch (Exception exception)
        {
            _closed.TrySetException(exception);
        }
    }

    /// <summary>Forcibly closes the window, like <see cref="CloseAsync(CancellationToken)"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private ValueTask RequireAndResize(uint width, uint height, CancellationToken cancellationToken)
    {
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.Resize,
            () => _engine.ResizePresentationAsync(width, height, cancellationToken), cancellationToken);
    }

    private ValueTask RequireAndMove(int x, int y, CancellationToken cancellationToken)
    {
        return _surface.RunWindowOperationAsync(this, DesktopWindowCapabilities.Move,
            () => _engine.MovePresentationAsync(x, y, cancellationToken), cancellationToken);
    }

    // The monitors of the window's host, for its platform services.
    internal Runic.Platform.IDesktopMonitors Monitors => _surface.Monitors;

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
