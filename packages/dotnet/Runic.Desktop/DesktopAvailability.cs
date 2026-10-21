namespace Runic.Desktop;

/// <summary>Describes whether one concrete presentation host can be used by the current process.</summary>
/// <param name="Browser">The presentation: an installed browser or <see cref="BrowserKind.Embedded"/>.</param>
/// <param name="IsAvailable">Whether the presentation can open.</param>
/// <param name="ExecutablePath">The browser executable, or <see langword="null"/> for the embedded WebView or a missing browser.</param>
/// <param name="Capabilities">The window operations the presentation supports.</param>
/// <param name="Diagnostics">Every missing prerequisite of an unavailable presentation; empty when it is available.</param>
public sealed record DesktopPresentationAvailability(
    BrowserKind Browser,
    bool IsAvailable,
    string? ExecutablePath,
    DesktopWindowCapabilities Capabilities,
    IReadOnlyList<DesktopDiagnostic> Diagnostics)
{
    private readonly DesktopDiagnostic[] _diagnostics = Copy(Diagnostics);

    /// <summary>Gets every missing prerequisite of an unavailable presentation, in the order a user can act on them.</summary>
    /// <remarks>
    /// For example, a GTK4 selection without its provider reports the provider and each missing native library. The
    /// list is empty when the presentation is available.
    /// </remarks>
    public IReadOnlyList<DesktopDiagnostic> Diagnostics
    {
        // A read-only view: callers cannot cast it back to the array and change the record.
        get => Array.AsReadOnly(_diagnostics);
        init => _diagnostics = Copy(value);
    }

    private static DesktopDiagnostic[] Copy(IReadOnlyList<DesktopDiagnostic> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        DesktopDiagnostic[] diagnostics = [.. value];
        if (Array.IndexOf(diagnostics, null) >= 0) throw new ArgumentException("Diagnostics cannot contain null.", nameof(value));
        return diagnostics;
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

    /// <summary>
    /// Gets every missing prerequisite of the preferred presentation and its declared fallback when no declared
    /// presentation path is available, the preferred presentation's first; empty when one is available.
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
/// <param name="Platform">The operating system: <c>windows</c>, <c>macos</c>, <c>linux</c> or <c>unsupported</c>.</param>
/// <param name="Presentations">
/// One entry per presentation, matched by <see cref="DesktopPresentationAvailability.Browser"/>; the list can grow.
/// </param>
/// <remarks>
/// <see cref="DesktopPlatform.GetAvailability"/> creates it. Two results are equal when their members are, with the
/// lists compared by value and order.
/// </remarks>
public sealed record DesktopAvailabilityResult(
    string Platform,
    IReadOnlyList<DesktopPresentationAvailability> Presentations)
{
    private readonly DesktopPresentationAvailability[] _presentations = Copy(Presentations, nameof(Presentations));
    private readonly LinuxEmbeddedBackendAvailability[] _linuxBackends = [];

    /// <summary>Gets one entry per presentation, matched by <see cref="DesktopPresentationAvailability.Browser"/>; the list can grow.</summary>
    public IReadOnlyList<DesktopPresentationAvailability> Presentations
    {
        get => Array.AsReadOnly(_presentations);
        init => _presentations = Copy(value, nameof(Presentations));
    }

    /// <summary>
    /// Gets the GTK 3 and GTK 4 library discovery on Linux, to choose <see cref="LinuxDesktopOptions.EmbeddedBackend"/>;
    /// empty on other platforms. It does not depend on the options passed to <see cref="DesktopPlatform.GetAvailability"/>.
    /// </summary>
    public IReadOnlyList<LinuxEmbeddedBackendAvailability> LinuxBackends
    {
        get => Array.AsReadOnly(_linuxBackends);
        init => _linuxBackends = Copy(value, nameof(LinuxBackends));
    }

    /// <summary>Gets whether at least one presentation host is available.</summary>
    public bool IsReady => _presentations.Any(static presentation => presentation.IsAvailable);

    /// <summary>Gets every actionable missing-prerequisite diagnostic.</summary>
    public IReadOnlyList<DesktopDiagnostic> Diagnostics => _presentations
        .SelectMany(static presentation => presentation.Diagnostics)
        .ToArray();

    /// <summary>Gets the embedded WebView presentation: the configured window host's, or the built-in one.</summary>
    /// <remarks>The same as <c>GetPresentation(BrowserKind.Embedded)</c>.</remarks>
    public DesktopPresentationAvailability Embedded => GetPresentation(BrowserKind.Embedded);

    /// <summary>Gets the presentation a window requesting <paramref name="browser"/> would use.</summary>
    /// <param name="browser">The requested presentation, as in <see cref="DesktopWindowOptions.Browser"/>.</param>
    /// <returns>
    /// The entry of <see cref="BrowserKind.Embedded"/> or a concrete browser. For <see cref="BrowserKind.Any"/>, the first
    /// available installed browser; for <see cref="BrowserKind.ChromiumBased"/>, the first available Chromium-based one.
    /// When there is none, an unavailable entry for <paramref name="browser"/>: <c>browser-unsupported</c> for Safari and
    /// Opera, which Runic Desktop does not launch, otherwise <c>presentation-unavailable</c>. Never <see langword="null"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="browser"/> is not a defined value.</exception>
    public DesktopPresentationAvailability GetPresentation(BrowserKind browser)
    {
        if (!Enum.IsDefined(browser)) throw new ArgumentOutOfRangeException(nameof(browser), browser, "The browser kind is not defined.");
        DesktopPresentationAvailability? found = browser switch
        {
            BrowserKind.Any => Array.Find(_presentations, static candidate =>
                candidate.Browser != BrowserKind.Embedded && candidate.IsAvailable),
            BrowserKind.ChromiumBased => Array.Find(_presentations, static candidate =>
                candidate.IsAvailable && candidate.Browser is BrowserKind.Chrome or BrowserKind.Edge or
                    BrowserKind.Chromium or BrowserKind.Brave or BrowserKind.Vivaldi or BrowserKind.Epic or
                    BrowserKind.Yandex),
            _ => Array.Find(_presentations, candidate => candidate.Browser == browser),
        };
        return found ?? DesktopHost.Unavailable(browser);
    }

    /// <summary>Compares every member, and the lists by value and order.</summary>
    public bool Equals(DesktopAvailabilityResult? other) =>
        ReferenceEquals(this, other) ||
        other is not null &&
        string.Equals(Platform, other.Platform, StringComparison.Ordinal) &&
        _presentations.AsSpan().SequenceEqual(other._presentations) &&
        _linuxBackends.AsSpan().SequenceEqual(other._linuxBackends);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Platform, StringComparer.Ordinal);
        foreach (var presentation in _presentations) hash.Add(presentation);
        foreach (var backend in _linuxBackends) hash.Add(backend);
        return hash.ToHashCode();
    }

    private static T[] Copy<T>(IReadOnlyList<T> value, string name) where T : class
    {
        ArgumentNullException.ThrowIfNull(value, name);
        T[] items = [.. value];
        if (Array.IndexOf(items, null) >= 0) throw new ArgumentException($"{name} cannot contain null.", name);
        return items;
    }
}

/// <summary>Reports native library discovery for one Linux toolkit without loading it.</summary>
/// <param name="Backend">The toolkit.</param>
/// <param name="LibrariesDiscovered">Whether its GTK and WebKitGTK libraries are in the native loader search path.</param>
/// <param name="Diagnostics">What is missing; empty when the libraries were discovered.</param>
/// <remarks>
/// Library discovery is not proof of a compatible ABI, graphical session, or registered provider. Two entries are
/// equal when their members are, with the diagnostics compared by value and order.
/// </remarks>
public sealed record LinuxEmbeddedBackendAvailability(
    LinuxEmbeddedBackend Backend,
    bool LibrariesDiscovered,
    IReadOnlyList<DesktopDiagnostic> Diagnostics)
{
    private readonly DesktopDiagnostic[] _diagnostics = Copy(Diagnostics);

    /// <summary>Gets what is missing; empty when the libraries were discovered.</summary>
    public IReadOnlyList<DesktopDiagnostic> Diagnostics
    {
        get => Array.AsReadOnly(_diagnostics);
        init => _diagnostics = Copy(value);
    }

    private static DesktopDiagnostic[] Copy(IReadOnlyList<DesktopDiagnostic> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        DesktopDiagnostic[] diagnostics = [.. value];
        if (Array.IndexOf(diagnostics, null) >= 0) throw new ArgumentException("Diagnostics cannot contain null.", nameof(value));
        return diagnostics;
    }

    /// <summary>Compares every member, and the diagnostics by value and order.</summary>
    public bool Equals(LinuxEmbeddedBackendAvailability? other) =>
        ReferenceEquals(this, other) ||
        other is not null &&
        Backend == other.Backend &&
        LibrariesDiscovered == other.LibrariesDiscovered &&
        _diagnostics.AsSpan().SequenceEqual(other._diagnostics);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Backend);
        hash.Add(LibrariesDiscovered);
        foreach (var diagnostic in _diagnostics) hash.Add(diagnostic);
        return hash.ToHashCode();
    }
}
