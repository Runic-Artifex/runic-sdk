using System.ComponentModel;
using System.Text.Json.Nodes;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop.Hosting;

// IRunicWindowPresentation of a hosted Window (host-protocol.md, Presentation): each request goes to the host's window
// over the channel and reports the host's platform outcome; activity and colour scheme follow the host's pushes. The
// state changes when a push arrives, and the events are raised on a queue of the window's own, in order, so an
// application handler that blocks, such as one that waits for another request's reply, never stops the channel.
internal sealed class HostedWindowPresentation : IRunicWindowPresentation
{
    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    private static readonly PlatformResult<PlatformUnit> Closed = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
    private static readonly PlatformResult<PlatformUnit> Unsupported = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    private const string Source = "Runic host protocol";

    private readonly Func<JsonObject, CancellationToken, Task<JsonObject?>> _request;
    private readonly Action<Exception>? _onHandlerError;
    private readonly object _gate = new();
    private readonly object _eventGate = new();
    private Task _events = Task.CompletedTask;
    private readonly RunicWindowPresentationFeatures _features;
    private RunicWindowActivity _activity;
    private DesktopColorScheme _colorScheme;
    private RunicWindowPlacement? _placement;
    private volatile bool _closed;

    // request sends a presentation request for this window and returns the reply, or null when the channel is gone;
    // onHandlerError receives what an event handler threw.
    internal HostedWindowPresentation(RunicWindowPresentationFeatures features, RunicWindowActivity activity,
        DesktopColorScheme colorScheme, Func<JsonObject, CancellationToken, Task<JsonObject?>> request,
        Action<Exception>? onHandlerError = null, RunicWindowPlacement? placement = null)
    {
        _features = features;
        _activity = activity;
        // Only a window whose host lists placement reports one (host-protocol.md, window-open).
        _placement = features.HasFlag(RunicWindowPresentationFeatures.Placement) ? placement : null;
        _colorScheme = colorScheme;
        _request = request;
        _onHandlerError = onHandlerError;
    }

    // Raises an event after the ones raised before, off the caller's thread.
    private void Raise(Action raise)
    {
        lock (_eventGate)
        {
            _events = _events.ContinueWith(_ =>
            {
                try { raise(); }
                catch (Exception error) when (error is not OutOfMemoryException) { _onHandlerError?.Invoke(error); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    // Completes when the events raised so far have run (tests).
    internal Task EventsRaised
    {
        get { lock (_eventGate) return _events; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<RunicWindowActivityChangedEventArgs>? ActivityChanged;

    public RunicWindowPresentationFeatures Features => _closed ? RunicWindowPresentationFeatures.None : _features;

    public RunicWindowActivity Activity => Volatile.Read(ref _activity);

    public bool IsActive => Activity.IsActive;

    public bool IsMinimized => Activity.IsMinimized;

    public bool IsVisible => Activity.IsVisible;

    public DesktopColorScheme ColorScheme
    {
        get { lock (_gate) return _colorScheme; }
    }

    // The window-open reply's features (host-protocol.md, window-open); unknown ones are ignored.
    internal static RunicWindowPresentationFeatures FeaturesOf(JsonArray? names)
    {
        var features = RunicWindowPresentationFeatures.None;
        foreach (var item in names ?? [])
        {
            features |= (item as JsonValue)?.TryGetValue<string>(out var name) == true ? name switch
            {
                "present" => RunicWindowPresentationFeatures.Present,
                "minimize" => RunicWindowPresentationFeatures.Minimize,
                "visibility" => RunicWindowPresentationFeatures.Visibility,
                "full-screen" => RunicWindowPresentationFeatures.FullScreen,
                "document-edited" => RunicWindowPresentationFeatures.DocumentEdited,
                "color-scheme" => RunicWindowPresentationFeatures.ColorScheme,
                "per-window-color-scheme" => RunicWindowPresentationFeatures.PerWindowColorScheme,
                "activity" => RunicWindowPresentationFeatures.Activity,
                "owned-windows" => RunicWindowPresentationFeatures.OwnedWindows,
                "maximize" => RunicWindowPresentationFeatures.Maximize,
                "placement" => RunicWindowPresentationFeatures.Placement,
                _ => RunicWindowPresentationFeatures.None,
            } : RunicWindowPresentationFeatures.None;
        }
        return features;
    }

    // {active, minimized, visible}, or null when a member is missing.
    internal static RunicWindowActivity? ActivityOf(JsonObject? message) =>
        message is not null && HostProtocol.Boolean(message, "active") is { } active &&
        HostProtocol.Boolean(message, "minimized") is { } minimized && HostProtocol.Boolean(message, "visible") is { } visible
            ? new RunicWindowActivity(active, minimized, visible)
            : null;

    public RunicWindowPlacement? Placement => Volatile.Read(ref _placement);

    // The placement of a placement push or of the window-open reply: restored when the host sends it, else the current
    // frame of a host that does not (host-protocol.md, Activity and placement); null when a member is missing.
    internal static RunicWindowPlacement? PlacementOf(JsonObject? message)
    {
        if (message is null || HostProtocol.Boolean(message, "maximized") is not { } maximized ||
            HostProtocol.Boolean(message, "fullScreen") is not { } fullScreen)
            return null;
        var bounds = message["restored"] as JsonObject ?? message["bounds"] as JsonObject;
        return bounds is not null && Dimension(bounds, "width") is { } width && Dimension(bounds, "height") is { } height &&
            Position(bounds, out var x, out var y)
            ? new RunicWindowPlacement { X = x, Y = y, Width = width, Height = height, Maximized = maximized, FullScreen = fullScreen }
            : null;

        static int? Dimension(JsonObject members, string name) =>
            HostProtocol.Integer(members, name) is { } value and >= 0 and <= int.MaxValue ? (int)value : null;

        // x and y: both integers, or both absent or null where the host has no position (Wayland).
        static bool Position(JsonObject members, out int? x, out int? y)
        {
            x = y = null;
            var hasX = members["x"] is not null;
            if (hasX != members["y"] is not null) return false;
            if (!hasX) return true;
            if (HostProtocol.Integer(members, "x") is not (long px and >= int.MinValue and <= int.MaxValue) ||
                HostProtocol.Integer(members, "y") is not (long py and >= int.MinValue and <= int.MaxValue))
                return false;
            (x, y) = ((int)px, (int)py);
            return true;
        }
    }

    // The placement push.
    internal void OnPlacement(RunicWindowPlacement placement)
    {
        lock (_gate)
        {
            if (_closed || !_features.HasFlag(RunicWindowPresentationFeatures.Placement) || Equals(_placement, placement)) return;
            Volatile.Write(ref _placement, placement);
        }
        Raise(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Placement))));
    }

    internal static DesktopColorScheme? ColorSchemeOf(string? token) => token switch
    {
        "light" => DesktopColorScheme.Light,
        "dark" => DesktopColorScheme.Dark,
        "no-preference" => DesktopColorScheme.NoPreference,
        _ => null,
    };

    private static string Token(DesktopColorScheme scheme) => scheme switch
    {
        DesktopColorScheme.Light => "light",
        DesktopColorScheme.Dark => "dark",
        _ => "no-preference",
    };

    // The activity push.
    internal void OnActivity(RunicWindowActivity activity)
    {
        RunicWindowActivity previous;
        lock (_gate)
        {
            if (_closed) return;
            previous = _activity;
            if (Equals(previous, activity)) return;
            Volatile.Write(ref _activity, activity);
        }
        Raise(() => ActivityChanged?.Invoke(this, new RunicWindowActivityChangedEventArgs(previous, activity)));
    }

    // The color-scheme push.
    internal void OnColorScheme(DesktopColorScheme scheme)
    {
        lock (_gate)
        {
            if (_closed || _colorScheme == scheme) return;
            _colorScheme = scheme;
        }
        Raise(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorScheme))));
    }

    internal void MarkClosed()
    {
        RunicWindowActivity previous;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            previous = _activity;
            Volatile.Write(ref _activity, RunicWindowActivity.Closed);
        }
        if (!Equals(previous, RunicWindowActivity.Closed))
            Raise(() => ActivityChanged?.Invoke(this, new RunicWindowActivityChangedEventArgs(previous, RunicWindowActivity.Closed)));
    }

    // The host owns the activation token (Wayland): the guest's own context does not apply to the host's window.
    public ValueTask<PlatformResult<PlatformUnit>> PresentAsync(DesktopActivationContext? activation = null, CancellationToken cancellationToken = default) =>
        RequestAsync(RunicWindowPresentationFeatures.Present, "present", null, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> MinimizeAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(RunicWindowPresentationFeatures.Minimize, "minimize", null, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) =>
        RequestAsync(RunicWindowPresentationFeatures.Visibility, "set-visible", new() { ["visible"] = visible }, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default) =>
        RequestAsync(RunicWindowPresentationFeatures.FullScreen, "set-full-screen", new() { ["fullScreen"] = fullScreen }, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetDocumentEditedAsync(bool edited, CancellationToken cancellationToken = default) =>
        RequestAsync(RunicWindowPresentationFeatures.DocumentEdited, "set-document-edited", new() { ["edited"] = edited }, cancellationToken);

    public async ValueTask<PlatformResult<PlatformUnit>> SetColorSchemeAsync(DesktopColorScheme colorScheme, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(colorScheme)) throw new ArgumentOutOfRangeException(nameof(colorScheme));
        var result = await RequestAsync(RunicWindowPresentationFeatures.ColorScheme, "set-color-scheme",
            new() { ["colorScheme"] = Token(colorScheme) }, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded) OnColorScheme(colorScheme);
        return result;
    }

    // sdk#897, W1b (host-protocol.md, Presentation): the host's policy decides, and may clamp a placement.
    public ValueTask<PlatformResult<PlatformUnit>> MaximizeAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(RunicWindowPresentationFeatures.Maximize, "maximize", null, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> RestoreAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(RunicWindowPresentationFeatures.Maximize, "restore", null, cancellationToken);

    public ValueTask<PlatformResult<PlatformUnit>> SetPlacementAsync(RunicWindowPlacement placement, CancellationToken cancellationToken = default)
    {
        RunicWindowPlacement.Validate(placement);
        var members = new JsonObject();
        // A position the protocol cannot carry is kept within its range; the host clamps the bounds to its monitors.
        if (placement is { X: { } x, Y: { } y })
        {
            members["x"] = Math.Clamp(x, -32_768, 32_767);
            members["y"] = Math.Clamp(y, -32_768, 32_767);
        }
        members["width"] = Math.Clamp(placement.Width, 0, 32_767);
        members["height"] = Math.Clamp(placement.Height, 0, 32_767);
        members["maximized"] = placement.Maximized;
        members["fullScreen"] = placement.FullScreen;
        return RequestAsync(RunicWindowPresentationFeatures.Placement, "set-placement", members, cancellationToken);
    }

    // announce: the host's window asks the screen reader to speak the text (at most 1,024 characters are sent).
    internal ValueTask<PlatformResult<PlatformUnit>> AnnounceAsync(string text, DesktopAnnouncementPriority priority, CancellationToken cancellationToken = default) =>
        RequestAsync(RunicWindowPresentationFeatures.None, "announce", new()
        {
            ["text"] = HostedWire.Shorten(text, 1_024),
            ["priority"] = priority == DesktopAnnouncementPriority.Assertive ? "assertive" : "polite",
        }, cancellationToken);

    // set-layout-direction: the native direction of the host's window, which follows the Window's locale.
    internal ValueTask<PlatformResult<PlatformUnit>> SetLayoutDirectionAsync(DesktopLayoutDirection direction, CancellationToken cancellationToken = default) =>
        RequestAsync(RunicWindowPresentationFeatures.None, "set-layout-direction", new()
        {
            ["direction"] = direction switch
            {
                DesktopLayoutDirection.LeftToRight => "left-to-right",
                DesktopLayoutDirection.RightToLeft => "right-to-left",
                _ => "default",
            },
        }, cancellationToken);

    private async ValueTask<PlatformResult<PlatformUnit>> RequestAsync(RunicWindowPresentationFeatures feature, string kind,
        JsonObject? members, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed) return Closed;
        // A request whose feature the host did not list is not sent (host-protocol.md, Presentation).
        if (!_features.HasFlag(feature)) return Unsupported;
        var request = new JsonObject { ["id"] = 0, ["kind"] = kind };
        foreach (var (name, value) in members?.ToArray() ?? [])
        {
            members!.Remove(name);
            request[name] = value;
        }
        var reply = await _request(request, cancellationToken).ConfigureAwait(false);
        return reply is null ? Closed : OutcomeOf(reply);
    }

    // A reply as a platform result (host-protocol.md, Platform outcomes).
    internal static PlatformResult<PlatformUnit> OutcomeOf(JsonObject reply)
    {
        if (HostProtocol.Boolean(reply, "ok") != true)
        {
            var reason = reply["error"] is JsonObject error ? HostProtocol.String(error, "reason") : null;
            var code = reason switch
            {
                "refused" => PlatformFailureCode.PermissionDenied,
                "busy" or "capacity" => PlatformFailureCode.ResourceBusy,
                _ => PlatformFailureCode.Unknown,
            };
            var message = reply["error"] is JsonObject failed ? HostProtocol.String(failed, "message") : null;
            return new PlatformResult<PlatformUnit>.Failed(code, message is null ? null : new PlatformDiagnostic(Source, 0, message));
        }
        switch (HostProtocol.String(reply, "outcome"))
        {
            case "success":
                return Done;
            case "unavailable":
                return new PlatformResult<PlatformUnit>.Unavailable(HostProtocol.String(reply, "reason") switch
                {
                    "provider-not-configured" => PlatformUnavailableReason.ProviderNotConfigured,
                    "owner-unavailable" => PlatformUnavailableReason.OwnerUnavailable,
                    "owner-closed" => PlatformUnavailableReason.OwnerClosed,
                    // "embedded": a region window, where the request does not apply (host-protocol.md, Presentation in a
                    // region), reads as backend-unavailable, as for any reason this guest does not know.
                    _ => PlatformUnavailableReason.BackendUnavailable,
                });
            default:
                var code = HostProtocol.String(reply, "code") is { } token &&
                    Enum.GetValues<PlatformFailureCode>().FirstOrDefault(value => HostProtocol.Kebab(value.ToString()) == token) is var known &&
                    HostProtocol.Kebab(known.ToString()) == token
                    ? known
                    : PlatformFailureCode.Unknown;
                var message = HostProtocol.String(reply, "message");
                return new PlatformResult<PlatformUnit>.Failed(code, message is null ? null : new PlatformDiagnostic(Source, 0, message));
        }
    }
}
