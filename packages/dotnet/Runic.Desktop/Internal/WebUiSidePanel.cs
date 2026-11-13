namespace Runic.Desktop.Internal;

// sdk#864, P2: what DesktopWindow.OpenSidePanelAsync asks a built-in window host for. The host creates the panel's view
// beside the page's from the page's environment and profile, runs DocumentStartScript in it (the panel credential of
// the surface's panel slot, WebUiWindow.CreatePanelDocumentStartScript), loads Url and only then grows the window by
// Width on the left or right edge (SidePanelPlanner). It reports every close, its own and the requested one, through
// Closed, once, on any thread.
internal sealed record WebUiSidePanelRequest(
    Uri Url,
    string DocumentStartScript,
    bool Left,
    int Width,
    TimeSpan LoadTimeout,
    Action<DesktopSidePanelCloseReason> Closed);

// The host's side of an open panel. Its operations run on the window's thread and re-check the window there.
internal interface IWebUiSidePanel
{
    bool IsLeft { get; }

    // Device-independent pixels, as requested.
    int Width { get; }

    ValueTask<DesktopSidePanelRefusal?> MoveAsync(bool left, CancellationToken cancellationToken);

    ValueTask<DesktopSidePanelRefusal?> SetWidthAsync(int width, CancellationToken cancellationToken);

    ValueTask FocusAsync(CancellationToken cancellationToken);

    ValueTask CloseAsync(CancellationToken cancellationToken);
}

// A host's answer to WebUiSidePanelRequest: the open panel, or why none opened (the window did not change).
internal readonly record struct WebUiSidePanelOpening(IWebUiSidePanel? Panel, DesktopSidePanelRefusal? Refusal)
{
    internal static WebUiSidePanelOpening Refused(DesktopSidePanelRefusal refusal) => new(null, refusal);
}
