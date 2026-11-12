using System.Text.Json.Nodes;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

// The host's side of region/1 (host-protocol.md, Regions): a window the policy embeds opens without a native window of
// its own and shows in a region of a host page; its presentation requests map to the region; its dialogs and context
// menus are its host window's, and its menu bar is not shown.
public sealed partial class RunicHostedApplication
{
    // The window-open reply's features of a region window: never minimize, visibility, full screen or color schemes.
    private const RunicWindowPresentationFeatures RegionFeatures = RunicWindowPresentationFeatures.Present |
        RunicWindowPresentationFeatures.DocumentEdited | RunicWindowPresentationFeatures.Activity | RunicWindowPresentationFeatures.OwnedWindows;

    private HostedRegionDirectory Regions => _host.RegionDirectory;

    // window-open with the policy's Embed: the window opens at once, and attaches when a region takes it.
    private async Task OpenRegionWindowAsync(HostRequest request, RunicHostedWindow window, RunicHostedWindowDecision decision,
        DesktopPermissionGrant permissions)
    {
        if (!InUse(HostFeatures.Region) || window.IsModal)
        {
            DesktopLog.HostedWindowPolicyFailed(_logger, null, "EmbedUnavailable");
            await request.FailAsync("failed", null, window.IsModal
                ? "The host's window policy embedded a modal window, which cannot show in a region."
                : "The host's window policy embedded a window, but the application does not implement region/1.").ConfigureAwait(false);
            return;
        }
        var options = decision.RegionOptions ?? RunicHostedRegionOptions.Default;
        var grant = options.Permissions ?? (permissions & _host.WindowOptions.AllowedPermissions);
        // The frame's permissions policy: only what the policy granted (host-protocol.md, Features in a region).
        var allow = grant.HasFlag(DesktopPermissionGrant.MediaCapture) ? "camera; microphone" : "";
        var region = new RunicHostedRegion(this, Regions, window, decision.Region!, decision.HostWindow, options, allow);
        window.AttachRegion(region, RegionFeatures);
        if (Regions.Open(region, out _) is { } failure)
        {
            await region.EndAsync().ConfigureAwait(false);
            window.MarkClosed();
            await request.FailAsync("failed", failure,
                $"Two of the host's windows declare the region '{decision.Region}'; the host's window policy must name the host window.").ConfigureAwait(false);
            return;
        }
        bool ended;
        lock (_gate)
        {
            ended = _ended;
            if (!ended)
            {
                _windows[window.Id] = window;
                _open.Add(window);
            }
        }
        if (ended)
        {
            await region.EndAsync().ConfigureAwait(false);
            window.MarkClosed();
            await AnswerAsync(request.FailAsync("failed", null, "The application ended while its window opened.")).ConfigureAwait(false);
            return;
        }
        _owner.RaiseWindowOpened(window);
        try
        {
            await request.ReplyAsync(new JsonObject
            {
                ["presentation"] = "region",
                ["features"] = new JsonArray([.. FeatureNames(RegionFeatures).Select(static name => (JsonNode)name)]),
                ["activity"] = region.ActivityMembers(),
                ["region"] = region.OpenRegionMember(),
            }).ConfigureAwait(false);
        }
        catch (HostChannelClosedException)
        {
            await window.OnGuestClosedAsync().ConfigureAwait(false);
            return;
        }
        // The guest knows the window now: a region that took it attaches (region-attach).
        region.Opened.TrySetResult();
    }

    // The region window closed, for any reason: its region ends, then the window as a native window's close would.
    internal async Task CloseRegionWindowAsync(RunicHostedWindow window)
    {
        if (window.Region is { } region) await region.EndAsync().ConfigureAwait(false);
        await OnWindowClosedAsync(window, null).ConfigureAwait(false);
    }

    // region-attach: the guest's frame launch address, or why there is none.
    internal async Task<RegionAttachResult> RegionAttachAsync(RunicHostedWindow window, string embedder, RunicHostedRegionSize size,
        Runic.Application.Views.HostedRegionSizing sizing, bool allowSameSite)
    {
        if (_channel is not { IsOpen: true } channel) return new(null, false, "closed");
        var message = new JsonObject
        {
            ["kind"] = "region-attach",
            ["window"] = window.Id,
            ["embedding"] = "frame",
            ["embedder"] = embedder,
            ["width"] = size.Width,
            ["height"] = size.Height,
            ["scale"] = size.Scale,
            ["sizing"] = sizing == Runic.Application.Views.HostedRegionSizing.ContentHeight ? "content-height" : "fill",
            ["allowSameSite"] = allowSameSite,
        };
        JsonObject reply;
        try { reply = await channel.RequestAsync(message, _ending.Token).ConfigureAwait(false); }
        catch (Exception error) when (error is HostChannelClosedException or OperationCanceledException) { return new(null, false, "closed"); }
        if (HostProtocol.Boolean(reply, "ok") != true)
            return new(null, false, reply["error"] is JsonObject failed
                ? HostProtocol.String(failed, "reason") ?? HostProtocol.String(failed, "kind") ?? "failed"
                : "failed");
        if (reply["frame"] is not JsonObject frame || FrameUrl(HostProtocol.String(frame, "url")) is not { } url ||
            (frame["sameSite"] is not null && HostProtocol.Boolean(frame, "sameSite") is null))
            return new(null, false, "invalid-reply");
        return new(url.OriginalString, HostProtocol.Boolean(frame, "sameSite") == true, null);
    }

    internal readonly record struct RegionAttachResult(string? Url, bool SameSite, string? Failure);

    // A frame launch address (host-protocol.md, region-attach): http on a loopback literal and an explicit port, with a
    // path that ends in /_runic_frame/<code>, at most 2,048 characters.
    internal static Uri? FrameUrl(string? text)
    {
        if (text is not { Length: > 0 and <= 2_048 } || !Uri.TryCreate(text, UriKind.Absolute, out var url)) return null;
        if (url.Scheme != Uri.UriSchemeHttp || url.IsDefaultPort || url.Host is not ("127.0.0.1" or "[::1]") ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment) ||
            !text.Contains("://" + url.Authority, StringComparison.Ordinal))
            return null;
        var path = url.AbsolutePath;
        var marker = path.LastIndexOf("/_runic_frame/", StringComparison.Ordinal);
        if (marker < 0) return null;
        var code = path[(marker + "/_runic_frame/".Length)..];
        return code.Length > 0 && !code.Contains('/') ? url : null;
    }

    // set-title and region-focus (guest → host); false for another kind.
    private async Task<bool> OnRegionRequestAsync(HostRequest request)
    {
        if (request.Kind is not ("set-title" or "region-focus")) return false;
        if (!InUse(HostFeatures.Region))
        {
            await request.FailAsync("rejected", "invalid-request", $"'{request.Kind}' belongs to {HostFeatures.Region}, which is not in use.").ConfigureAwait(false);
            return true;
        }
        var message = request.Message;
        RunicHostedWindow? window;
        lock (_gate) window = HostProtocol.String(message, "window") is { } id ? _windows.GetValueOrDefault(id) : null;
        var valid = request.Kind == "set-title"
            ? message["title"] is JsonValue && HostProtocol.Text(message, "title", RunicHostedRegion.MaximumTitleLength) is { } title && !title.Contains('\0')
            : HostProtocol.String(message, "focus") == "leave" && HostProtocol.String(message, "direction") is "next" or "previous";
        // A region message for a window that is not a region window of the channel (host-protocol.md, Region messages).
        if (window?.Region is not { } region || !valid)
        {
            await request.FailAsync("rejected", "invalid-request",
                $"The '{request.Kind}' request is missing a member, has one out of range or names no region window of this channel.").ConfigureAwait(false);
            return true;
        }
        if (!window.IsOpen)
        {
            await request.ReplyAsync(Outcome(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed))).ConfigureAwait(false);
            return true;
        }
        if (request.Kind == "region-focus")
        {
            // Frame mode traverses out of the frame natively: the focus has left already (host-protocol.md, region-focus).
            await request.ReplyAsync(region.NativeWindow is null
                ? Outcome(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed))
                : Outcome(new PlatformResult<PlatformUnit>.Success(new PlatformUnit()))).ConfigureAwait(false);
            return true;
        }
        if (!region.TitleRate.TryTake())
        {
            await request.FailAsync("rejected", "capacity", "The window's set-title requests are over the host's rate.").ConfigureAwait(false);
            return true;
        }
        region.SetTitle(HostProtocol.String(message, "title"));
        await request.ReplyAsync(Outcome(new PlatformResult<PlatformUnit>.Success(new PlatformUnit()))).ConfigureAwait(false);
        return true;
    }

    // region-page (guest → host push).
    private void OnRegionPage(JsonObject push)
    {
        RunicHostedWindow? window;
        lock (_gate) window = HostProtocol.String(push, "window") is { } id ? _windows.GetValueOrDefault(id) : null;
        if (window?.Region is { } region && HostProtocol.String(push, "page") is { } page) region.OnPage(page);
    }

    // The outcome unavailable with the reason embedded: the request does not apply in a region (host-protocol.md,
    // Presentation in a region). A guest that does not know the reason reads it as backend-unavailable.
    internal static JsonObject Embedded() => new() { ["outcome"] = "unavailable", ["reason"] = "embedded" };

    // A presentation request of a region window, after the policy (host-protocol.md, Presentation in a region).
    private async Task<JsonObject> ApplyRegionPresentationAsync(RunicHostedPresentationRequest request)
    {
        var region = request.Window.Region!;
        var native = region.NativeWindow;
        switch (request.Kind)
        {
            case RunicHostedPresentationKind.Present:
                if (native is null) return Outcome(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable));
                // The host window comes forward with the host's activation rights; the focus moves into the region only
                // when the host page holds none (host-protocol.md, Focus in a region).
                var presented = await new DesktopWindowPresentation(native, () => new DesktopNativeOwner(native))
                    .PresentAsync(null, _ending.Token).ConfigureAwait(false);
                if (presented is PlatformResult<PlatformUnit>.Success) region.RequestFocus();
                return Outcome(presented);
            case RunicHostedPresentationKind.Resize when request is { Width: { } width and >= 1, Height: { } height and >= 1 }:
                return region.SetPreferredSize(width, height) ? Outcome(new PlatformResult<PlatformUnit>.Success(new PlatformUnit())) : Embedded();
            case RunicHostedPresentationKind.SetDocumentEdited:
                region.SetDocumentEdited(request.Value ?? false);
                return Outcome(new PlatformResult<PlatformUnit>.Success(new PlatformUnit()));
            case RunicHostedPresentationKind.SetLayoutDirection:
                // The guest's page applies its own direction; the host window keeps its own.
                return Outcome(new PlatformResult<PlatformUnit>.Success(new PlatformUnit()));
            case RunicHostedPresentationKind.Announce when request.Text is { Length: > 0 } text:
                if (native is null) return Outcome(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable));
                return Outcome(await HostedPresentation.RunAsync(native,
                    () => native.AnnounceAsync(text, request.Priority ?? DesktopAnnouncementPriority.Polite, _ending.Token)).ConfigureAwait(false));
            case RunicHostedPresentationKind.ToggleMaximized or RunicHostedPresentationKind.Move or RunicHostedPresentationKind.SetRepresentedFile:
                return Embedded();
            case RunicHostedPresentationKind.Minimize or RunicHostedPresentationKind.SetVisible or RunicHostedPresentationKind.SetFullScreen
                or RunicHostedPresentationKind.SetColorScheme:
                return Outcome(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
            default:
                return Outcome(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData));
        }
    }

    // A region's events, one after the other on a thread-pool thread (RunicHostedApplications.Post).
    internal void Post(Action action) => _owner.Post(action);

    internal void RaiseRegionAttached(RunicHostedRegion region) => _owner.RaiseRegionAttached(region);

    internal void RaiseRegionDetached(RunicHostedRegion region, string reason) => _owner.RaiseRegionDetached(region, reason);

    internal void LogRegionAttachFailed(string region, string reason) => DesktopLog.HostedRegionAttachFailed(_logger, Guest.Name, region, reason);

    internal void LogRegionSameSite(string region) => DesktopLog.HostedRegionSameSite(_logger, Guest.Name, region);

    internal void LogRegionFailed(Exception error) => DesktopLog.HostedRegionFailed(_logger, error, error.GetType().Name);
}
