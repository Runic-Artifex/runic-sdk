namespace Runic.Application.Views.Wpf;

/// <summary>The presentation state of a <c>RunicViewHost</c>.</summary>
/// <remarks>Open: a minor release may add states, so handle values not listed here.</remarks>
public enum RunicViewHostState
{
    /// <summary>No web presentation: the host is unloaded, has no model, or its last open was cancelled.</summary>
    Closed,
    /// <summary>The host is starting its web presentation.</summary>
    Opening,
    /// <summary>The web presentation is open and connected to the model.</summary>
    Open,
    /// <summary>The web presentation failed to open; the host shows its fallback content.</summary>
    Failed,
}
