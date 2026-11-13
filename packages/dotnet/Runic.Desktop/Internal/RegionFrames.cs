namespace Runic.Desktop.Internal;

// The attached regions of a host window's page (host-protocol.md, Features in a region): a region shows a hosted
// guest's Window in a frame of the guest's own loopback origin. The window's native host routes what a document of such
// a frame does to the region's guest, never to the host's page: drops (D059.3, files-dropped), permission requests,
// decided by the guest's grant, and unhandled menu keys, which reach the host window's menus. A frame of any other
// origin than the host page's still gets none of these. Read on the engine's threads, written by the host's region code.
internal sealed class RegionFrames
{
    private readonly object _gate = new();
    // Replaced, never changed, so readers on the engine's threads take the array without the gate.
    private RegionFrameRoute[] _routes = [];

    internal bool IsEmpty => Volatile.Read(ref _routes).Length == 0;

    // Routes a frame until the returned value is disposed, such as at the region's detach.
    internal IDisposable Add(RegionFrameRoute route)
    {
        WindowsWebView2Controller.RegionTrace?.Invoke($"route added: {route.Origin}{route.PathBase}");
        lock (_gate) Volatile.Write(ref _routes, [.. _routes, route]);
        return new Removal(this, route);
    }

    // The route of the document at url, the address of a frame's document or, where an engine reports no more, its
    // origin. Frames of one guest share its origin: the longest path base that the document's path is under wins, and
    // an origin alone, or a path under none of them, reads as the guest's latest route. Pages of one origin can script
    // each other anyway, so this never routes across guests. null for every other origin and for anything that does
    // not parse.
    internal RegionFrameRoute? Find(string? url)
    {
        var routes = Volatile.Read(ref _routes);
        if (routes.Length == 0 || url is not { Length: > 0 and <= 8_192 } ||
            !Uri.TryCreate(url, UriKind.Absolute, out var address) || address is null || address.Scheme != Uri.UriSchemeHttp)
            return null;
        var origin = Origin(address);
        RegionFrameRoute? best = null;
        RegionFrameRoute? latest = null;
        foreach (var route in routes)
        {
            if (!string.Equals(route.Origin, origin, StringComparison.Ordinal)) continue;
            latest = route;
            if (address.AbsolutePath.StartsWith(route.PathBase, StringComparison.Ordinal) &&
                (best is null || route.PathBase.Length > best.PathBase.Length))
                best = route;
        }
        return best ?? latest;
    }

    // The content process of the host page ended, which renders its frames too where the engine has no site isolation
    // (WebKit): every attached region detaches with crashed. The page reloads and declares its regions again.
    internal void HostPageProcessEnded()
    {
        foreach (var route in Volatile.Read(ref _routes)) route.Crash(hostPage: true);
    }

    // The process that rendered the frames at these addresses ended, and the engine shows an error page in them
    // (WebView2's frame render process): their regions detach with crashed and attach again.
    internal void FrameProcessEnded(IEnumerable<string?> frameUrls)
    {
        HashSet<RegionFrameRoute> crashed = [];
        foreach (var url in frameUrls)
            if (Find(url) is { } route && crashed.Add(route)) route.Crash(hostPage: false);
    }

    internal static bool SameOrigin(string address, string origin) =>
        Uri.TryCreate(address, UriKind.Absolute, out var first) && Uri.TryCreate(origin, UriKind.Absolute, out var second) &&
        string.Equals(Origin(first), Origin(second), StringComparison.Ordinal);

    // scheme://host:port as browsers serialize an http origin; ports are always explicit on loopback surfaces.
    internal static string Origin(Uri address) => $"{address.Scheme}://{address.Authority}";

    // An address for diagnostics: its origin and path, without a frame launch code (/_runic_frame/<code>) or a fragment,
    // which can carry a frame token.
    internal static string Redact(string? address)
    {
        if (address is null || !Uri.TryCreate(address, UriKind.Absolute, out var uri)) return address is null ? "(none)" : "(not an address)";
        var path = uri.AbsolutePath;
        var marker = path.IndexOf(FrameLaunchBinding.PathSegment + "/", StringComparison.Ordinal);
        if (marker >= 0) path = path[..(marker + FrameLaunchBinding.PathSegment.Length + 1)] + "…";
        return $"{uri.Scheme}://{uri.Authority}{path}";
    }

    private void Remove(RegionFrameRoute route)
    {
        WindowsWebView2Controller.RegionTrace?.Invoke($"route removed: {route.Origin}{route.PathBase}");
        lock (_gate) Volatile.Write(ref _routes, [.. _routes.Where(other => !ReferenceEquals(other, route))]);
    }

    private sealed class Removal(RegionFrames frames, RegionFrameRoute route) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) frames.Remove(route);
        }
    }
}

// One attached region's frame: the frame launch address's origin and the guest surface's path base (the part before
// /_runic_frame/), the guest's permission grant, and where its drops and the end of its content process go. The
// callbacks run on the engine's threads and only queue their work.
internal sealed class RegionFrameRoute
{
    internal RegionFrameRoute(Uri frameUrl, DesktopPermissionGrant permissions, Action<DesktopFileDropEventArgs> dropped, Action<bool> crashed)
    {
        Origin = RegionFrames.Origin(frameUrl);
        var path = frameUrl.AbsolutePath;
        var marker = path.LastIndexOf(FrameLaunchBinding.PathSegment + "/", StringComparison.Ordinal);
        PathBase = marker < 0 ? "/" : path[..(marker + 1)];
        Permissions = permissions;
        Dropped = dropped;
        Crashed = crashed;
    }

    internal string Origin { get; }

    internal string PathBase { get; }

    internal DesktopPermissionGrant Permissions { get; }

    // A drop onto the frame, with the native paths; the page's label and effect are hints, as for any drop.
    internal Action<DesktopFileDropEventArgs> Dropped { get; }

    // The frame's content process ended (detach reason crashed); true when it was the host page's own, which reloads.
    internal Action<bool> Crashed { get; }

    internal void Crash(bool hostPage)
    {
        WindowsWebView2Controller.RegionTrace?.Invoke($"route crashed: {Origin}{PathBase} (host page: {hostPage})");
        try { Crashed(hostPage); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    // Media capture (camera, microphone) only with the guest's grant.
    internal bool AllowsMediaCapture => (Permissions & DesktopPermissionGrant.MediaCapture) != 0;
}
