namespace Runic.Desktop;

/// <summary>Describes whether one concrete presentation host can be used by the current process.</summary>
public sealed record DesktopPresentationAvailability(
    BrowserKind Browser,
    bool IsAvailable,
    string? ExecutablePath,
    DesktopWindowCapabilities Capabilities,
    DesktopDiagnostic? Diagnostic);

/// <summary>Describes whether a requested presentation can be opened without changing its declared fallback policy.</summary>
public sealed record DesktopPresentationPreflight(
    BrowserKind RequestedBrowser,
    DesktopPresentationPolicy PresentationPolicy,
    DesktopPresentationAvailability Preferred,
    DesktopPresentationAvailability? Fallback)
{
    /// <summary>Gets whether the preferred presentation is available without fallback.</summary>
    public bool IsPreferredAvailable => Preferred.IsAvailable;

    /// <summary>Gets whether the declared policy has an available presentation path.</summary>
    public bool IsAvailable => Preferred.IsAvailable || Fallback?.IsAvailable == true;

    /// <summary>Gets whether opening the request would use its declared fallback.</summary>
    public bool WouldFallBack => !Preferred.IsAvailable && Fallback?.IsAvailable == true;

    /// <summary>Gets the safe actionable diagnostic when no declared presentation path is available.</summary>
    public DesktopDiagnostic? Diagnostic => IsAvailable
        ? null
        : Preferred.Diagnostic ?? Fallback?.Diagnostic;
}

/// <summary>Reports the presentation hosts and prerequisites visible to the current process.</summary>
public sealed record DesktopAvailabilityResult(
    string Platform,
    IReadOnlyList<DesktopPresentationAvailability> Presentations)
{
    /// <summary>Gets whether at least one presentation host is available.</summary>
    public bool IsReady => Presentations.Any(static presentation => presentation.IsAvailable);

    /// <summary>Gets every actionable missing-prerequisite diagnostic.</summary>
    public IReadOnlyList<DesktopDiagnostic> Diagnostics => Presentations
        .Where(static presentation => presentation.Diagnostic is not null)
        .Select(static presentation => presentation.Diagnostic!)
        .ToArray();
}
