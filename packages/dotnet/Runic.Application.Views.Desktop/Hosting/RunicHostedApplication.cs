using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// A guest application that this application hosts (the Runic host protocol, <c>runic-host/1</c>): its process, its host
/// channel and the native windows that show its Windows.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IRunicHostedApplications.StartAsync"/> returns it once the guest proved itself. From then on each Window the
/// guest opens becomes a <see cref="RunicHostedWindow"/> (<see cref="IRunicHostedApplications.WindowOpened"/>), and each
/// request the guest makes for it passes <see cref="RunicHostedApplicationOptions.Policy"/>.
/// </para>
/// <para>
/// When the guest exits or crashes, or its channel closes, the host closes its windows, ends its process tree if it still
/// runs and completes <see cref="Exited"/> with its exit code; showing an error page or restarting it is the host
/// application's choice. <see cref="StopAsync"/> asks it to shut down and ends it after
/// <see cref="RunicHostedApplicationOptions.StopTimeout"/>; <see cref="RequestStopAsync"/> asks each window first;
/// <see cref="KillAsync"/> ends it at once. Disposing stops it as <see cref="StopAsync"/> does.
/// </para>
/// <para>Experimental; see <c>docs/experimental.md</c>.</para>
/// </remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class RunicHostedApplication : IAsyncDisposable
{
    private const int MaximumKeptErrorLines = 20;

    private readonly RunicHostedApplications _owner;
    private readonly RunicDesktopHost _host;
    private readonly RunicHostedApplicationOptions _options;
    private readonly GuestProcess _process;
    private readonly ILogger _logger;
    private readonly string _fileName;
    private readonly object _gate = new();
    // Every window id the guest used, and the windows that opened, closed ones included (host-protocol.md, Messages).
    private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunicHostedWindow> _windows = new(StringComparer.Ordinal);
    private readonly List<RunicHostedWindow> _open = [];
    private readonly Queue<string> _errorTail = new();
    private readonly TaskCompletionSource<string?> _launchLine = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _ending = new();
    private HostChannel? _channel;
    private string? _dataStore;
    // Set under _gate when the guest is gone: no window joins _open after that.
    private bool _ended;
    private int _stopping;

    private RunicHostedApplication(RunicHostedApplications owner, RunicDesktopHost host, RunicHostedApplicationOptions options,
        GuestProcess process, ILogger logger, string fileName)
    {
        _owner = owner;
        _host = host;
        _options = options;
        _process = process;
        _logger = logger;
        _fileName = fileName;
        _ = ReadOutputAsync();
        _ = ReadErrorsAsync();
    }

    /// <summary>The guest application as its handshake named it.</summary>
    public RunicGuestInfo Guest { get; private set; } = null!;

    /// <summary>The guest's process id.</summary>
    public int ProcessId => _process.Id;

    /// <summary>The protocol version of the channel.</summary>
    public int Protocol { get; private set; }

    /// <summary>The protocol features in use on the channel: those both sides listed. The base protocol has none.</summary>
    public IReadOnlyList<string> Features { get; private set; } = [];

    /// <summary>The guest's open windows, in the order they opened.</summary>
    public IReadOnlyList<RunicHostedWindow> Windows
    {
        get { lock (_gate) return [.. _open]; }
    }

    /// <summary>
    /// Completes with the guest's exit code once it exited and its windows closed: 128 plus the signal for a guest that a
    /// signal ended on Linux and macOS, such as 137 after <see cref="KillAsync"/>, and -1 when the code cannot be known.
    /// </summary>
    public Task<int> Exited => _exited.Task;

    internal TimeSpan StopTimeout => _options.StopTimeout;

    internal bool IsChannelOpen => _channel is { IsOpen: true };

    internal static async Task<RunicHostedApplication> StartAsync(RunicHostedApplications owner, RunicDesktopHost host,
        RunicGuestStartInfo startInfo, RunicHostedApplicationOptions options, ILogger logger, CancellationToken cancellationToken)
    {
        GuestProcess process;
        try { process = GuestProcess.Start(startInfo); }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A missing executable, one this user cannot run, or a job object the guest cannot be contained in.
            var failure = new RunicHostedApplicationException("guest-start-failed",
                $"'{startInfo.FileName}': The guest could not be started: {error.Message}", error);
            DesktopLog.HostedApplicationStartFailed(logger, startInfo.FileName, failure.Code, failure.Message);
            throw failure;
        }
        var application = new RunicHostedApplication(owner, host, options, process, logger, startInfo.FileName);
        try
        {
            await application.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            process.KillTree();
            try { _ = await process.Exited.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException) { }
            process.Dispose();
            if (error is RunicHostedApplicationException failure)
                DesktopLog.HostedApplicationStartFailed(logger, startInfo.FileName, failure.Code, failure.Message);
            throw;
        }
        DesktopLog.HostedApplicationStarted(logger, application.Guest.Name, application.Guest.Version, application.ProcessId);
        _ = application.WatchAsync();
        return application;
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.StartTimeout);
        var hostName = _options.HostName is { Length: > 0 and <= 128 } name ? name : DefaultHostName;
        var hostVersion = _options.HostVersion is { Length: > 0 and <= 64 } version ? version : DefaultHostVersion;
        var nonce = RandomNumberGenerator.GetBytes(32);
        try
        {
            // The offer carries the nonce: it is written to the guest's standard input only, never logged.
            var offer = Encoding.UTF8.GetBytes(HostProtocol.Line(HostProtocol.OfferPrefix,
                HostProtocol.Offer([HostProtocol.Version], HostProtocol.Base64Url(nonce), hostName, hostVersion)));
            await _process.StandardInput.WriteAsync(offer, deadline.Token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (IOException error) { throw Failure("guest-exited", "The guest closed its standard input before the offer.", error); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("guest-start-timeout", $"The guest did not read the offer within {_options.StartTimeout}.");
        }

        string? line;
        try { line = await _launchLine.Task.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("guest-start-timeout", $"The guest wrote no {HostProtocol.ChannelPrefix} line within {_options.StartTimeout}.");
        }
        var channel = HostLaunch.Channel(line, [HostProtocol.Version], ExitCodeText, Failure);
        // Appearance and locale follow in a later change; the guest keeps its own until then.
        var hello = await HostLaunch.HandshakeAsync(channel, nonce, hostName, hostVersion, new JsonObject
        {
            ["features"] = new JsonArray(),
            ["locale"] = null,
            ["languages"] = new JsonArray(),
            ["appearance"] = null,
        }, Failure, () => _process.Exited.IsCompleted, ExitCodeText, deadline.Token, cancellationToken).ConfigureAwait(false);
        Guest = new RunicGuestInfo(hello.Name, hello.Version, hello.ApplicationId);
        Protocol = hello.Protocol;
        Features = hello.Features;
        _dataStore = Runic.Desktop.Internal.EphemeralDataStores.Begin();
        _channel = new HostChannel(hello.Socket, firstRequestId: 2, OnRequestAsync, OnPush,
            error => DesktopLog.HostedApplicationChannelFailed(_logger, error, error.GetType().Name));
        _channel.Start();
    }

    private RunicHostedApplicationException Failure(string code, string message, Exception? inner = null, string? guestCode = null)
    {
        string errors;
        lock (_errorTail) errors = string.Join('\n', _errorTail);
        var text = errors.Length == 0 ? message : $"{message}\nThe guest's last standard error lines:\n{errors}";
        return new RunicHostedApplicationException(code, $"'{_fileName}': {text}", inner) { GuestCode = guestCode };
    }

    private string ExitCodeText() =>
        _process.Exited.IsCompletedSuccessfully ? _process.Exited.Result.ToString(CultureInfo.InvariantCulture) : "no exit code yet";

    // The channel closes or the process exits: the guest is gone (host-protocol.md, Guest gone).
    private async Task WatchAsync()
    {
        await Task.WhenAny(_channel!.Closed, _process.Exited).ConfigureAwait(false);
        await EndAsync().ConfigureAwait(false);
    }

    private async Task EndAsync()
    {
        RunicHostedWindow[] open;
        lock (_gate)
        {
            if (_ended) return;
            _ended = true;
            open = [.. _open];
        }
        await _ending.CancelAsync().ConfigureAwait(false);
        foreach (var window in open)
        {
            try { await window.OnGuestClosedAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { DesktopLog.HostedApplicationChannelFailed(_logger, error, error.GetType().Name); }
        }
        if (_channel is { } channel) await channel.CloseAsync(WebSocketCloseStatus.NormalClosure, "").ConfigureAwait(false);
        // A guest whose channel closed exits by itself, as after shutdown; one that does not is ended.
        if (!_process.Exited.IsCompleted)
        {
            try { _ = await _process.Exited.WaitAsync(_options.StopTimeout).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        // Also what the guest started and left behind in its job object or process group.
        _process.KillTree();
        int code;
        try { code = await _process.Exited.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { code = -1; }
        // Each closed window's Closed handler finishes the window; wait for them, briefly, before Exited.
        try { await Task.WhenAll(open.Select(static window => window.Closed)).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        if (_dataStore is { } dataStore) Runic.Desktop.Internal.EphemeralDataStores.End(dataStore);
        _owner.Remove(this);
        _process.Dispose();
        DesktopLog.HostedApplicationExited(_logger, Guest.Name, code);
        _exited.TrySetResult(code);
    }

    /// <summary>
    /// Asks each open window's close first, as the user closing it would; when every Window allowed, stops the guest as
    /// <see cref="StopAsync"/> does.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait; the guest keeps running unless its stop already began.</param>
    /// <returns>
    /// Whether the guest stops: <see langword="false"/> when a Window that guards its close refused, and the guest keeps
    /// running. <see langword="true"/> also for a guest that already exited.
    /// </returns>
    public async ValueTask<bool> RequestStopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var window in Windows)
        {
            if (!await window.RequestCloseAsync(force: false, cancellationToken).ConfigureAwait(false)) return false;
        }
        _ = await StopAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Asks the guest to shut down (<c>shutdown</c>): its Windows close without asking their guards, and the process
    /// exits. A guest that has not exited after <see cref="RunicHostedApplicationOptions.StopTimeout"/> is ended with its
    /// process tree.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait: the guest's process tree is then ended at once.</param>
    /// <returns>The guest's exit code (<see cref="Exited"/>).</returns>
    public ValueTask<int> StopAsync(CancellationToken cancellationToken = default) => StopCoreAsync("stop", cancellationToken);

    internal async ValueTask<int> StopCoreAsync(string reason, CancellationToken cancellationToken)
    {
        if (_exited.Task.IsCompleted) return await _exited.Task.ConfigureAwait(false);
        Interlocked.Exchange(ref _stopping, 1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.StopTimeout);
        try
        {
            if (_channel is { IsOpen: true } channel)
            {
                try { _ = await channel.RequestAsync(new JsonObject { ["kind"] = "shutdown", ["reason"] = reason }, timeout.Token).ConfigureAwait(false); }
                catch (HostChannelClosedException) { }
            }
            _ = await _process.Exited.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _process.KillTree();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return await _exited.Task.ConfigureAwait(false);
    }

    /// <summary>Ends the guest and the processes it started at once, without asking it.</summary>
    /// <returns>The guest's exit code (<see cref="Exited"/>).</returns>
    public async ValueTask<int> KillAsync()
    {
        _process.KillTree();
        return await _exited.Task.ConfigureAwait(false);
    }

    /// <summary>Stops the guest as <see cref="StopAsync"/> does.</summary>
    /// <returns>A task that completes when the guest exited.</returns>
    public async ValueTask DisposeAsync()
    {
        try { _ = await StopCoreAsync("stop", CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { DesktopLog.HostedApplicationChannelFailed(_logger, error, error.GetType().Name); }
    }

    internal async Task<bool> CloseRequestAsync(RunicHostedWindow window, bool force, CancellationToken cancellationToken)
    {
        if (_channel is not { IsOpen: true } channel) return true;
        var request = new JsonObject { ["kind"] = "close-request", ["window"] = window.Id };
        if (force) request["force"] = true;
        JsonObject reply;
        try { reply = await channel.RequestAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (HostChannelClosedException) { return true; }
        // A failed reply, such as for a window the guest no longer knows, keeps the window: the guest's push closes it.
        return HostProtocol.Boolean(reply, "ok") == true && HostProtocol.Boolean(reply, "allow") != false;
    }

    internal async Task PushAsync(JsonObject push)
    {
        if (_channel is not { IsOpen: true } channel) return;
        try { await channel.PushAsync(push).ConfigureAwait(false); }
        catch (HostChannelClosedException) { }
    }

    private void OnPush(JsonObject push)
    {
        if (HostProtocol.String(push, "kind") != "window-closed") return;
        RunicHostedWindow? window;
        lock (_gate) window = HostProtocol.String(push, "window") is { } id ? _windows.GetValueOrDefault(id) : null;
        // The push runs in the receive loop; the close does not hold it.
        if (window is not null) _ = window.OnGuestClosedAsync();
    }

    private async Task OnRequestAsync(HostRequest request)
    {
        if (request.Kind == "window-open")
        {
            await OpenWindowAsync(request).ConfigureAwait(false);
            return;
        }
        if (PresentationKind(request.Kind) is not { } kind)
        {
            await request.FailAsync("rejected", "invalid-request", $"'{request.Kind}' is not a request of this host.").ConfigureAwait(false);
            return;
        }
        RunicHostedWindow? window;
        lock (_gate) window = HostProtocol.String(request.Message, "window") is { } id ? _windows.GetValueOrDefault(id) : null;
        if (window is null)
        {
            await request.FailAsync("rejected", "invalid-request", "The request names no window this channel opened.").ConfigureAwait(false);
            return;
        }
        if (ReadPresentation(window, kind, request.Message) is not { } presentation)
        {
            await request.FailAsync("rejected", "invalid-request", $"The '{request.Kind}' request is missing a member or has one out of range.").ConfigureAwait(false);
            return;
        }
        // A request for a window the host closed is not an error: the guest may not have learned of the close yet.
        if (!window.IsOpen)
        {
            await request.ReplyAsync(Outcome(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed))).ConfigureAwait(false);
            return;
        }
        if (FeatureOf(kind) is { } feature && !window.Features.HasFlag(feature))
        {
            await request.ReplyAsync(Outcome(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable))).ConfigureAwait(false);
            return;
        }
        RunicHostedPresentationDecision decision;
        try { decision = Policy.Present(presentation); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            DesktopLog.HostedWindowPolicyFailed(_logger, error, error.GetType().Name);
            await request.FailAsync("failed", null, "The host's window policy failed.").ConfigureAwait(false);
            return;
        }
        if (!decision.IsAllowed)
        {
            await request.FailAsync("rejected", "refused", decision.Message ?? "The host's window policy refused the request.").ConfigureAwait(false);
            return;
        }
        // The policy's request applies as it returned it, also with another kind or another window of this guest.
        var applied = decision.Request ?? presentation;
        if (!ReferenceEquals(applied.Window.Application, this))
        {
            DesktopLog.HostedWindowPolicyFailed(_logger, null, "ForeignWindow");
            await request.FailAsync("failed", null, "The host's window policy named a window of another application.").ConfigureAwait(false);
            return;
        }
        var result = await HostedPresentation.ApplyAsync(applied.Window, applied, _ending.Token).ConfigureAwait(false);
        await request.ReplyAsync(Outcome(result)).ConfigureAwait(false);
    }

    private IRunicHostedWindowPolicy Policy => _options.Policy ?? AllowAllPolicy.Instance;

    private sealed class AllowAllPolicy : IRunicHostedWindowPolicy
    {
        internal static readonly AllowAllPolicy Instance = new();
    }

    // window-open (host-protocol.md, Windows): checks, the policy, then a native window of the host that loads the
    // guest's launch address.
    private async Task OpenWindowAsync(HostRequest request)
    {
        var message = request.Message;
        var id = HostProtocol.String(message, "window");
        var type = HostProtocol.String(message, "type") ?? "";
        var title = HostProtocol.String(message, "title");
        var width = Size(message, "width");
        var height = Size(message, "height");
        var minimumWidth = Size(message, "minWidth");
        var minimumHeight = Size(message, "minHeight");
        var ownerId = HostProtocol.String(message, "owner");
        var modality = HostProtocol.String(message, "modality") ?? "none";
        var guardClose = HostProtocol.Boolean(message, "guardClose") ?? false;
        var valid = HostProtocol.IsIdentifier(id) && type.Length <= 512 && title is null or { Length: <= 1_024 } &&
            width is not -1 && height is not -1 && minimumWidth is not -1 && minimumHeight is not -1 &&
            modality is "none" or "window" && (modality == "none" || ownerId is not null) &&
            (message["owner"] is null || ownerId is not null) && (message["guardClose"] is null || HostProtocol.Boolean(message, "guardClose") is not null) &&
            LaunchUrl(HostProtocol.String(message, "url")) is not null &&
            (message["permissions"] is null || message["permissions"] is JsonArray);
        var url = LaunchUrl(HostProtocol.String(message, "url"));
        RunicHostedWindow? owner = null;
        lock (_gate)
        {
            if (valid && !_usedIds.Add(id!)) valid = false;
            if (valid && ownerId is not null && (!_windows.TryGetValue(ownerId, out owner) || !owner.IsOpen)) valid = false;
        }
        if (!valid)
        {
            await request.FailAsync("rejected", "invalid-request", "The window-open is not valid: a member is missing, out of range or names no open window.").ConfigureAwait(false);
            return;
        }
        if (_ending.IsCancellationRequested || Volatile.Read(ref _stopping) != 0 || _owner.IsClosing)
        {
            await request.FailAsync("rejected", "refused", "The host is stopping the application.").ConfigureAwait(false);
            return;
        }
        var permissions = message["permissions"] is JsonArray list &&
            list.Any(static item => item is JsonValue value && value.TryGetValue<string>(out var name) && name == "media-capture")
            ? DesktopPermissionGrant.MediaCapture
            : DesktopPermissionGrant.None;
        var defaults = _host.WindowOptions;
        // The guest gets what it asks for only as far as the host grants its own windows; a policy can grant more.
        var proposed = defaults with
        {
            Width = width ?? defaults.Width,
            Height = height ?? defaults.Height,
            MinimumWidth = minimumWidth ?? defaults.MinimumWidth,
            MinimumHeight = minimumHeight ?? defaults.MinimumHeight,
            Owner = owner?.PresentationOrNull is { IsOpen: true } ownerWindow ? DesktopWindowOwner.FromWindow(ownerWindow) : null,
            Modality = owner is not null && modality == "window" ? DesktopWindowModality.Window : DesktopWindowModality.None,
            AllowedPermissions = permissions & defaults.AllowedPermissions,
        };
        var window = new RunicHostedWindow(this, id!, type, owner, modality == "window" && owner is not null, guardClose);
        var windowRequest = new RunicHostedWindowRequest(this, id!, type, title, owner, window.IsModal, guardClose, permissions, proposed);
        RunicHostedWindowDecision decision;
        try { decision = await Policy.OpenWindowAsync(windowRequest, _ending.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            DesktopLog.HostedWindowPolicyFailed(_logger, error, error.GetType().Name);
            await request.FailAsync("failed", null, "The host's window policy failed.").ConfigureAwait(false);
            return;
        }
        if (!decision.IsAllowed)
        {
            await request.FailAsync("rejected", "refused", decision.Message ?? "The host's window policy refused the window.").ConfigureAwait(false);
            return;
        }
        // Whatever the policy chose: an embedded window without fallbacks, whose close the guest decides.
        var options = (decision.WindowOptions ?? proposed) with
        {
            Browser = BrowserKind.Embedded,
            PresentationPolicy = DesktopPresentationPolicy.RequestedOnly,
            Fallbacks = [],
            ConfirmCloseAsync = window.ConfirmCloseAsync,
        };
        DesktopSurface? surface = null;
        DesktopWindow presentation;
        try
        {
            surface = await _host.Desktop.CreateHostedSurfaceAsync(url!, _dataStore!, _ending.Token).ConfigureAwait(false);
            presentation = await surface.OpenWindowAsync(options, _ending.Token).ConfigureAwait(false);
            if (!presentation.IsOpen) throw new InvalidOperationException("The window was closed while it opened.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (surface is not null) await surface.DisposeAsync().ConfigureAwait(false);
            DesktopLog.HostedWindowFailed(_logger, error, Guest.Name, error.GetType().Name);
            await request.FailAsync("failed", null, error is DesktopException desktop
                ? $"The host could not open the window ({desktop.Code})."
                : "The host could not open the window.").ConfigureAwait(false);
            return;
        }
        var adapter = new DesktopWindowPresentation(presentation, () => new DesktopNativeOwner(presentation));
        window.Attach(presentation, adapter);
        bool ended;
        lock (_gate)
        {
            // The guest went away while the window opened: EndAsync closed the others and does not know this one.
            ended = _ended;
            if (!ended)
            {
                _windows[window.Id] = window;
                _open.Add(window);
            }
        }
        var hostedSurface = surface!;
        if (ended)
        {
            await CloseUnlistedAsync(window, hostedSurface).ConfigureAwait(false);
            await AnswerAsync(request.FailAsync("failed", null, "The application ended while its window opened.")).ConfigureAwait(false);
            return;
        }
        // WindowOpened before the reply, and before the window's Closed handler can raise WindowClosed: the guest can
        // close the window as soon as the reply arrives, and the events are raised in order.
        _owner.RaiseWindowOpened(window);
        // The host's pushes for the window (host-protocol.md, Activity and placement).
        adapter.ActivityChanged += (_, e) =>
        {
            if (window.Features.HasFlag(RunicWindowPresentationFeatures.Activity))
                window.Push("activity", () => Activity(window.Id, e.Activity));
        };
        adapter.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IRunicWindowPresentation.ColorScheme))
                window.Push("color-scheme", () => new JsonObject { ["kind"] = "color-scheme", ["window"] = window.Id, ["colorScheme"] = Token(adapter.ColorScheme) });
        };
        presentation.StateChanged += (_, e) => window.Push("placement", () => Placement(window.Id, e.State));
        presentation.Closed += (_, _) => _ = OnWindowClosedAsync(window, hostedSurface);
        try
        {
            await request.ReplyAsync(new JsonObject
            {
                ["features"] = new JsonArray([.. FeatureNames(window.Features).Select(static name => (JsonNode)name)]),
                ["activity"] = ActivityMembers(adapter.Activity),
                ["colorScheme"] = Token(adapter.ColorScheme),
            }).ConfigureAwait(false);
        }
        catch (HostChannelClosedException)
        {
            // The guest never learns of the window: it closes as for a guest that is gone.
            await window.OnGuestClosedAsync().ConfigureAwait(false);
        }
        if (!presentation.IsOpen) _ = OnWindowClosedAsync(window, hostedSurface);
    }

    // A window that opened after its guest went away: closed and released without events, since it never opened for the
    // host application.
    private async Task CloseUnlistedAsync(RunicHostedWindow window, DesktopSurface surface)
    {
        try { await window.OnGuestClosedAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { DesktopLog.HostedWindowFailed(_logger, error, Guest.Name, error.GetType().Name); }
        try { await surface.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { DesktopLog.HostedWindowFailed(_logger, error, Guest.Name, error.GetType().Name); }
        window.MarkClosed();
    }

    private static async Task AnswerAsync(Task answer)
    {
        try { await answer.ConfigureAwait(false); }
        catch (HostChannelClosedException) { }
    }

    // The native window closed, for any reason. A window the guest did not close, such as one the host's shutdown
    // closed, closes the guest's Window too.
    private async Task OnWindowClosedAsync(RunicHostedWindow window, DesktopSurface surface)
    {
        lock (_gate)
        {
            if (!_open.Remove(window)) return;
        }
        if (!window.IsClosedByGuest)
        {
            try { _ = await CloseRequestAsync(window, force: true, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { DesktopLog.HostedApplicationChannelFailed(_logger, error, error.GetType().Name); }
        }
        try { await surface.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { DesktopLog.HostedWindowFailed(_logger, error, Guest.Name, error.GetType().Name); }
        window.MarkClosed();
        _owner.RaiseWindowClosed(window);
    }

    // A size member: absent (null), an integer from 1 to 32,767, or -1 for one that is not valid.
    private static int? Size(JsonObject message, string name)
    {
        if (message[name] is null) return null;
        return HostProtocol.Integer(message, name) is { } value && value is >= 1 and <= 32_767 ? (int)value : -1;
    }

    // http on a loopback literal and an explicit port, with a path that ends in /_runic_launch/<code> (host-protocol.md,
    // window-open).
    internal static Uri? LaunchUrl(string? text)
    {
        if (text is not { Length: > 0 and <= 2_048 } || !Uri.TryCreate(text, UriKind.Absolute, out var url)) return null;
        if (url.Scheme != Uri.UriSchemeHttp || url.IsDefaultPort || url.Host is not ("127.0.0.1" or "[::1]") ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            return null;
        var path = url.AbsolutePath;
        var marker = path.LastIndexOf("/_runic_launch/", StringComparison.Ordinal);
        if (marker < 0) return null;
        var code = path[(marker + "/_runic_launch/".Length)..];
        return code.Length > 0 && !code.Contains('/') ? url : null;
    }

    private static RunicHostedPresentationKind? PresentationKind(string kind) => kind switch
    {
        "present" => RunicHostedPresentationKind.Present,
        "minimize" => RunicHostedPresentationKind.Minimize,
        "set-visible" => RunicHostedPresentationKind.SetVisible,
        "set-full-screen" => RunicHostedPresentationKind.SetFullScreen,
        "toggle-maximized" => RunicHostedPresentationKind.ToggleMaximized,
        "resize" => RunicHostedPresentationKind.Resize,
        "move" => RunicHostedPresentationKind.Move,
        "set-document-edited" => RunicHostedPresentationKind.SetDocumentEdited,
        "set-represented-file" => RunicHostedPresentationKind.SetRepresentedFile,
        "set-color-scheme" => RunicHostedPresentationKind.SetColorScheme,
        "set-layout-direction" => RunicHostedPresentationKind.SetLayoutDirection,
        "announce" => RunicHostedPresentationKind.Announce,
        _ => null,
    };

    private static RunicWindowPresentationFeatures? FeatureOf(RunicHostedPresentationKind kind) => kind switch
    {
        RunicHostedPresentationKind.Present => RunicWindowPresentationFeatures.Present,
        RunicHostedPresentationKind.Minimize => RunicWindowPresentationFeatures.Minimize,
        RunicHostedPresentationKind.SetVisible => RunicWindowPresentationFeatures.Visibility,
        RunicHostedPresentationKind.SetFullScreen => RunicWindowPresentationFeatures.FullScreen,
        RunicHostedPresentationKind.SetDocumentEdited => RunicWindowPresentationFeatures.DocumentEdited,
        RunicHostedPresentationKind.SetColorScheme => RunicWindowPresentationFeatures.ColorScheme,
        _ => null,
    };

    // The request's members (host-protocol.md, Presentation), or null when one is missing or out of range.
    private static RunicHostedPresentationRequest? ReadPresentation(RunicHostedWindow window, RunicHostedPresentationKind kind, JsonObject message)
    {
        var request = new RunicHostedPresentationRequest(window, kind);
        switch (kind)
        {
            case RunicHostedPresentationKind.SetVisible:
                return HostProtocol.Boolean(message, "visible") is { } visible ? request with { Value = visible } : null;
            case RunicHostedPresentationKind.SetFullScreen:
                return HostProtocol.Boolean(message, "fullScreen") is { } fullScreen ? request with { Value = fullScreen } : null;
            case RunicHostedPresentationKind.SetDocumentEdited:
                return HostProtocol.Boolean(message, "edited") is { } edited ? request with { Value = edited } : null;
            case RunicHostedPresentationKind.Resize:
                return HostProtocol.Integer(message, "width") is { } w and >= 1 and <= 32_767 &&
                    HostProtocol.Integer(message, "height") is { } h and >= 1 and <= 32_767
                    ? request with { Width = (int)w, Height = (int)h }
                    : null;
            case RunicHostedPresentationKind.Move:
                return HostProtocol.Integer(message, "x") is { } x and >= -32_768 and <= 32_767 &&
                    HostProtocol.Integer(message, "y") is { } y and >= -32_768 and <= 32_767
                    ? request with { X = (int)x, Y = (int)y }
                    : null;
            case RunicHostedPresentationKind.SetRepresentedFile:
                if (message["path"] is null) return message.ContainsKey("path") ? request : null;
                return HostProtocol.String(message, "path") is { Length: > 0 } path && Path.IsPathFullyQualified(path)
                    ? request with { Path = path }
                    : null;
            case RunicHostedPresentationKind.SetColorScheme:
                return HostedWindowPresentation.ColorSchemeOf(HostProtocol.String(message, "colorScheme")) is { } scheme
                    ? request with { ColorScheme = scheme }
                    : null;
            case RunicHostedPresentationKind.SetLayoutDirection:
                DesktopLayoutDirection? direction = HostProtocol.String(message, "direction") switch
                {
                    "default" => DesktopLayoutDirection.Default,
                    "left-to-right" => DesktopLayoutDirection.LeftToRight,
                    "right-to-left" => DesktopLayoutDirection.RightToLeft,
                    _ => null,
                };
                return direction is null ? null : request with { LayoutDirection = direction };
            case RunicHostedPresentationKind.Announce:
                DesktopAnnouncementPriority? priority = HostProtocol.String(message, "priority") switch
                {
                    "polite" => DesktopAnnouncementPriority.Polite,
                    "assertive" => DesktopAnnouncementPriority.Assertive,
                    _ => null,
                };
                return HostProtocol.Text(message, "text", 1_024) is { } text && priority is not null
                    ? request with { Text = text, Priority = priority }
                    : null;
            default:
                return request;
        }
    }

    // A platform outcome reply (host-protocol.md, Platform outcomes).
    internal static JsonObject Outcome(PlatformResult<PlatformUnit> result) => result switch
    {
        PlatformResult<PlatformUnit>.Unavailable unavailable =>
            new JsonObject { ["outcome"] = "unavailable", ["reason"] = HostProtocol.Kebab(unavailable.Reason.ToString()) },
        PlatformResult<PlatformUnit>.Failed failed => failed.Diagnostic?.Message is { } detail
            ? new JsonObject { ["outcome"] = "failed", ["code"] = HostProtocol.Kebab(failed.Code.ToString()), ["message"] = detail.Length <= 1_024 ? detail : detail[..1_024] }
            : new JsonObject { ["outcome"] = "failed", ["code"] = HostProtocol.Kebab(failed.Code.ToString()) },
        _ => new JsonObject { ["outcome"] = "success" },
    };

    internal static IEnumerable<string> FeatureNames(RunicWindowPresentationFeatures features)
    {
        if (features.HasFlag(RunicWindowPresentationFeatures.Present)) yield return "present";
        if (features.HasFlag(RunicWindowPresentationFeatures.Minimize)) yield return "minimize";
        if (features.HasFlag(RunicWindowPresentationFeatures.Visibility)) yield return "visibility";
        if (features.HasFlag(RunicWindowPresentationFeatures.FullScreen)) yield return "full-screen";
        if (features.HasFlag(RunicWindowPresentationFeatures.DocumentEdited)) yield return "document-edited";
        if (features.HasFlag(RunicWindowPresentationFeatures.ColorScheme)) yield return "color-scheme";
        if (features.HasFlag(RunicWindowPresentationFeatures.PerWindowColorScheme)) yield return "per-window-color-scheme";
        if (features.HasFlag(RunicWindowPresentationFeatures.Activity)) yield return "activity";
        if (features.HasFlag(RunicWindowPresentationFeatures.OwnedWindows)) yield return "owned-windows";
    }

    private static JsonObject ActivityMembers(RunicWindowActivity activity) => new()
    {
        ["active"] = activity.IsActive,
        ["minimized"] = activity.IsMinimized,
        ["visible"] = activity.IsVisible,
    };

    private static JsonObject Activity(string window, RunicWindowActivity activity)
    {
        var push = ActivityMembers(activity);
        push.Insert(0, "window", window);
        push.Insert(0, "kind", "activity");
        return push;
    }

    private static JsonObject Placement(string window, DesktopWindowState state) => new()
    {
        ["kind"] = "placement",
        ["window"] = window,
        ["bounds"] = new JsonObject { ["x"] = state.X ?? 0, ["y"] = state.Y ?? 0, ["width"] = state.Width, ["height"] = state.Height },
        ["maximized"] = state.IsMaximized,
        ["fullScreen"] = state.IsFullScreen,
        ["monitor"] = state.MonitorId,
    };

    private static string Token(DesktopColorScheme scheme) => scheme switch
    {
        DesktopColorScheme.Light => "light",
        DesktopColorScheme.Dark => "dark",
        _ => "no-preference",
    };

    // The guest's standard output: its first launch line decides the start and is not logged, since it carries the
    // channel key; the other lines are its console output.
    private async Task ReadOutputAsync()
    {
        try
        {
            using var reader = new StreamReader(_process.StandardOutput, new UTF8Encoding(false));
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!_launchLine.Task.IsCompleted && HostLaunch.IsLaunchLine(line))
                {
                    _launchLine.TrySetResult(line);
                    continue;
                }
                DesktopLog.HostedGuestOutput(_logger, _fileName, line);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { _launchLine.TrySetResult(null); }
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            using var reader = new StreamReader(_process.StandardError, new UTF8Encoding(false));
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_errorTail)
                {
                    _errorTail.Enqueue(line);
                    if (_errorTail.Count > MaximumKeptErrorLines) _errorTail.Dequeue();
                }
                DesktopLog.HostedGuestError(_logger, _fileName, line);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private static string DefaultHostName =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name is { Length: > 0 and <= 128 } name ? name : "Runic application";

    private static string DefaultHostVersion
    {
        get
        {
            var entry = System.Reflection.Assembly.GetEntryAssembly();
            var version = (entry is null ? null : System.Reflection.CustomAttributeExtensions
                    .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(entry)?.InformationalVersion)
                ?? entry?.GetName().Version?.ToString() ?? "0.0.0";
            var metadata = version.IndexOf('+', StringComparison.Ordinal);
            version = metadata < 0 ? version : version[..metadata];
            return version is { Length: > 0 and <= 64 } ? version : "0.0.0";
        }
    }
}
