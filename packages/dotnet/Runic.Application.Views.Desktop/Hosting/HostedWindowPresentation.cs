using System.ComponentModel;
using System.Text.Json.Nodes;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop.Hosting;

// IRunicWindowPresentation of a hosted Window (host-protocol.md, Presentation): each request goes to the host's window
// over the channel and reports the host's platform outcome; activity and colour scheme follow the host's pushes.
internal sealed class HostedWindowPresentation : IRunicWindowPresentation
{
    private static readonly PlatformResult<PlatformUnit> Done = new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    private static readonly PlatformResult<PlatformUnit> Closed = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
    private static readonly PlatformResult<PlatformUnit> Unsupported = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    private const string Source = "Runic host protocol";

    private readonly Func<JsonObject, CancellationToken, Task<JsonObject?>> _request;
    private readonly object _gate = new();
    private readonly RunicWindowPresentationFeatures _features;
    private RunicWindowActivity _activity;
    private DesktopColorScheme _colorScheme;
    private volatile bool _closed;

    // request sends a presentation request for this window and returns the reply, or null when the channel is gone.
    internal HostedWindowPresentation(RunicWindowPresentationFeatures features, RunicWindowActivity activity,
        DesktopColorScheme colorScheme, Func<JsonObject, CancellationToken, Task<JsonObject?>> request)
    {
        _features = features;
        _activity = activity;
        _colorScheme = colorScheme;
        _request = request;
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
        ActivityChanged?.Invoke(this, new RunicWindowActivityChangedEventArgs(previous, activity));
    }

    // The color-scheme push.
    internal void OnColorScheme(DesktopColorScheme scheme)
    {
        lock (_gate)
        {
            if (_closed || _colorScheme == scheme) return;
            _colorScheme = scheme;
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColorScheme)));
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
            ActivityChanged?.Invoke(this, new RunicWindowActivityChangedEventArgs(previous, RunicWindowActivity.Closed));
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
