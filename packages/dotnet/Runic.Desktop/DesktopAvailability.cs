namespace Runic.Desktop;

/// <summary>Describes whether one concrete presentation host can be used by the current process.</summary>
public sealed record DesktopPresentationAvailability(
    BrowserKind Browser,
    bool IsAvailable,
    string? ExecutablePath,
    DesktopWindowCapabilities Capabilities,
    DesktopDiagnostic? Diagnostic)
{
    private readonly DesktopDiagnostic[] _diagnostics = Diagnostic is null ? [] : [Diagnostic];

    /// <summary>Gets the first missing prerequisite of an unavailable presentation, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Always the first entry of <see cref="Diagnostics"/>. Setting it, including through a <see langword="with"/>
    /// expression, replaces <see cref="Diagnostics"/> with this diagnostic alone, or with none.
    /// </remarks>
    public DesktopDiagnostic? Diagnostic
    {
        get => _diagnostics.Length == 0 ? null : _diagnostics[0];
        init => _diagnostics = value is null ? [] : [value];
    }

    /// <summary>Gets every missing prerequisite of an unavailable presentation, not only the first.</summary>
    /// <remarks>
    /// For example, a GTK4 selection without its provider reports the provider and each missing native library.
    /// <see cref="Diagnostic"/> is the first entry; setting this list after it replaces it.
    /// </remarks>
    public IReadOnlyList<DesktopDiagnostic> Diagnostics
    {
        get => _diagnostics;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            DesktopDiagnostic[] diagnostics = [.. value];
            if (Array.IndexOf(diagnostics, null) >= 0) throw new ArgumentException("Diagnostics cannot contain null.", nameof(value));
            _diagnostics = diagnostics;
        }
    }

    /// <summary>Compares every member, and the diagnostics by value and order.</summary>
    public bool Equals(DesktopPresentationAvailability? other) =>
        ReferenceEquals(this, other) ||
        other is not null &&
        Browser == other.Browser &&
        IsAvailable == other.IsAvailable &&
        string.Equals(ExecutablePath, other.ExecutablePath, StringComparison.Ordinal) &&
        Capabilities == other.Capabilities &&
        _diagnostics.AsSpan().SequenceEqual(other._diagnostics);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Browser);
        hash.Add(IsAvailable);
        hash.Add(ExecutablePath, StringComparer.Ordinal);
        hash.Add(Capabilities);
        foreach (var diagnostic in _diagnostics) hash.Add(diagnostic);
        return hash.ToHashCode();
    }
}

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

    /// <summary>
    /// Gets every missing prerequisite of the preferred presentation and its declared fallback when no declared
    /// presentation path is available. <see cref="Diagnostic"/> is the first entry.
    /// </summary>
    public IReadOnlyList<DesktopDiagnostic> Diagnostics => IsAvailable
        ? []
        : Preferred.Diagnostics.Concat(Fallback?.Diagnostics ?? []).Distinct().ToArray();

    /// <summary>
    /// Gets one diagnostic per requested window option or permission grant that the preferred presentation, or its
    /// declared fallback, rejects (<see cref="DesktopDiagnosticSeverity.Error"/>) or ignores
    /// (<see cref="DesktopDiagnosticSeverity.Warning"/>). <see cref="DesktopDiagnostic.Option"/> names the option.
    /// </summary>
    public IReadOnlyList<DesktopDiagnostic> OptionDiagnostics { get; init; } = [];
}

/// <summary>Collects every configuration check run before a Desktop window opens.</summary>
public sealed record DesktopValidationResult(IReadOnlyList<DesktopDiagnostic> Diagnostics)
{
    /// <summary>Gets whether no check failed. Warnings do not make a configuration invalid.</summary>
    public bool IsValid => !Diagnostics.Any(static diagnostic => diagnostic.Severity == DesktopDiagnosticSeverity.Error);

    /// <summary>Gets the checks that fail the configuration.</summary>
    public IReadOnlyList<DesktopDiagnostic> Errors => Diagnostics
        .Where(static diagnostic => diagnostic.Severity == DesktopDiagnosticSeverity.Error)
        .ToArray();

    /// <summary>Gets the options the presentation will ignore or narrow.</summary>
    public IReadOnlyList<DesktopDiagnostic> Warnings => Diagnostics
        .Where(static diagnostic => diagnostic.Severity == DesktopDiagnosticSeverity.Warning)
        .ToArray();

    /// <summary>Throws a <see cref="DesktopConfigurationException"/> listing every error, if any check failed.</summary>
    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new DesktopConfigurationException(Errors);
        }
    }
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
        .SelectMany(static presentation => presentation.Diagnostics)
        .ToArray();
}

/// <summary>Reports native library discovery for one Linux toolkit without loading it.</summary>
/// <remarks>Library discovery is not proof of a compatible ABI, graphical session, or registered provider.</remarks>
public sealed record LinuxEmbeddedBackendAvailability(
    LinuxEmbeddedBackend Backend,
    bool LibrariesDiscovered,
    DesktopDiagnostic? Diagnostic);
