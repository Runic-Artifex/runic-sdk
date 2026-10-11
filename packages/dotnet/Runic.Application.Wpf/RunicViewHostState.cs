using System.Diagnostics.CodeAnalysis;

namespace Runic.Application.Views.Wpf;

/// <summary>The presentation state of a <c>RunicViewHost</c>.</summary>
[Experimental(WpfDiagnostics.Id, UrlFormat = WpfDiagnostics.UrlFormat)]
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
