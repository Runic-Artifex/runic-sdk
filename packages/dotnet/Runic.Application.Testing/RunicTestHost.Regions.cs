using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Views.Desktop.Hosting;

namespace Runic.Application.Testing;

// region/1 (host-protocol.md, Regions): the test host shows the windows that RunicTestHostOptions.EmbedWindow chooses in
// a region of a host page, attaches them when a test asks and records what the guest reports.
public sealed partial class RunicTestHost
{
    // Whether the window-open of window shows it in a region: region/1 is in use and the options choose it.
    private bool Embeds(RunicTestHostedWindow window) =>
        Features.Contains(HostFeatures.Region, StringComparer.Ordinal) && _options.EmbedWindow?.Invoke(window) == true;

    // The reply of a region window's window-open: no window features, not visible until a region shows it.
    private static JsonObject RegionReply(Views.RunicWindowPresentationFeatures features) => new()
    {
        ["presentation"] = "region",
        ["features"] = new JsonArray([.. FeatureNames(features).Select(static name => (JsonNode)name)]),
        ["activity"] = new JsonObject { ["active"] = false, ["minimized"] = false, ["visible"] = false },
        ["region"] = null,
    };

    // The features a region window never has (host-protocol.md, Presentation of a window).
    private const Views.RunicWindowPresentationFeatures RegionlessFeatures =
        Views.RunicWindowPresentationFeatures.Minimize | Views.RunicWindowPresentationFeatures.Visibility |
        Views.RunicWindowPresentationFeatures.FullScreen | Views.RunicWindowPresentationFeatures.ColorScheme |
        Views.RunicWindowPresentationFeatures.PerWindowColorScheme;

    // The presentation requests of a region window that do not apply there, and set-title of a window shown in a window
    // (host-protocol.md, Presentation in a region); null for a request answered as in a window.
    private static JsonObject? RegionOutcome(RunicTestHostedWindow window, string kind) => (window.IsRegion, kind) switch
    {
        (true, "toggle-maximized" or "move" or "set-represented-file") => Unavailable("embedded"),
        (true, "resize") when !window.FollowsContentHeight => Unavailable("embedded"),
        _ => null,
    };

    private static JsonObject Unavailable(string reason) => new() { ["outcome"] = "unavailable", ["reason"] = reason };

    private void OnRegionPage(JsonObject push)
    {
        if (FindWindow(HostProtocol.String(push, "window")) is { IsRegion: true } window && HostProtocol.String(push, "page") is { } page)
            window.RecordPage(page);
    }
}

public sealed partial class RunicTestHostedWindow
{
    private readonly List<string> _pages = [];
    private string? _sizing;

    /// <summary>
    /// Whether the window shows in a region of a host page (<c>region/1</c>) instead of a window of its own, as
    /// <see cref="RunicTestHostOptions.EmbedWindow"/> chose. Its page loads only from the frame launch address of
    /// <see cref="AttachRegionAsync"/>; its <see cref="LaunchUrl"/> admits nobody.
    /// </summary>
    public bool IsRegion { get; internal set; }

    /// <summary>Whether a region shows the window now: from <see cref="AttachRegionAsync"/> until <see cref="DetachRegionAsync"/>.</summary>
    public bool IsAttached { get; private set; }

    internal bool FollowsContentHeight => IsAttached && _sizing == "content-height";

    /// <summary>
    /// The guest's reports about the page that its frame admitted, in order (<c>region-page</c>): <c>connected</c>,
    /// <c>disconnected</c> or <c>revoked</c>.
    /// </summary>
    public IReadOnlyList<string> RegionPages
    {
        get { lock (_pages) return [.. _pages]; }
    }

    internal void RecordPage(string page)
    {
        lock (_pages) _pages.Add(page);
    }

    /// <summary>
    /// Attaches a region window to a region of a host page, as a host does when the page shows the region
    /// (<c>region-attach</c>), and returns the guest's one-time frame launch address. Load it once, in an
    /// <c>&lt;iframe&gt;</c> of a page of <paramref name="embedder"/>: the guest lets no other origin frame its page, and
    /// a second load revokes the page's token. An attached window is detached first, by the guest.
    /// </summary>
    /// <param name="embedder">
    /// The origin of the host page that frames the guest's page: <c>http://127.0.0.1:</c>, <c>http://[::1]:</c> or
    /// <c>http://localhost:</c> and a port, nothing after it.
    /// </param>
    /// <param name="width">The region's width in CSS pixels.</param>
    /// <param name="height">The region's height in CSS pixels.</param>
    /// <param name="scale">The host page's device pixel ratio.</param>
    /// <param name="sizing">
    /// <c>fill</c>, or <c>content-height</c> for a region that takes the height the guest's page asks for with
    /// <c>resize</c>, which the test host then answers with success.
    /// </param>
    /// <param name="allowSameSite">
    /// Whether the guest may serve the frame from the embedder's own site where it cannot listen on the other loopback
    /// family.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait for the reply.</param>
    /// <returns>The frame launch address (<c>frame.url</c>).</returns>
    /// <exception cref="InvalidOperationException">The window is not a region window, or the guest refused the attach.</exception>
    public async Task<Uri> AttachRegionAsync(string embedder, int width = 800, int height = 600, double scale = 1,
        string sizing = "fill", bool allowSameSite = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(sizing);
        if (!IsRegion) throw new InvalidOperationException("The window shows in a window of its own, not in a region.");
        var reply = await _host.RequestAsync(new JsonObject
        {
            ["id"] = 0,
            ["kind"] = "region-attach",
            ["window"] = Id,
            ["embedding"] = "frame",
            ["embedder"] = embedder,
            ["width"] = width,
            ["height"] = height,
            ["scale"] = scale,
            ["sizing"] = sizing,
            ["allowSameSite"] = allowSameSite,
        }, cancellationToken).ConfigureAwait(false);
        if (HostProtocol.Boolean(reply, "ok") != true ||
            reply["frame"] is not JsonObject frame || HostProtocol.String(frame, "url") is not { } url)
        {
            var error = reply["error"] as JsonObject;
            throw new InvalidOperationException(
                $"The guest refused the region-attach: {(error is null ? "no frame" : $"{HostProtocol.String(error, "kind")} {HostProtocol.String(error, "reason")}: {HostProtocol.String(error, "message")}")}.");
        }
        _sizing = sizing;
        IsAttached = true;
        FrameIsSameSite = HostProtocol.Boolean(frame, "sameSite") == true;
        return new Uri(url, UriKind.Absolute);
    }

    /// <summary>
    /// Whether the guest served the latest <see cref="AttachRegionAsync"/>'s frame from the embedder's own site
    /// (<c>frame.sameSite</c>).
    /// </summary>
    public bool FrameIsSameSite { get; private set; }

    /// <summary>
    /// Attaches the region window as a <b>component</b> (<c>region-component/1</c>, trusted component mode): the guest
    /// answers its component module, its WebSocket address and a new component token, for a host page whose origin is
    /// <paramref name="embedder"/>. Needs <c>region-component/1</c> in <see cref="RunicTestHostOptions.Features"/>, a
    /// guest that allows component embedding (<c>RunicGuestOptions.AllowComponentEmbedding</c>) and its component build.
    /// </summary>
    /// <param name="embedder">The origin of the host page that loads the component, such as <c>http://127.0.0.1:5173</c>.</param>
    /// <param name="allowSameSite">
    /// Whether the guest may serve the component from the embedder's own site, as Runic's embedded hosts allow; without
    /// it, a component for a <c>127.0.0.1</c> page comes from <c>[::1]</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait for the reply.</param>
    /// <returns>
    /// The module, WebSocket address and token: import <c>Module</c> in the page, create the element named by its
    /// <c>tag</c> export and call its <c>connect({ url, token })</c>, as <c>&lt;runic-hosted-region&gt;</c> does.
    /// </returns>
    /// <exception cref="InvalidOperationException">The window is not a region window, or the guest refused the attach.</exception>
    public async Task<RunicTestComponent> AttachComponentAsync(string embedder, bool allowSameSite = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        if (!IsRegion) throw new InvalidOperationException("The window shows in a window of its own, not in a region.");
        var reply = await _host.RequestAsync(new JsonObject
        {
            ["id"] = 0,
            ["kind"] = "region-attach",
            ["window"] = Id,
            ["embedding"] = "component",
            ["embedder"] = embedder,
            ["width"] = 800,
            ["height"] = 600,
            ["scale"] = 1,
            ["allowSameSite"] = allowSameSite,
        }, cancellationToken).ConfigureAwait(false);
        if (HostProtocol.Boolean(reply, "ok") != true || reply["component"] is not JsonObject component ||
            HostProtocol.String(component, "module") is not { } module || HostProtocol.String(component, "url") is not { } url ||
            HostProtocol.String(component, "token") is not { } token)
        {
            var error = reply["error"] as JsonObject;
            throw new InvalidOperationException(
                $"The guest refused the component attach: {(error is null ? "no component" : $"{HostProtocol.String(error, "kind")} {HostProtocol.String(error, "reason")}: {HostProtocol.String(error, "message")}")}.");
        }
        _sizing = "fill";
        IsAttached = true;
        return new RunicTestComponent(new Uri(module, UriKind.Absolute), new Uri(url, UriKind.Absolute), token,
            HostProtocol.Boolean(component, "sameSite") == true);
    }

    /// <summary>
    /// Detaches the region, as a host does when its page removed the region (<c>region-detach</c>): the guest revokes
    /// the frame's address and token and disconnects its page; the Window stays open.
    /// </summary>
    /// <param name="reason"><c>removed</c>, <c>reload</c>, <c>crashed</c> or <c>host</c>.</param>
    /// <param name="cancellationToken">Cancels sending.</param>
    /// <returns>A task that completes when the push was sent.</returns>
    public Task DetachRegionAsync(string reason = "removed", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        IsAttached = false;
        return _host.PushAsync(new JsonObject { ["kind"] = "region-detach", ["window"] = Id, ["reason"] = reason }, cancellationToken);
    }

    /// <summary>Reports the region's new size and scale to the guest (<c>region-bounds</c>).</summary>
    /// <param name="width">The region's width in CSS pixels.</param>
    /// <param name="height">The region's height in CSS pixels.</param>
    /// <param name="scale">The host page's device pixel ratio.</param>
    /// <param name="cancellationToken">Cancels sending.</param>
    /// <returns>A task that completes when the push was sent.</returns>
    public Task SetRegionBoundsAsync(int width, int height, double scale = 1, CancellationToken cancellationToken = default) =>
        _host.PushAsync(new JsonObject
        {
            ["kind"] = "region-bounds",
            ["window"] = Id,
            ["width"] = width,
            ["height"] = height,
            ["scale"] = scale,
        }, cancellationToken);

    /// <summary>
    /// Moves the keyboard focus into the region, as the host does for Tab, Shift+Tab or its own move
    /// (<c>region-focus</c> enter): the guest's page focuses its first, last or last focused element.
    /// </summary>
    /// <param name="direction"><c>forward</c>, <c>backward</c> or <c>programmatic</c>.</param>
    /// <param name="cancellationToken">Cancels sending.</param>
    /// <returns>A task that completes when the push was sent.</returns>
    public Task FocusRegionAsync(string direction = "forward", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(direction);
        return _host.PushAsync(new JsonObject
        {
            ["kind"] = "region-focus",
            ["window"] = Id,
            ["focus"] = "enter",
            ["direction"] = direction,
        }, cancellationToken);
    }

    /// <summary>The titles the guest sent for its page in the region (<c>set-title</c>), in order.</summary>
    public IReadOnlyList<string> Titles =>
        [.. PresentationRequests.Where(static request => request.Kind == "set-title")
            .Select(static request => request.Message.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                ? title.GetString()!
                : string.Empty)];
}

/// <summary>
/// A guest's component as <see cref="RunicTestHostedWindow.AttachComponentAsync"/> attached it (<c>region-component/1</c>).
/// </summary>
/// <param name="Module">The address of the guest's component module.</param>
/// <param name="Url">The WebSocket address the component connects to.</param>
/// <param name="Token">The component token, which the guest admits only from the embedder's origin.</param>
/// <param name="SameSite">Whether the guest serves it from the embedder's own site.</param>
[System.Diagnostics.CodeAnalysis.Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicTestComponent(Uri Module, Uri Url, string Token, bool SameSite);
