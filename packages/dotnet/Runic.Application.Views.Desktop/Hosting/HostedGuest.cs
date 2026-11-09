using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop.Hosting;

// The window-open request of one Window (host-protocol.md, window-open).
internal sealed record HostedWindowRequest(string Type, Uri Url, int Width, int Height, int? MinWidth, int? MinHeight,
    string? Owner, bool Modal, bool GuardClose, bool MediaCapture);

// One Window of the guest on the channel.
internal sealed class HostedWindow
{
    private readonly HostedGuest _guest;
    private readonly Func<CancellationToken, ValueTask<bool>>? _guard;
    private readonly Func<Task> _close;
    private readonly object _gate = new();
    private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<bool>? _decision;
    private int _closing;
    private int _closed;

    internal HostedWindow(HostedGuest guest, string id, Func<CancellationToken, ValueTask<bool>>? guard, Func<Task> close)
    {
        _guest = guest;
        Id = id;
        _guard = guard;
        _close = close;
    }

    internal string Id { get; }

    internal HostedWindowPresentation? Presentation { get; private set; }

    internal bool IsClosed => Volatile.Read(ref _closed) != 0;

    internal bool IsClosing => Volatile.Read(ref _closing) != 0;

    // Completes once window-closed was pushed, or not needed.
    internal Task Finished => _finished.Task;

    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Opened(HostedWindowPresentation presentation)
    {
        Presentation = presentation;
        _opened.TrySetResult(true);
    }

    internal void OpenFailed() => _opened.TrySetResult(false);

    // close-request: the guard decides unless the host forces the close. One decision at a time; a closing window allows.
    internal Task<bool> DecideCloseAsync(bool force)
    {
        if (force || _guard is null || Volatile.Read(ref _closing) != 0 || IsClosed) return Task.FromResult(true);
        lock (_gate)
        {
            if (_decision is null || _decision.IsCompleted) _decision = _guard(CancellationToken.None).AsTask();
            return _decision;
        }
    }

    // Closes the Window without asking it; its close then pushes window-closed.
    internal Task CloseAsync() => Interlocked.Exchange(ref _closing, 1) == 0 ? _close() : Task.CompletedTask;

    // The Window finished closing, for any reason: window-closed, after the window-open reply when it was pending.
    internal async Task OnClosedAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Interlocked.Exchange(ref _closing, 1);
        try
        {
            if (!await _opened.Task.ConfigureAwait(false)) return;
            Presentation?.MarkClosed();
            await _guest.PushAsync(new JsonObject { ["kind"] = "window-closed", ["window"] = Id }).ConfigureAwait(false);
        }
        finally { _finished.TrySetResult(); }
    }
}

// Hosted mode of a RunicDesktopHost (host-protocol.md): the launch contract and handshake, the window registry, the
// host's requests and pushes, and the shutdown when the host goes away.
internal sealed class HostedGuest : IAsyncDisposable
{
    private readonly RunicGuestOptions _options;
    private readonly DesktopHost _desktop;
    private readonly ILogger _logger;
    private readonly HostInput _input;
    private readonly ConcurrentDictionary<string, HostedWindow> _windows = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _ended = new();
    private readonly CancellationTokenSource _hostGone = new();
    private HostChannel? _channel;
    private HostChannelListener? _listener;
    private int _nextWindow;
    private int _shutdown;

    private HostedGuest(RunicGuestOptions options, DesktopHost desktop, ILogger logger)
    {
        _options = options;
        _desktop = desktop;
        _logger = logger;
        _input = new HostInput(options.OpenInput());
    }

    internal RunicGuestSession Session { get; private set; } = null!;

    internal bool IsHostGone => _shutdown != 0;

    // Runs the launch contract and the handshake; throws RunicHostedApplicationException when the host cannot host it.
    internal static async Task<HostedGuest> StartAsync(RunicGuestOptions options, DesktopHost desktop, ILogger logger)
    {
        var guest = new HostedGuest(options, desktop, logger);
        try
        {
            await guest.HandshakeAsync().ConfigureAwait(false);
            return guest;
        }
        catch (Exception error)
        {
            await guest.DisposeAsync().ConfigureAwait(false);
            if (error is RunicHostedApplicationException failure) DesktopLog.HostedStartFailed(logger, failure.Code, failure.Message);
            throw;
        }
    }

    private async Task HandshakeAsync()
    {
        using var deadline = new CancellationTokenSource(_options.StartTimeout);
        string? line;
        try { line = await _input.FirstLine.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw Failure("offer-missing", "No offer arrived on standard input within the start timeout."); }
        if (line is null) throw Failure("offer-missing", "Standard input ended before the host's offer.");
        if (HostProtocol.ParseLine(line, HostProtocol.OfferPrefix) is not { } offerLine || HostProtocol.ReadOffer(offerLine) is not { } offer)
            throw Refuse("offer-invalid", "The first line of standard input is not a valid RUNIC_HOST_OFFER line.");
        if (!offer.Protocols.Contains(HostProtocol.Version))
            throw Refuse("protocol-unsupported",
                $"The host offers runic-host versions {string.Join(", ", offer.Protocols)}; this application implements {HostProtocol.Version}.",
                withProtocols: true);
        var host = new RunicHostInfo(offer.HostName, offer.HostVersion);
        if (_options.AcceptHost is { } accept && !accept(host))
            throw Refuse("host-not-accepted", $"The application does not accept the host '{host.Name}' {host.Version}.");
        if (_options.CheckPrivileges() is { } privileges)
            throw Refuse(privileges, privileges == "guest-elevated"
                ? "The application runs elevated, so it cannot be hosted."
                : "The application runs as another user than the process that started it, so it cannot be hosted.");
        if (_desktop.Options.NetworkExposure != DesktopNetworkExposure.Loopback)
            throw Refuse("network-exposure", "The application's Desktop host listens on other interfaces than loopback.");

        _listener = HostChannelListener.Start(_options.UnauthenticatedTimeout);
        var key = HostProtocol.NewSecret();
        var hostProof = HostProtocol.Proof(offer.Nonce, HostProtocol.HostProofLabel, key);
        WriteLine(HostProtocol.Line(HostProtocol.ChannelPrefix, new JsonObject
        {
            ["url"] = _listener.Url.AbsoluteUri,
            ["key"] = key,
            ["protocol"] = HostProtocol.Version,
        }));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var accepting = _listener.AcceptAsync((socket, hello) => AuthenticateAsync(socket, hello, key, hostProof), stop.Token);
        if (await Task.WhenAny(accepting, _input.Ended).ConfigureAwait(false) != accepting)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await IgnoreAsync(accepting).ConfigureAwait(false);
            throw Failure("host-gone", "Standard input ended before the host connected.");
        }
        WebSocket socket;
        JsonObject hello;
        try { (socket, hello) = await accepting.ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            throw Failure("handshake-timeout", "The host did not connect and prove itself within the start timeout.");
        }

        // The key is spent: nothing listens any more.
        var features = new JsonArray();
        Session = new RunicGuestSession(host, HostProtocol.Version, [], _hostGone.Token);
        await HostChannel.SendAsync(socket, new JsonObject
        {
            ["id"] = HostProtocol.Integer(hello, "id"),
            ["ok"] = true,
            ["protocol"] = HostProtocol.Version,
            ["proof"] = HostProtocol.Proof(offer.Nonce, HostProtocol.GuestProofLabel, key),
            ["guest"] = new JsonObject
            {
                ["name"] = _options.ResolvedName,
                ["version"] = _options.ResolvedVersion,
                ["applicationId"] = _options.ApplicationId,
            },
            ["features"] = features,
        }, CancellationToken.None).ConfigureAwait(false);
        _channel = new HostChannel(socket, firstRequestId: 1, OnRequestAsync, OnPush,
            error => DesktopLog.HostedChannelFailed(_logger, error, error.GetType().Name));
        _channel.Start();
        _ = WatchHostAsync(_channel);
    }

    // The checks of host-protocol.md, Guest rules: key and proof first, then the protocol and the members.
    private static async Task<HostHelloVerdict> AuthenticateAsync(WebSocket socket, JsonObject hello, string key, string hostProof)
    {
        if (!HostProtocol.FixedTimeEquals(HostProtocol.String(hello, "key"), key) |
            !HostProtocol.FixedTimeEquals(HostProtocol.String(hello, "proof"), hostProof))
            return HostHelloVerdict.Rejected;
        var id = HostProtocol.Integer(hello, "id") ?? 1;
        if (HostProtocol.Integer(hello, "protocol") is { } protocol && protocol != HostProtocol.Version)
        {
            var reply = HostProtocol.Error(id, "failed", "protocol-mismatch",
                $"The host speaks runic-host version {protocol}; this application chose {HostProtocol.Version}.");
            reply["protocol"] = HostProtocol.Version;
            await SendAndCloseAsync(socket, reply, WebSocketCloseStatus.ProtocolError).ConfigureAwait(false);
            throw Failure("protocol-unsupported", "The host's hello named another protocol version.");
        }
        if (HostProtocol.Integer(hello, "id") is not >= 1 || HostProtocol.Integer(hello, "protocol") is null ||
            HostProtocol.Product(hello, "host") is null || !ValidFeatures(hello["features"]))
        {
            var reply = HostProtocol.Error(id, "rejected", "invalid-request", "The hello is missing a member or has one of the wrong type.");
            await SendAndCloseAsync(socket, reply, WebSocketCloseStatus.ProtocolError).ConfigureAwait(false);
            throw Failure("handshake-invalid", "The host's hello is not valid.");
        }
        return HostHelloVerdict.Accepted;
    }

    private static bool ValidFeatures(JsonNode? features) =>
        features is null || features is JsonArray { Count: <= 64 } list &&
        list.All(static item => item is JsonValue value && value.TryGetValue<string>(out var name) && name.Length <= 64);

    private static async Task SendAndCloseAsync(WebSocket socket, JsonObject reply, WebSocketCloseStatus status)
    {
        try { await HostChannel.SendAsync(socket, reply, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is WebSocketException or IOException or ObjectDisposedException) { }
        await HostChannel.CloseAsync(socket, status, "").ConfigureAwait(false);
    }

    // Standard input ends or the channel closes: the host is gone (host-protocol.md, Host gone).
    private async Task WatchHostAsync(HostChannel channel)
    {
        var ended = await Task.WhenAny(channel.Closed, _input.Ended).ConfigureAwait(false);
        BeginShutdown(ended == _input.Ended ? "standard input ended" : "the host channel closed");
    }

    // Closes every Window, requests the Desktop host's shutdown and ends the process if the body does not return in time.
    internal void BeginShutdown(string reason)
    {
        if (_ended.IsCancellationRequested || Interlocked.Exchange(ref _shutdown, 1) != 0) return;
        DesktopLog.HostedShutdown(_logger, reason);
        _hostGone.Cancel();
        foreach (var window in _windows.Values) _ = CloseQuietlyAsync(window);
        _desktop.RequestShutdown($"Runic host protocol: {reason}");
        _ = ExitIfStillRunningAsync();
    }

    private async Task CloseQuietlyAsync(HostedWindow window)
    {
        try { await window.CloseAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { DesktopLog.HostedChannelFailed(_logger, error, error.GetType().Name); }
    }

    private async Task ExitIfStillRunningAsync()
    {
        try { await Task.Delay(_options.HostGoneExitTimeout, _ended.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        DesktopLog.HostedExit(_logger, (int)_options.HostGoneExitTimeout.TotalSeconds);
        _options.ExitProcess(1);
    }

    // window-open: a new window id; the host's reply opens it or refuses it.
    internal async Task<HostedWindow> OpenWindowAsync(HostedWindowRequest request, Func<CancellationToken, ValueTask<bool>>? guard,
        Func<Task> close, CancellationToken cancellationToken)
    {
        if (IsHostGone || _channel is not { IsOpen: true } channel) throw HostGone();
        var id = "w" + Interlocked.Increment(ref _nextWindow).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var window = new HostedWindow(this, id, guard, close);
        _windows[id] = window;
        var message = new JsonObject
        {
            ["id"] = 0,
            ["kind"] = "window-open",
            ["window"] = id,
            ["type"] = request.Type,
            ["url"] = request.Url.AbsoluteUri,
            ["width"] = request.Width,
            ["height"] = request.Height,
        };
        if (request.MinWidth is { } minWidth) message["minWidth"] = minWidth;
        if (request.MinHeight is { } minHeight) message["minHeight"] = minHeight;
        message["owner"] = request.Owner;
        message["modality"] = request.Modal ? "window" : "none";
        message["guardClose"] = request.GuardClose;
        message["permissions"] = request.MediaCapture ? new JsonArray("media-capture") : new JsonArray();
        JsonObject reply;
        try { reply = await channel.RequestAsync(message, cancellationToken).ConfigureAwait(false); }
        catch (HostChannelClosedException)
        {
            window.OpenFailed();
            _windows.TryRemove(id, out _);
            throw HostGone();
        }
        catch
        {
            window.OpenFailed();
            throw;
        }
        if (HostProtocol.Boolean(reply, "ok") != true)
        {
            window.OpenFailed();
            _windows.TryRemove(id, out _);
            var error = reply["error"] as JsonObject;
            var refused = error is not null && HostProtocol.String(error, "reason") == "refused";
            var detail = error is not null ? HostProtocol.String(error, "message") : null;
            throw new RunicWindowConfigurationException([new RunicWindowDiagnostic(
                refused ? "hosted-window-refused" : "hosted-window-failed",
                refused
                    ? $"The host application refused the window{(detail is null ? "." : ": " + detail)}"
                    : $"The host application could not open the window{(detail is null ? "." : ": " + detail)}",
                refused ? "The host's window policy decides which windows a guest may open." : null)]);
        }
        var presentation = new HostedWindowPresentation(
            HostedWindowPresentation.FeaturesOf(reply["features"] as JsonArray),
            HostedWindowPresentation.ActivityOf(reply["activity"] as JsonObject) ?? RunicWindowActivity.Assumed,
            HostedWindowPresentation.ColorSchemeOf(HostProtocol.String(reply, "colorScheme")) ?? DesktopColorScheme.NoPreference,
            (presentationRequest, token) => WindowRequestAsync(id, presentationRequest, token));
        window.Opened(presentation);
        return window;
    }

    // The hosted window of an open Window of this guest, for window-open's owner.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IRunicWindow, HostedWindow> _byWindow = [];

    internal void Track(IRunicWindow window, HostedWindow hosted) => _byWindow.AddOrUpdate(window, hosted);

    internal string? WindowId(IRunicWindow window) =>
        _byWindow.TryGetValue(window, out var hosted) && !hosted.IsClosed ? hosted.Id : null;

    private static RunicWindowConfigurationException HostGone() => new([new RunicWindowDiagnostic("hosted-host-gone",
        "The host application has gone away, so the hosted application opens no more windows.",
        "A hosted application never shows a window of its own; end the application.")]);

    private async Task<JsonObject?> WindowRequestAsync(string window, JsonObject request, CancellationToken cancellationToken)
    {
        if (_channel is not { IsOpen: true } channel) return null;
        request["window"] = window;
        try { return await channel.RequestAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (HostChannelClosedException) { return null; }
    }

    internal async Task PushAsync(JsonObject push)
    {
        if (_channel is not { IsOpen: true } channel) return;
        try { await channel.PushAsync(push).ConfigureAwait(false); }
        catch (HostChannelClosedException) { }
    }

    private async Task OnRequestAsync(HostRequest request)
    {
        switch (request.Kind)
        {
            case "close-request":
                if (FindWindow(request.Message) is not { } window)
                {
                    await request.FailAsync("rejected", "invalid-request", "The request names no window of this channel.").ConfigureAwait(false);
                    return;
                }
                var allow = await window.DecideCloseAsync(HostProtocol.Boolean(request.Message, "force") == true).ConfigureAwait(false);
                await request.ReplyAsync(new JsonObject { ["allow"] = allow }).ConfigureAwait(false);
                if (allow) await CloseQuietlyAsync(window).ConfigureAwait(false);
                return;
            case "shutdown":
                await request.ReplyAsync().ConfigureAwait(false);
                BeginShutdown("the host asked to shut down");
                return;
            default:
                await request.FailAsync("rejected", "invalid-request", $"'{request.Kind}' is not a request this guest serves.").ConfigureAwait(false);
                return;
        }
    }

    private void OnPush(JsonObject push)
    {
        switch (HostProtocol.String(push, "kind"))
        {
            case "activity":
                if (FindWindow(push)?.Presentation is { } activityWindow && HostedWindowPresentation.ActivityOf(push) is { } activity)
                    activityWindow.OnActivity(activity);
                return;
            case "color-scheme":
                if (FindWindow(push)?.Presentation is { } schemeWindow &&
                    HostedWindowPresentation.ColorSchemeOf(HostProtocol.String(push, "colorScheme")) is { } scheme)
                    schemeWindow.OnColorScheme(scheme);
                return;
            // placement, appearance, locale and unknown pushes: not followed yet, ignored.
        }
    }

    private HostedWindow? FindWindow(JsonObject message) =>
        HostProtocol.String(message, "window") is { } id && _windows.TryGetValue(id, out var window) ? window : null;

    private void WriteLine(string line)
    {
        var output = _options.Output;
        // One write, so the application's own output cannot come between.
        output.Write(line);
        output.Flush();
    }

    private RunicHostedApplicationException Refuse(string code, string message, bool withProtocols = false)
    {
        var refusal = new JsonObject { ["code"] = code, ["message"] = message };
        if (withProtocols) refusal["protocols"] = new JsonArray(HostProtocol.Version);
        WriteLine(HostProtocol.Line(HostProtocol.RefusedPrefix, refusal));
        return Failure(code, message);
    }

    private static RunicHostedApplicationException Failure(string code, string message) => new(code, message);

    private static async Task IgnoreAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    // The body returned: no exit watchdog; the channel closes normally.
    public async ValueTask DisposeAsync()
    {
        await _ended.CancelAsync().ConfigureAwait(false);
        // The Windows that are closing, such as after the host's shutdown, push window-closed before the channel closes.
        try
        {
            await Task.WhenAll(_windows.Values.Where(static window => window.IsClosing).Select(static window => window.Finished))
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        await _hostGone.CancelAsync().ConfigureAwait(false);
        if (_listener is { } listener) await listener.DisposeAsync().ConfigureAwait(false);
        if (_channel is { } channel) await channel.CloseAsync(WebSocketCloseStatus.NormalClosure, "").ConfigureAwait(false);
    }
}
