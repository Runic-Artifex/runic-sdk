namespace CsWebUi.Managed;

/// <summary>Identifies an event reported by a managed WebUI bridge.</summary>
public enum WebUiEventType : uint
{
    /// <summary>A client disconnected from a window.</summary>
    Disconnected = 0,

    /// <summary>A client connected to a window.</summary>
    Connected,

    /// <summary>An HTML element was clicked.</summary>
    MouseClick,

    /// <summary>A client attempted to navigate within the window.</summary>
    Navigation,

    /// <summary>A JavaScript binding invoked managed code.</summary>
    Callback,
}
