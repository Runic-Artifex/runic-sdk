namespace Runic.Desktop;

/// <summary>Whether a page connected to a surface or disconnected from it.</summary>
/// <remarks>Open: a later release may add kinds, so ignore a value you do not handle.</remarks>
public enum DesktopConnectionEventKind
{
    /// <summary>A page disconnected from the surface: it closed, reloaded or navigated away.</summary>
    Disconnected = 0,

    /// <summary>A page authenticated and connected to the surface.</summary>
    Connected = 1,
}

/// <summary>Reports that a page connected to or disconnected from a surface.</summary>
/// <remarks>Delivered to the handlers of <see cref="DesktopSurface.SubscribeConnectionEvents"/>.</remarks>
public sealed class DesktopConnectionEvent
{
    internal DesktopConnectionEvent(DesktopSurface surface, WebUiEvent webUiEvent)
    {
        Surface = surface;
        Kind = (DesktopConnectionEventKind)webUiEvent.EventType;
        SessionId = webUiEvent.ConnectionId;
    }

    /// <summary>Gets whether the page connected or disconnected.</summary>
    public DesktopConnectionEventKind Kind { get; }

    /// <summary>
    /// Gets the surface. Its <see cref="DesktopSurface.ConnectionCount"/> already includes this event's change.
    /// </summary>
    public DesktopSurface Surface { get; }

    // The connection's transport identity, the same as PresentationSession.Id; the Bridge releases its client by it.
    internal ulong SessionId { get; }
}
