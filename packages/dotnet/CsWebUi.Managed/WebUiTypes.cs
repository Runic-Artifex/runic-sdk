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

/// <summary>Controls process-wide managed WebUI behavior.</summary>
public enum WebUiConfiguration : uint
{
    /// <summary>Reserved for browser-host compatibility in M3.</summary>
    ShowWaitConnection = 0,

    /// <summary>Reserved for event-dispatch compatibility.</summary>
    UiEventBlocking,

    /// <summary>Reserved for folder-monitor compatibility.</summary>
    FolderMonitor,

    /// <summary>Whether more than one browser may connect to a window concurrently.</summary>
    MultiClient,

    /// <summary>Whether managed WebUI authentication cookies are enabled.</summary>
    UseCookies,

    /// <summary>Reserved because managed callbacks are asynchronous by design.</summary>
    AsynchronousResponse,
}
