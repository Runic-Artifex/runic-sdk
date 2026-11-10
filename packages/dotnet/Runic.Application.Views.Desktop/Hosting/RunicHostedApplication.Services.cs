using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

// The host's side of the appearance and locale pushes (host-protocol.md, Appearance and locale) and of window-services/1
// (host-protocol.md, Window services): the guest follows this application's appearance and locale; its clipboard,
// monitors and launcher requests run with this application's window services, under the policy and the limits; the
// files the user drops onto a guest's window reach the guest.
public sealed partial class RunicHostedApplication
{
    private IDisposable? _appearanceFollow;
    private IRunicApplicationLocale? _hostLocale;
    private readonly object _environmentGate = new();
    private DesktopAppearance? _sentAppearance;
    private bool _pushingEnvironment;
    private bool _appearanceDue;
    private bool _localeDue;
    // Set once the channel is open: changes before are recorded and pushed then.
    private bool _environmentReady;
    private readonly TaskCompletionSource _firstAppearance = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // How long the start waits for the first read of this application's appearance, when none of its windows has read it
    // yet; a desktop that reports none starts the guest with an unknown appearance.
    private static readonly TimeSpan FirstAppearanceTimeout = TimeSpan.FromMilliseconds(500);

    // hello's locale, languages and appearance: this application's locale, its user's languages and the appearance its
    // own windows show. From now on, each change is recorded, and pushed once the channel is open.
    private async Task<JsonObject> HelloEnvironmentAsync(JsonArray features, CancellationToken cancellationToken)
    {
        _hostLocale = _host.Services.GetService(typeof(IRunicApplicationLocale)) as IRunicApplicationLocale;
        if (_hostLocale is not null) _hostLocale.LocaleChanged += OnHostLocaleChanged;
        _appearanceFollow = _host.AppearanceFeed.Add(this, OnHostAppearance, _logger);
        try { await _firstAppearance.Task.WaitAsync(FirstAppearanceTimeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { }
        var appearance = _host.AppearanceFeed.Current;
        lock (_environmentGate)
        {
            _sentAppearance = appearance;
            _appearanceDue = false;
            _localeDue = false;
        }
        return new JsonObject
        {
            ["features"] = features,
            ["locale"] = Locale(),
            ["languages"] = HostServices.WriteLanguages(DesktopLanguages.Preferred),
            ["appearance"] = appearance is null ? null : HostServices.WriteAppearance(appearance),
        };
    }

    private string? Locale() => _hostLocale?.Locale is { } locale && HostServices.IsLocale(locale) ? locale : null;

    // After the handshake: each change of this application's appearance or locale is pushed to the guest, also one made
    // during the handshake.
    private void FollowEnvironment()
    {
        bool due;
        lock (_environmentGate)
        {
            _environmentReady = true;
            due = _appearanceDue || _localeDue;
        }
        if (due) PushEnvironment();
    }

    private void StopFollowingEnvironment()
    {
        Interlocked.Exchange(ref _appearanceFollow, null)?.Dispose();
        if (_hostLocale is not null) _hostLocale.LocaleChanged -= OnHostLocaleChanged;
    }

    private void OnHostAppearance(DesktopAppearance appearance)
    {
        _firstAppearance.TrySetResult();
        lock (_environmentGate)
        {
            if (Equals(_sentAppearance, appearance)) return;
            _appearanceDue = true;
            if (!_environmentReady) return;
        }
        PushEnvironment();
    }

    private void OnHostLocaleChanged(object? sender, RunicApplicationLocaleChangedEventArgs e)
    {
        lock (_environmentGate)
        {
            _localeDue = true;
            if (!_environmentReady) return;
        }
        PushEnvironment();
    }

    // One push at a time, each with the latest value, so a guest that reads slowly receives the latest appearance and
    // locale instead of every change.
    private void PushEnvironment()
    {
        lock (_environmentGate)
        {
            if (_pushingEnvironment) return;
            _pushingEnvironment = true;
        }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                JsonObject push;
                lock (_environmentGate)
                {
                    if (_appearanceDue)
                    {
                        _appearanceDue = false;
                        var appearance = _host.AppearanceFeed.Current;
                        if (Equals(_sentAppearance, appearance)) continue;
                        _sentAppearance = appearance;
                        push = HostServices.WriteAppearance(appearance);
                        push.Insert(0, "kind", "appearance");
                    }
                    else if (_localeDue)
                    {
                        _localeDue = false;
                        push = new JsonObject
                        {
                            ["kind"] = "locale",
                            ["locale"] = Locale(),
                            ["languages"] = HostServices.WriteLanguages(DesktopLanguages.Preferred),
                        };
                    }
                    else
                    {
                        _pushingEnvironment = false;
                        return;
                    }
                }
                try { await PushAsync(push).ConfigureAwait(false); }
                catch (Exception error) when (error is not OutOfMemoryException) { DesktopLog.HostedApplicationChannelFailed(_logger, error, error.GetType().Name); }
            }
        });
    }

    // ----- window-services/1

    // The guest's clipboard and monitor requests across its windows, and its launches (RunicHostedLimits).
    private RequestRate? _serviceRate;
    private RequestRate? _launchRate;

    private RequestRate ServiceRate => Volatile.Read(ref _serviceRate) ?? Interlocked.CompareExchange(
        ref _serviceRate, new RequestRate(Limits.ServiceRequestsPerSecond, TimeSpan.FromSeconds(1)), null) ?? _serviceRate!;

    private RequestRate LaunchRate => Volatile.Read(ref _launchRate) ?? Interlocked.CompareExchange(
        ref _launchRate, new RequestRate(Limits.LaunchesPerTenSeconds, TimeSpan.FromSeconds(10)), null) ?? _launchRate!;

    // The paths the user gave the guest through this host, its file dialogs and drops, which the guest may open by
    // default; the most recent 4,096.
    private readonly object _givenGate = new();
    private readonly HashSet<string> _given = new(HostServices.PathComparer);
    private readonly Queue<string> _givenOrder = new();
    private const int MaximumGivenPaths = 4_096;

    internal void GivePaths(IEnumerable<string> paths)
    {
        lock (_givenGate)
        {
            foreach (var path in paths)
            {
                if (FullPath(path) is not { } full || !_given.Add(full)) continue;
                _givenOrder.Enqueue(full);
                if (_givenOrder.Count > MaximumGivenPaths) _given.Remove(_givenOrder.Dequeue());
            }
        }
    }

    private bool IsGiven(string path)
    {
        if (FullPath(path) is not { } full) return false;
        lock (_givenGate) return _given.Contains(full);
    }

    // The path without . and .. segments, so a guest cannot reach another file through a given one's directory.
    private static string? FullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    // The window service requests (host-protocol.md, Window services); false for another kind.
    private async Task<bool> OnServiceRequestAsync(HostRequest request)
    {
        if (request.Kind is not ("clipboard-read" or "clipboard-write" or "monitors" or "launch")) return false;
        if (!InUse(HostServices.Feature))
        {
            await request.FailAsync("rejected", "invalid-request", $"'{request.Kind}' belongs to {HostServices.Feature}, which is not in use.").ConfigureAwait(false);
            return true;
        }
        var message = request.Message;
        var valid = request.Kind switch
        {
            "clipboard-read" => HostServices.CheckClipboardRead(message),
            "clipboard-write" => HostServices.CheckClipboardWrite(message),
            "launch" => HostServices.CheckLaunch(message),
            _ => true,
        };
        RunicHostedWindow? window;
        lock (_gate) window = HostProtocol.String(message, "window") is { } id ? _windows.GetValueOrDefault(id) : null;
        if (window is null || !valid)
        {
            await request.FailAsync("rejected", "invalid-request",
                $"The '{request.Kind}' request is missing a member, has one out of range or names no window this channel opened.").ConfigureAwait(false);
            return true;
        }
        if (!window.IsOpen)
        {
            await request.ReplyAsync(Outcome(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed))).ConfigureAwait(false);
            return true;
        }
        if (!(request.Kind == "launch" ? LaunchRate : ServiceRate).TryTake())
        {
            await request.FailAsync("rejected", "capacity", request.Kind == "launch"
                ? "The application opened as many files and links as the host allows for now."
                : "The application's clipboard and monitor requests are over the host's rate.").ConfigureAwait(false);
            return true;
        }
        JsonObject reply;
        try
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(window.Closing, _ending.Token);
            reply = request.Kind switch
            {
                "clipboard-read" => await ClipboardReadAsync(message, window, cancel.Token).ConfigureAwait(false),
                "clipboard-write" => await ClipboardWriteAsync(message, window, cancel.Token).ConfigureAwait(false),
                "monitors" => await MonitorsAsync(window, cancel.Token).ConfigureAwait(false),
                _ => await LaunchAsync(message, window, cancel.Token).ConfigureAwait(false),
            };
        }
        catch (RejectedException rejected)
        {
            await request.FailAsync(rejected.Kind, rejected.Reason, Limit(rejected.Message)).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
            // The window closed, or the guest went away, while the service ran.
            reply = Outcome(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed));
        }
        await request.ReplyAsync(reply).ConfigureAwait(false);
        return true;
    }

    private static bool IsActive(RunicHostedWindow window) => window.PresentationOrNull?.Activity is { IsActive: true };

    // The policy's decision; the request that runs, which keeps the guest's kind and window.
    private async Task<RunicHostedServiceRequest> DecideServiceAsync(RunicHostedServiceRequest proposed, CancellationToken cancellationToken)
    {
        RunicHostedServiceDecision decision;
        try { decision = await Policy.ServiceAsync(proposed, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            DesktopLog.HostedWindowPolicyFailed(_logger, error, error.GetType().Name);
            throw PolicyFailed();
        }
        if (!decision.IsAllowed)
            throw new RejectedException("rejected", "refused", decision.Message ?? DefaultRefusal(proposed));
        var applied = decision.Request ?? proposed;
        if (!ReferenceEquals(applied.Window, proposed.Window) || applied.Kind != proposed.Kind)
        {
            DesktopLog.HostedWindowPolicyFailed(_logger, null, "InvalidService");
            throw new RejectedException("failed", null, "The host's window policy returned another kind of request or another window.");
        }
        return applied;
    }

    private static string DefaultRefusal(RunicHostedServiceRequest request) => request.Kind switch
    {
        RunicHostedServiceKind.ClipboardRead => "The host reads the clipboard only for the active window.",
        RunicHostedServiceKind.Launch when request.Uri is not null => "The host opens only http, https and mailto links of the active window.",
        RunicHostedServiceKind.Launch => "The host opens only files the user gave the application, for the active window.",
        _ => "The host's window policy refused the request.",
    };

    private async Task<JsonObject> ClipboardReadAsync(JsonObject message, RunicHostedWindow window, CancellationToken cancellationToken)
    {
        var format = HostServices.Format(HostProtocol.String(message, "format")) ?? DesktopClipboardFormats.None;
        // Reads only for the window the user works in (host-protocol.md, Window services).
        _ = await DecideServiceAsync(new RunicHostedServiceRequest(window, RunicHostedServiceKind.ClipboardRead, IsActive(window))
        {
            ClipboardFormats = format,
        }, cancellationToken).ConfigureAwait(false);
        var clipboard = window.Dialogs(CreateDialogs).Clipboard;
        var maximum = (int)(HostProtocol.Integer(message, "maximum") ?? 0);
        if (format is not (DesktopClipboardFormats.None or DesktopClipboardFormats.Text) && clipboard is not IDesktopClipboard)
            return Outcome(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
        var data = clipboard as IDesktopClipboard;
        var (value, failure) = format switch
        {
            DesktopClipboardFormats.None when data is null =>
                // A text-only clipboard's formats: text when it has some, which a read of no characters shows as too large.
                await clipboard.ReadTextAsync(0, cancellationToken).ConfigureAwait(false) switch
                {
                    PlatformResult<string?>.Success(null) => (new JsonArray(), null),
                    PlatformResult<string?>.Success or PlatformResult<string?>.Failed { Code: PlatformFailureCode.TooLarge } => (new JsonArray("text"), null),
                    var other => Read(other, static _ => null),
                },
            DesktopClipboardFormats.None => Read(await data!.GetFormatsAsync(cancellationToken).ConfigureAwait(false), HostServices.WriteFormats),
            DesktopClipboardFormats.Text => Read(await clipboard.ReadTextAsync(maximum, cancellationToken).ConfigureAwait(false), static text => text),
            DesktopClipboardFormats.Html => Read(await data!.ReadHtmlAsync(maximum, cancellationToken).ConfigureAwait(false), static text => text),
            DesktopClipboardFormats.Rtf => Read(await data!.ReadRtfAsync(maximum, cancellationToken).ConfigureAwait(false), static text => text),
            DesktopClipboardFormats.Image => Read(await data!.ReadImageAsync(maximum, cancellationToken).ConfigureAwait(false),
                static image => image is null ? null : Convert.ToBase64String(image)),
            _ => Read(await data!.ReadFilesAsync(maximum, cancellationToken).ConfigureAwait(false),
                static files => new JsonArray([.. files.Select(static file => (JsonNode)file)])),
        };
        if (failure is not null) return Outcome(failure);
        var reply = new JsonObject { ["outcome"] = "success", ["value"] = value };
        // A reply larger than a message fails as content over the guest's bound does.
        return HostServices.Fits(reply) ? reply : Outcome(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.TooLarge));
    }

    private static (JsonNode? Value, PlatformResult<PlatformUnit>? Failure) Read<T>(PlatformResult<T> result, Func<T, JsonNode?> write) => result switch
    {
        PlatformResult<T>.Success(var value) => (write(value), null),
        PlatformResult<T>.Unavailable(var reason) => (null, new PlatformResult<PlatformUnit>.Unavailable(reason)),
        PlatformResult<T>.Failed(var code, var diagnostic) => (null, new PlatformResult<PlatformUnit>.Failed(code, diagnostic)),
        _ => (null, new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Unknown)),
    };

    private async Task<JsonObject> ClipboardWriteAsync(JsonObject message, RunicHostedWindow window, CancellationToken cancellationToken)
    {
        var content = HostServices.ReadClipboardContent((JsonObject)message["content"]!);
        var formats = (content.Text is null ? 0 : DesktopClipboardFormats.Text) | (content.Html is null ? 0 : DesktopClipboardFormats.Html)
            | (content.Rtf is null ? 0 : DesktopClipboardFormats.Rtf) | (content.Image.IsEmpty ? 0 : DesktopClipboardFormats.Image)
            | (content.Files.IsDefaultOrEmpty ? 0 : DesktopClipboardFormats.Files);
        _ = await DecideServiceAsync(new RunicHostedServiceRequest(window, RunicHostedServiceKind.ClipboardWrite, isAllowedByDefault: true)
        {
            ClipboardFormats = formats,
        }, cancellationToken).ConfigureAwait(false);
        var clipboard = window.Dialogs(CreateDialogs).Clipboard;
        try
        {
            var result = clipboard is IDesktopClipboard data
                ? await data.WriteAsync(content, cancellationToken).ConfigureAwait(false)
                : formats == DesktopClipboardFormats.Text
                    ? await clipboard.WriteTextAsync(content.Text!, cancellationToken).ConfigureAwait(false)
                    : new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            return Outcome(result);
        }
        catch (ArgumentException error) { throw new RejectedException("rejected", "invalid-request", $"The clipboard content is not valid: {error.Message}"); }
    }

    private async Task<JsonObject> MonitorsAsync(RunicHostedWindow window, CancellationToken cancellationToken)
    {
        _ = await DecideServiceAsync(new RunicHostedServiceRequest(window, RunicHostedServiceKind.Monitors, isAllowedByDefault: true),
            cancellationToken).ConfigureAwait(false);
        var presentation = window.Presentation;
        var monitors = presentation.Monitors;
        WatchMonitors(window, monitors);
        var result = await monitors.ListAsync(cancellationToken).ConfigureAwait(false);
        if (result is not PlatformResult<ImmutableArray<DesktopMonitor>>.Success(var list))
            return Outcome(Read(result, static _ => null).Failure ?? new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Unknown));
        return new JsonObject
        {
            ["outcome"] = "success",
            ["monitors"] = new JsonArray([.. list.Take(HostServices.MaximumMonitors).Select(static monitor => (JsonNode)HostServices.WriteMonitor(monitor))]),
            ["current"] = presentation.State?.MonitorId,
        };
    }

    // monitors-changed for a window whose guest listed the monitors, while it is open: after each change the host's
    // monitors report, coalesced as the window's other pushes.
    private static void WatchMonitors(RunicHostedWindow window, IDesktopMonitors monitors)
    {
        if (!window.BeginMonitorWatch()) return;
        _ = Task.Run(async () =>
        {
            var first = true;
            try
            {
                await foreach (var _ in monitors.WatchAsync(window.Closing).ConfigureAwait(false))
                {
                    if (first)
                    {
                        first = false;
                        continue;
                    }
                    window.Push("monitors-changed", () => new JsonObject { ["kind"] = "monitors-changed", ["window"] = window.Id });
                }
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
        });
    }

    private async Task<JsonObject> LaunchAsync(JsonObject message, RunicHostedWindow window, CancellationToken cancellationToken)
    {
        var active = IsActive(window);
        RunicHostedServiceRequest proposed;
        if (HostProtocol.String(message, "uri") is { } text)
        {
            var uri = new Uri(text, UriKind.Absolute);
            proposed = new RunicHostedServiceRequest(window, RunicHostedServiceKind.Launch, active && HostServices.IsDefaultUri(uri)) { Uri = uri };
        }
        else
        {
            var path = HostProtocol.String(message, "path")!;
            proposed = new RunicHostedServiceRequest(window, RunicHostedServiceKind.Launch, active && IsGiven(path))
            {
                Path = path,
                Operation = HostServices.Operation(HostProtocol.String(message, "operation") ?? "open")!.Value,
            };
        }
        var applied = await DecideServiceAsync(proposed, cancellationToken).ConfigureAwait(false);
        var launcher = window.Dialogs(CreateDialogs).Launcher;
        try
        {
            // The host's own launcher, with its activation rights and its rules (link schemes, mail attachments,
            // programs); a request the policy changed runs as the policy returned it.
            var result = applied.Uri is { } link
                ? await launcher.LaunchUriAsync(link, cancellationToken).ConfigureAwait(false)
                : applied.Path is { } file && Path.IsPathFullyQualified(file)
                    ? await launcher.LaunchAsync(file, applied.Operation, cancellationToken).ConfigureAwait(false)
                    : throw new RejectedException("failed", null, "The host's window policy returned a launch without an absolute path or a link.");
            return Outcome(result);
        }
        catch (ArgumentException error)
        {
            throw ReferenceEquals(applied, proposed)
                ? new RejectedException("rejected", "invalid-request", $"The launch is not valid: {error.Message}")
                : new RejectedException("failed", null, "The host's window policy returned a launch that is not valid.");
        }
    }

    // ----- Drops

    // The files the user drops onto the window reach the guest (files-dropped), when its Window takes them and the policy
    // allows; the paths become ones the guest may open. Files without a path, such as macOS file promises, are not
    // forwarded: the host releases them.
    private void ForwardDrops(RunicHostedWindow window, DesktopWindow presentation) =>
        presentation.FilesDropped += (_, drop) => _ = ForwardDropAsync(window, drop);

    private async Task ForwardDropAsync(RunicHostedWindow window, DesktopFileDropEventArgs drop)
    {
        foreach (var promised in drop.PromisedFiles)
        {
            try { await promised.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        if (drop.Paths.Count == 0 || !window.IsOpen) return;
        ImmutableArray<string> paths;
        try
        {
            var proposed = new RunicHostedServiceRequest(window, RunicHostedServiceKind.FileDrop, isAllowedByDefault: true) { Paths = [.. drop.Paths] };
            var applied = await DecideServiceAsync(proposed, window.Closing).ConfigureAwait(false);
            paths = applied.Paths.IsDefault
                ? []
                : [.. applied.Paths.Where(static path => HostServices.IsAbsolutePath(JsonValue.Create(path))).Take(HostServices.MaximumDroppedPaths)];
        }
        catch (Exception error) when (error is RejectedException or OperationCanceledException or ObjectDisposedException) { return; }
        if (paths.IsEmpty) return;
        GivePaths(paths);
        var push = new JsonObject
        {
            ["kind"] = "files-dropped",
            ["window"] = window.Id,
            ["paths"] = new JsonArray([.. paths.Select(static path => (JsonNode)path)]),
            ["effect"] = HostServices.EffectToken(drop.Effect),
        };
        if (drop.Target is { Length: > 0 and <= HostServices.MaximumDropTarget } target) push["target"] = target;
        await PushAsync(push).ConfigureAwait(false);
    }
}
