using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Runic.Desktop;
using Runic.Desktop.Internal;
using Runic.Platform;

namespace Runic.Application.Views.Desktop.Hosting;

// The window-open request of one Window (host-protocol.md, window-open).
internal sealed record HostedWindowRequest(string Type, Uri Url, int Width, int Height, int? MinWidth, int? MinHeight,
    string? Owner, bool Modal, bool GuardClose, bool MediaCapture)
{
    // The Window's locale, for the web view's language and the window's direction; null for none.
    internal string? Locale { get; init; }

    // Where the Window's dropped files go (window-services/1, files-dropped); null when it takes none. Set before the
    // request goes, so a drop right after the reply is not lost.
    internal Action<DesktopFileDropEventArgs>? Drop { get; init; }

    // The Window's surface, which serves its page in a host page's frame when the host shows the window in a region
    // (region/1); null when the Window cannot show in one.
    internal DesktopSurface? Surface { get; init; }

    // The Window's content session, whose pages receive a region's appearance override (region-appearance/1) before the
    // window opens; null for none.
    internal WindowContentSession? Content { get; init; }
}

// Where a window is for its appearance override (region-appearance/1): waiting for its window-open reply, a region
// window, or a window that takes none.
internal enum HostedAppearanceState
{
    Opening,
    Region,
    None,
}

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

    internal HostedGuest Guest => _guest;

    internal HostedWindowPresentation? Presentation { get; private set; }

    // menus/1: the window's menu bar, once the window opened with the feature in use.
    internal HostedWindowMenus? Menus { get; private set; }

    // region/1: the region that shows the window, when its window-open reply named presentation "region".
    internal HostedRegion? Region { get; private set; }

    // region-appearance/1, under the guest's appearance gate: the window's override, and whether it is a region window.
    // A push that arrives before the guest read the window-open reply is newer than the reply's override.
    internal DesktopAppearance? AppearanceOverride { get; set; }

    internal bool AppearancePushed { get; set; }

    internal HostedAppearanceState AppearanceState { get; set; }

    // Completes with whether the window opened, when its window-open reply arrived.
    internal Task<bool> WhenOpened => _opened.Task;

    // Starts the window's menu bar; a window that closed meanwhile disposes it.
    internal void UseMenus(HostedWindowMenus menus)
    {
        lock (_gate)
        {
            if (!IsClosed)
            {
                Menus = menus;
                menus.Start();
                return;
            }
        }
        menus.Dispose();
    }

    internal bool IsClosed => Volatile.Read(ref _closed) != 0;

    internal bool IsClosing => Volatile.Read(ref _closing) != 0;

    // Completes once window-closed was pushed, or not needed.
    internal Task Finished => _finished.Task;

    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Opened(HostedWindowPresentation presentation, HostedRegion? region = null)
    {
        Presentation = presentation;
        Region = region;
        _opened.TrySetResult(true);
    }

    internal void OpenFailed() => _opened.TrySetResult(false);

    // window-services/1: the Window's drops, which the host forwards (files-dropped); null when its root ViewModel takes
    // none.
    internal Action<Runic.Desktop.DesktopFileDropEventArgs>? DropTarget { get; set; }

    // window-services/1: completes at the host's next monitors-changed for the window, or when it closes.
    private TaskCompletionSource _monitorsChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task MonitorsChanged() => Volatile.Read(ref _monitorsChanged).Task;

    internal void OnMonitorsChanged()
    {
        var next = IsClosed ? _monitorsChanged : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _monitorsChanged, next).TrySetResult();
    }

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

    // The Window finished closing, for any reason: window-closed, after the window-open reply when it was pending. The
    // guest then forgets the window and keeps only its id.
    internal async Task OnClosedAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        DropTarget = null;
        OnMonitorsChanged();
        HostedWindowMenus? menus;
        lock (_gate) (menus, Menus) = (Menus, null);
        menus?.Dispose();
        Region?.Dispose();
        Interlocked.Exchange(ref _closing, 1);
        try
        {
            if (!await _opened.Task.ConfigureAwait(false)) return;
            Presentation?.MarkClosed();
            await _guest.PushAsync(new JsonObject { ["kind"] = "window-closed", ["window"] = Id }).ConfigureAwait(false);
        }
        finally
        {
            _guest.Forget(this);
            _finished.TrySetResult();
        }
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
    // The windows that are opening or open; a closed window's id moves to _closedIds (host-protocol.md, window-closed).
    private readonly ConcurrentDictionary<string, HostedWindow> _windows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _closedIds = new(StringComparer.Ordinal);
    // window-open replies the guest still waits for after it gave the open up, to close what the host opened late.
    private readonly ConcurrentDictionary<Task, byte> _abandonedOpens = new();
    private readonly CancellationTokenSource _ended = new();
    private readonly CancellationTokenSource _hostGone = new();
    // The application's locale, which follows the host's (host-protocol.md, Appearance and locale); null without one.
    private readonly IRunicApplicationLocale? _locale;
    // The host's appearance, which the Windows' pages show, and their content sessions.
    private readonly object _appearanceGate = new();
    // Each session's region window once its window-open reply was read, else null.
    private readonly Dictionary<WindowContentSession, HostedWindow?> _appearanceSessions = [];
    private DesktopAppearance? _appearance;
    // One locale switch at a time, in the order the host's pushes arrived; the last locale this guest followed.
    private readonly SemaphoreSlim _localeSwitch = new(1, 1);
    private string? _followedLocale;
    private HostChannel? _channel;
    private HostChannelListener? _listener;
    private int _nextWindow;
    private int _shutdown;

    // Whether this guest runs elevated (RunicGuestOptions.RunsElevated).
    internal bool RunsElevated => _options.RunsElevated();

    private HostedGuest(RunicGuestOptions options, DesktopHost desktop, ILogger logger, IRunicApplicationLocale? locale)
    {
        _options = options;
        _desktop = desktop;
        _logger = logger;
        _locale = locale;
        _input = new HostInput(options.OpenInput());
    }

    internal RunicGuestSession Session { get; private set; } = null!;

    internal bool IsHostGone => _shutdown != 0;

    // This guest runs elevated, hosted by its elevated parent (host-protocol.md, Elevated host).
    internal bool IsElevatedPair { get; private set; }

    // An elevated guest without elevated-peer admission, on a system where Runic cannot look peers up (macOS), refuses
    // region and component mode: whoever renders a region's page would otherwise not be checked.
    internal bool RefusesEmbedding => IsElevatedPair && !_options.CanCheckPeers;

    // Refused peers are logged at most this often, so a process that keeps connecting does not flood the log.
    private const int MaximumRefusalLogsPerMinute = 10;
    private readonly Queue<long> _refusalLogs = new();

    private void OnPeerRefused(System.Net.IPEndPoint peer)
    {
        lock (_refusalLogs)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            while (_refusalLogs.Count > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_refusalLogs.Peek(), now) > TimeSpan.FromMinutes(1))
                _refusalLogs.Dequeue();
            if (_refusalLogs.Count >= MaximumRefusalLogsPerMinute) return;
            _refusalLogs.Enqueue(now);
        }
        DesktopLog.HostedPeerRefused(_logger, peer.ToString());
    }

    // Runs the launch contract and the handshake; throws RunicHostedApplicationException when the host cannot host it.
    internal static async Task<HostedGuest> StartAsync(RunicGuestOptions options, DesktopHost desktop, ILogger logger,
        IRunicApplicationLocale? locale = null)
    {
        var guest = new HostedGuest(options, desktop, logger, locale);
        try
        {
            await guest.HandshakeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await guest.DisposeAsync().ConfigureAwait(false);
            if (error is RunicHostedApplicationException failure) DesktopLog.HostedStartFailed(logger, failure.Code, failure.Message);
            throw;
        }
        // The application starts in the host's locale, before its body opens a Window; an application that gives its own
        // locale up follows the host's again.
        await guest.FollowLocaleAsync().ConfigureAwait(false);
        if (locale is RunicApplicationLocale own) own.HostLocale = guest.HostLocaleToFollow;
        if (locale is not null) locale.LocaleChanged += guest.OnApplicationLocaleChanged;
        return guest;
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
        var facts = _options.ReadPrivileges();
        var privileges = GuestPrivileges.Decide(facts, _options.AllowElevatedHost, offer.ElevatedGuest);
        if (privileges.Code is { } refusal) throw Refuse(refusal, privileges.Message!);
        if (privileges.Message is { } elevated) DesktopLog.HostedElevated(_logger, elevated);
        if (_desktop.Options.NetworkExposure != DesktopNetworkExposure.Loopback)
            throw Refuse("network-exposure", "The application's Desktop host listens on other interfaces than loopback.");

        // An elevated pair: every listener, the channel's and the Desktop host's, admits only elevated peers, checked
        // before anything is read (host-protocol.md, Elevated-peer admission). Where Runic cannot look peers up (macOS),
        // the listeners keep the pair's other protections, and region and component mode are refused.
        LoopbackPeerAdmission? admission = null;
        if (facts.Elevated)
        {
            IsElevatedPair = true;
            if (_options.CanCheckPeers)
            {
                admission = new LoopbackPeerAdmission(_options.CheckPeer, OnPeerRefused);
                _desktop.PeerAdmission = admission;
            }
        }
        _listener = HostChannelListener.Start(_options.UnauthenticatedTimeout, _options.HelloTimeout, admission is null ? null : admission.Admit);
        var key = HostProtocol.NewSecret();
        var hostProof = HostProtocol.Proof(offer.Nonce, HostProtocol.HostProofLabel, key);
        var guestProof = HostProtocol.Proof(offer.Nonce, HostProtocol.GuestProofLabel, key);
        WriteLine(HostProtocol.Line(HostProtocol.ChannelPrefix, new JsonObject
        {
            ["url"] = _listener.Url.AbsoluteUri,
            ["key"] = key,
            ["protocol"] = HostProtocol.Version,
        }));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var accepting = _listener.AcceptAsync((socket, hello) => AuthenticateAsync(socket, hello, key, hostProof, guestProof), stop.Token);
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

        // The key is spent: nothing listens any more. A feature is in use when both hellos list it (host-protocol.md, Features).
        var implemented = GuestFeatures(host);
        var features = new JsonArray([.. implemented.Select(static name => (JsonNode)name)]);
        var offered = hello["features"] is JsonArray list
            ? list.Select(static item => item is JsonValue value && value.TryGetValue<string>(out var name) ? name : null).ToHashSet(StringComparer.Ordinal)
            : [];
        Session = new RunicGuestSession(host, HostProtocol.Version, [.. implemented.Where(offered.Contains)], _hostGone.Token);
        // The host's appearance and locale (host-protocol.md, Handshake); members that are not valid read as unknown.
        _appearance = HostServices.ReadAppearance(hello["appearance"]);
        var (hostLocale, languages) = HostServices.ReadLocale(hello);
        Session.Update(_appearance, hostLocale, languages);
        try
        {
            await HostChannel.SendAsync(socket, new JsonObject
            {
                ["id"] = HostProtocol.Integer(hello, "id"),
                ["ok"] = true,
                ["protocol"] = HostProtocol.Version,
                ["proof"] = guestProof,
                ["guest"] = new JsonObject
                {
                    ["name"] = _options.ResolvedName,
                    ["version"] = _options.ResolvedVersion,
                    ["applicationId"] = _options.ApplicationId,
                },
                ["features"] = features,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            socket.Dispose();
            throw Failure("host-gone", "The host's connection closed before it took the hello reply.");
        }
        _channel = new HostChannel(socket, firstRequestId: 1, OnRequestAsync, OnPush, LogFailure,
            _options.SendTimeout, _options.MaximumWaitingRequests);
        _channel.Start();
        _ = WatchHostAsync(_channel);
    }

    // The checks of host-protocol.md, Guest rules: key and proof first, then the protocol and the members. Every reply
    // to an authenticated hello, a failed one too, carries the guest proof, so the host can trust what it says.
    private static async Task<HostHelloVerdict> AuthenticateAsync(WebSocket socket, JsonObject hello, string key, string hostProof,
        string guestProof)
    {
        if (!HostProtocol.FixedTimeEquals(HostProtocol.String(hello, "key"), key) |
            !HostProtocol.FixedTimeEquals(HostProtocol.String(hello, "proof"), hostProof))
            return HostHelloVerdict.Rejected;
        long? id = HostProtocol.Integer(hello, "id") is >= 1 and var valid ? valid : null;
        if (HostProtocol.Integer(hello, "protocol") is { } protocol && protocol != HostProtocol.Version)
        {
            await SendAndCloseAsync(socket, FailedHelloReply(id, guestProof, "failed", "protocol-mismatch",
                $"The host speaks runic-host version {protocol}; this application chose {HostProtocol.Version}."),
                WebSocketCloseStatus.ProtocolError).ConfigureAwait(false);
            throw Failure("protocol-unsupported", "The host's hello named another protocol version.");
        }
        if (id is null || HostProtocol.Integer(hello, "protocol") is null ||
            HostProtocol.Product(hello, "host") is null || !ValidFeatures(hello["features"]))
        {
            await SendAndCloseAsync(socket, FailedHelloReply(id, guestProof, "rejected", "invalid-request",
                "The hello is missing a member or has one of the wrong type."), WebSocketCloseStatus.ProtocolError).ConfigureAwait(false);
            throw Failure("handshake-invalid", "The host's hello is not valid.");
        }
        return HostHelloVerdict.Accepted;
    }

    // {"id", "ok": false, "protocol", "proof", "error"}: the id is null for a hello without a valid one.
    private static JsonObject FailedHelloReply(long? id, string guestProof, string kind, string reason, string message)
    {
        var error = HostProtocol.Error(0, kind, reason, message)["error"]!.DeepClone();
        return new JsonObject
        {
            ["id"] = id,
            ["ok"] = false,
            ["protocol"] = HostProtocol.Version,
            ["proof"] = guestProof,
            ["error"] = error,
        };
    }

    private static bool ValidFeatures(JsonNode? features) =>
        features is null || features is JsonArray { Count: <= 64 } list &&
        list.All(static item => item is JsonValue value && value.TryGetValue<string>(out var name) && name.Length <= 64);

    private static async Task SendAndCloseAsync(WebSocket socket, JsonObject reply, WebSocketCloseStatus status)
    {
        try { await HostChannel.SendAsync(socket, reply, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException) { }
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
        catch (Exception error) when (error is not OutOfMemoryException) { LogFailure(error); }
    }

    private void LogFailure(Exception error) => DesktopLog.HostedChannelFailed(_logger, error, error.GetType().Name);

    private async Task ExitIfStillRunningAsync()
    {
        // The Windows that drain (RunicCloseDrain, sdk#900) first: the shutdown ends their drains at ShutdownDrainTimeout.
        try { await Task.Delay(Timeout.Infinite, _desktop.DrainsEnded).WaitAsync(_ended.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!_ended.IsCancellationRequested) { }
        catch (OperationCanceledException) { return; }
        try { await Task.Delay(_options.HostGoneExitTimeout, _ended.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        DesktopLog.HostedExit(_logger, (int)_options.HostGoneExitTimeout.TotalSeconds);
        _options.ExitProcess(1);
    }

    // window-open: a new window id; the host's reply opens it or refuses it. The open ends early when the caller
    // cancels it, the request timeout passes or the host goes away; the guest then keeps waiting for the reply and
    // pushes window-closed when it was successful, so the host keeps no window whose Window is gone.
    internal async Task<HostedWindow> OpenWindowAsync(HostedWindowRequest request, Func<CancellationToken, ValueTask<bool>>? guard,
        Func<Task> close, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsHostGone || _channel is not { IsOpen: true } channel) throw HostGone();
        var id = "w" + Interlocked.Increment(ref _nextWindow).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var window = new HostedWindow(this, id, guard, close);
        if (InUse(HostServices.Feature)) window.DropTarget = request.Drop;
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
        if (request.Locale is { } windowLocale && HostServices.IsLocale(windowLocale)) message["locale"] = windowLocale;
        if (window.DropTarget is not null) message["fileDrops"] = true;
        Task<JsonObject> replied;
        try { replied = await channel.BeginRequestAsync(message, CancellationToken.None).ConfigureAwait(false); }
        catch (HostChannelClosedException)
        {
            Forget(window);
            throw HostGone();
        }
        JsonObject reply;
        using (var giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _hostGone.Token))
        {
            try { reply = await replied.WaitAsync(_options.RequestTimeout, giveUp.Token).ConfigureAwait(false); }
            catch (HostChannelClosedException)
            {
                Forget(window);
                throw HostGone();
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
                Abandon(window, replied);
                if (IsHostGone) throw HostGone();
                if (error is TimeoutException)
                    throw new RunicWindowConfigurationException([new RunicWindowDiagnostic("hosted-window-failed",
                        "The host application did not answer the window's open within the request timeout.",
                        "The host is busy or not responding; the guest closes the window if the host opens it later.")], error);
                throw new OperationCanceledException("The window's open was cancelled.", error, cancellationToken);
            }
        }
        if (HostProtocol.Boolean(reply, "ok") != true)
        {
            Forget(window);
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
        // presentation: "window" by default, or "region" with region/1 in use. A value the guest does not know closes the
        // Window, as an open it gave up (host-protocol.md, Presentation of a window).
        var shownIn = HostProtocol.String(reply, "presentation");
        if (reply["presentation"] is not null && shownIn is not ("window" or "region") ||
            shownIn == "region" && (!InUse(HostFeatures.Region) || request.Surface is null))
        {
            Forget(window);
            await PushAsync(new JsonObject { ["kind"] = "window-closed", ["window"] = id }).ConfigureAwait(false);
            throw new RunicWindowConfigurationException([new RunicWindowDiagnostic("hosted-window-failed",
                "The host application opened the window with a presentation this application does not implement.",
                "The host is newer than this application, or did not check the features in use.")]);
        }
        var region = shownIn == "region" ? new HostedRegion(this, id, request.Surface!) : null;
        // region-appearance/1: the region window's override reaches its pages before the window opens, so before a
        // region-attach can admit a page (host-protocol.md, Appearance in a region, Ordering).
        OpenedForAppearance(window, request.Content, region is not null && UsesRegionAppearance ? reply : null);
        var presentation = new HostedWindowPresentation(
            HostedWindowPresentation.FeaturesOf(reply["features"] as JsonArray),
            HostedWindowPresentation.ActivityOf(reply["activity"] as JsonObject) ?? RunicWindowActivity.Assumed,
            HostedWindowPresentation.ColorSchemeOf(HostProtocol.String(reply, "colorScheme")) ?? DesktopColorScheme.NoPreference,
            (presentationRequest, token) => WindowRequestAsync(id, presentationRequest, token), LogFailure,
            HostedWindowPresentation.PlacementOf(reply["placement"] as JsonObject));
        window.Opened(presentation, region);
        return window;
    }

    // The open ended before its reply: the window is forgotten now, and a successful reply that still arrives is
    // followed by window-closed (host-protocol.md, window-open).
    private void Abandon(HostedWindow window, Task<JsonObject> replied)
    {
        Forget(window);
        var closing = CloseLateWindowAsync(window.Id, replied);
        _abandonedOpens[closing] = 0;
        _ = closing.ContinueWith(task => _abandonedOpens.TryRemove(task, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task CloseLateWindowAsync(string id, Task<JsonObject> replied)
    {
        JsonObject reply;
        try { reply = await replied.ConfigureAwait(false); }
        catch (HostChannelClosedException) { return; }
        if (HostProtocol.Boolean(reply, "ok") == true)
            await PushAsync(new JsonObject { ["kind"] = "window-closed", ["window"] = id }).ConfigureAwait(false);
    }

    // A window that closed or never opened: its id names a closed window from now on.
    internal void Forget(HostedWindow window)
    {
        window.OpenFailed();
        _closedIds[window.Id] = 0;
        _windows.TryRemove(window.Id, out _);
    }

    // The hosted window of an open Window of this guest, for window-open's owner.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IRunicWindow, HostedWindow> _byWindow = [];

    internal void Track(IRunicWindow window, HostedWindow hosted) => _byWindow.AddOrUpdate(window, hosted);

    // region/1: the region of an open Window that its host shows in one, or null.
    internal HostedRegion? RegionOf(IRunicWindow window) =>
        _byWindow.TryGetValue(window, out var hosted) && !hosted.IsClosed ? hosted.Region : null;

    internal string? WindowId(IRunicWindow window) =>
        _byWindow.TryGetValue(window, out var hosted) && !hosted.IsClosed ? hosted.Id : null;

    internal static RunicWindowConfigurationException HostGone() => new([new RunicWindowDiagnostic("hosted-host-gone",
        "The host application has gone away, so the hosted application opens no more windows.",
        "A hosted application never shows a window of its own; end the application.")]);

    // The features this guest lists: region-component/1 only when the application allows component embedding for this
    // host (RunicGuestOptions.AllowComponentEmbedding), and, in an elevated guest, only once its listeners admit elevated
    // peers alone (ComponentModeAvailable; host-protocol.md, Component mode).
    private string[] GuestFeatures(RunicHostInfo host) =>
        _options.AllowComponentEmbedding is { } allow && ComponentModeAvailable && allow(host)
            ? [.. HostFeatures.Implemented, HostFeatures.Region, HostFeatures.RegionComponent, HostFeatures.RegionAppearance]
            : HostFeatures.GuestImplemented;

    // An elevated guest's component would run in its host page's realm with the guest's token: only with elevated-peer
    // admission on its listeners (D059.13), which an elevated pair applies wherever it can check its peers; where it
    // cannot (macOS), it refuses every region-attach, component included (RefusesEmbedding).
    internal bool ComponentModeAvailable => !RefusesEmbedding;

    internal bool InUse(string feature) => Session.Features.Contains(feature, StringComparer.Ordinal);

    // A request for a window that waits for the user (host-protocol.md, Giving up a request): no request timeout; giving
    // it up sends the cancel push and, unless awaitReply says otherwise, waits for the host's reply, which closes the
    // dialog, before it throws. A window that is closing does not wait: the host closes its dialogs when window-closed
    // arrives, which the close sends only after it. Null when the channel closed.
    internal async Task<JsonObject?> UserRequestAsync(string window, JsonObject request, CancellationToken cancellationToken,
        Func<bool>? awaitReply = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_channel is not { IsOpen: true } channel) return null;
        request["window"] = window;
        Task<JsonObject> replied;
        try { replied = await channel.BeginRequestAsync(request, CancellationToken.None).ConfigureAwait(false); }
        catch (HostChannelClosedException) { return null; }
        try { return await replied.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (HostChannelClosedException) { return null; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await PushAsync(new JsonObject { ["kind"] = "cancel", ["request"] = HostProtocol.Integer(request, "id") }).ConfigureAwait(false);
            // The host answers once its dialog closed; until then the window's next dialog would find it busy.
            if (awaitReply?.Invoke() != false)
            {
                try { _ = await replied.WaitAsync(_options.RequestTimeout, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (error is HostChannelClosedException or TimeoutException) { }
            }
            throw;
        }
    }

    internal async Task<JsonObject?> WindowRequestAsync(string window, JsonObject request, CancellationToken cancellationToken)
    {
        if (_channel is not { IsOpen: true } channel) return null;
        request["window"] = window;
        try { return await channel.RequestAsync(request, cancellationToken, _options.RequestTimeout).ConfigureAwait(false); }
        catch (HostChannelClosedException) { return null; }
        // No reply within the request timeout: failed with unknown (host-protocol.md, Platform outcomes).
        catch (TimeoutException) { return HostProtocol.Error(0, "failed", null, "The host application did not answer within the request timeout."); }
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
                    // A window that closed allows its close (host-protocol.md, close-request).
                    if (HostProtocol.String(request.Message, "window") is { } closedId && _closedIds.ContainsKey(closedId))
                        await request.ReplyAsync(new JsonObject { ["allow"] = true }).ConfigureAwait(false);
                    else
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
            case "region-attach" when InUse(HostFeatures.Region):
                if (RefusesEmbedding)
                {
                    await request.FailAsync("rejected", "refused",
                        "The application runs elevated, and cannot check on this system that the process that renders a region runs elevated.").ConfigureAwait(false);
                    return;
                }
                // The host may attach as soon as it answered window-open, before the guest finished reading the reply.
                if (await RegionOfAsync(request.Message).ConfigureAwait(false) is { } attached)
                    await attached.AttachAsync(request).ConfigureAwait(false);
                else
                    await request.FailAsync("rejected", "invalid-request", "The request names no region window of this channel.").ConfigureAwait(false);
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
            case "placement":
                if (FindWindow(push)?.Presentation is { } placementWindow && HostedWindowPresentation.PlacementOf(push) is { } placement)
                    placementWindow.OnPlacement(placement);
                return;
            case "color-scheme":
                if (FindWindow(push)?.Presentation is { } schemeWindow &&
                    HostedWindowPresentation.ColorSchemeOf(HostProtocol.String(push, "colorScheme")) is { } scheme)
                    schemeWindow.OnColorScheme(scheme);
                return;
            case "menu-invoke":
                if (FindWindow(push)?.Menus is { } menus && HostProtocol.String(push, "itemId") is { } itemId) menus.Invoke(itemId);
                return;
            case "appearance":
                OnAppearance(HostServices.ReadAppearance(push));
                return;
            case "locale":
                var (locale, languages) = HostServices.ReadLocale(push);
                Session.Update(Session.Appearance, locale, languages);
                _ = FollowLocaleAsync();
                return;
            case "files-dropped" when InUse(HostServices.Feature):
                if (FindWindow(push) is { DropTarget: { } drop } && HostServices.CheckDrop(push))
                {
                    var paths = ((JsonArray)push["paths"]!).Select(static path => path!.GetValue<string>()).ToArray();
                    var target = HostProtocol.String(push, "target");
                    // Off the receive loop, as the drop reaches the root ViewModel.
                    _ = Task.Run(() => drop(new DesktopFileDropEventArgs(paths, target) { Effect = HostServices.Effect(HostProtocol.String(push, "effect")) }));
                }
                return;
            case "monitors-changed" when InUse(HostServices.Feature):
                FindWindow(push)?.OnMonitorsChanged();
                return;
            // region/1: a push for a window that is not a region window of the channel is ignored.
            case "region-detach" when InUse(HostFeatures.Region):
                FindWindow(push)?.Region?.Detach();
                return;
            case "region-bounds" when InUse(HostFeatures.Region):
                FindWindow(push)?.Region?.OnBounds(push);
                return;
            case "region-focus" when InUse(HostFeatures.Region):
                FindWindow(push)?.Region?.OnFocus(push);
                return;
            case "region-appearance" when UsesRegionAppearance:
                if (FindWindow(push) is { } overridden) OnRegionAppearance(overridden, push);
                return;
            // placement and unknown pushes: ignored.
        }
    }

    // ----- Appearance and locale (host-protocol.md, Appearance and locale)

    // The host's appearance reaches the Window's pages (appearance/1) while it is open, instead of this process's own
    // desktop settings: the hosted windows show on the host's desktop. A region window's pages receive it merged with the
    // window's override (region-appearance/1), once its window-open reply bound the session to the window.
    internal IDisposable FollowAppearance(WindowContentSession session)
    {
        lock (_appearanceGate)
        {
            _appearanceSessions[session] = null;
            ApplyAppearance(session, null);
        }
        return new AppearanceRegistration(this, session);
    }

    // region-appearance/1 in use: the host lists it, and region/1, which it requires.
    private bool UsesRegionAppearance => InUse(HostFeatures.RegionAppearance) && InUse(HostFeatures.Region);

    private void OnAppearance(DesktopAppearance? appearance)
    {
        Session.Update(appearance, Session.Locale, Session.Languages);
        // Under the gate, so a window's override and a desktop change reach its pages in the order the guest read them.
        lock (_appearanceGate)
        {
            _appearance = appearance;
            foreach (var (session, window) in _appearanceSessions) ApplyAppearance(session, window);
        }
    }

    // The window-open reply was read: a region window takes the reply's override, unless a region-appearance push
    // arrived first, which is newer; any other window takes none. reply is null for a window that takes none. Its session
    // (content) is bound to the window from now on.
    private void OpenedForAppearance(HostedWindow window, WindowContentSession? content, JsonObject? reply)
    {
        lock (_appearanceGate)
        {
            if (reply is null)
            {
                window.AppearanceState = HostedAppearanceState.None;
                window.AppearanceOverride = null;
                return;
            }
            window.AppearanceState = HostedAppearanceState.Region;
            // An appearance that is neither an object nor null opens the window without an override.
            if (!window.AppearancePushed)
                window.AppearanceOverride = HostServices.CheckAppearance(reply["appearance"]) ? HostServices.ReadOverride(reply["appearance"]) : null;
            if (content is not null && _appearanceSessions.ContainsKey(content))
            {
                _appearanceSessions[content] = window;
                ApplyAppearance(content, window);
            }
        }
    }

    // region-appearance: the region window's new override, which replaces the last one as a whole. An appearance that is
    // neither an object nor null is ignored, and so is a push for a window that is not a region window.
    private void OnRegionAppearance(HostedWindow window, JsonObject push)
    {
        if (!HostServices.CheckAppearance(push["appearance"])) return;
        var appearanceOverride = HostServices.ReadOverride(push["appearance"]);
        lock (_appearanceGate)
        {
            switch (window.AppearanceState)
            {
                case HostedAppearanceState.Opening:
                    window.AppearanceOverride = appearanceOverride;
                    window.AppearancePushed = true;
                    return;
                case HostedAppearanceState.Region:
                    window.AppearanceOverride = appearanceOverride;
                    foreach (var (session, bound) in _appearanceSessions)
                        if (ReferenceEquals(bound, window)) ApplyAppearance(session, window);
                    return;
            }
        }
    }

    // Under the gate: the desktop appearance, merged with the window's override when it is a region window's.
    private void ApplyAppearance(WindowContentSession session, HostedWindow? window)
    {
        var (appearance, decided) = HostServices.Merge(_appearance,
            window is { AppearanceState: HostedAppearanceState.Region } ? window.AppearanceOverride : null);
        try { session.SetAppearance(appearance, decided); }
        catch (ObjectDisposedException) { }
    }

    private sealed class AppearanceRegistration(HostedGuest guest, WindowContentSession session) : IDisposable
    {
        public void Dispose()
        {
            lock (guest._appearanceGate) guest._appearanceSessions.Remove(session);
        }
    }

    // The host's locale that RunicApplicationLocale switches to when the application gives its own locale up, which is the
    // locale this guest follows from then on.
    private string? HostLocaleToFollow()
    {
        var locale = Session.Locale;
        if (locale is not null) Volatile.Write(ref _followedLocale, locale);
        return locale;
    }

    // The application gave its own locale up: it follows the host's again. RunicApplicationLocale switched to the host's
    // locale itself (HostLocaleToFollow); another implementation switched to none.
    private void OnApplicationLocaleChanged(object? sender, RunicApplicationLocaleChangedEventArgs e)
    {
        if (e.Locale is null && Session.Locale is not null) _ = FollowLocaleAsync();
    }

    // The application's locale follows the host's, unless the application set its own (D055.8). Switches run one at a
    // time, each with the host's latest locale; a host without a locale changes nothing.
    private async Task FollowLocaleAsync()
    {
        if (_locale is null) return;
        try
        {
            await _localeSwitch.WaitAsync(_ended.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        try
        {
            var locale = Session.Locale;
            if (locale is null && _followedLocale is null) return;
            if (_locale is RunicApplicationLocale own)
            {
                if (await own.FollowHostAsync(locale, _ended.Token).ConfigureAwait(false)) _followedLocale = locale;
                return;
            }
            // Another implementation: the application set its own locale when it is not the one this guest followed.
            var current = _locale.Locale;
            if (current is not null && !string.Equals(current, _followedLocale, StringComparison.Ordinal)) return;
            await _locale.SetLocaleAsync(locale, _ended.Token).ConfigureAwait(false);
            _followedLocale = locale;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException) { DesktopLog.HostedLocaleFailed(_logger, error, error.GetType().Name); }
        finally
        {
            try { _localeSwitch.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    // The region of the window a request names, once its window-open reply was read; null for another window.
    private async Task<HostedRegion?> RegionOfAsync(JsonObject message)
    {
        if (FindWindow(message) is not { } window) return null;
        try
        {
            if (!await window.WhenOpened.WaitAsync(_options.RequestTimeout).ConfigureAwait(false)) return null;
        }
        catch (TimeoutException) { return null; }
        return window.IsClosed ? null : window.Region;
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
        if (_locale is not null) _locale.LocaleChanged -= OnApplicationLocaleChanged;
        if (_locale is RunicApplicationLocale own) own.HostLocale = null;
        await _ended.CancelAsync().ConfigureAwait(false);
        // The Windows that are closing, such as after the host's shutdown, push window-closed before the channel closes,
        // and so do the windows whose open the guest gave up when their reply arrives.
        try
        {
            await Task.WhenAll(_windows.Values.Where(static window => window.IsClosing).Select(static window => window.Finished)
                    .Concat(_abandonedOpens.Keys))
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        await _hostGone.CancelAsync().ConfigureAwait(false);
        if (_listener is { } listener) await listener.DisposeAsync().ConfigureAwait(false);
        if (_channel is { } channel) await channel.CloseAsync(WebSocketCloseStatus.NormalClosure, "").ConfigureAwait(false);
    }
}
