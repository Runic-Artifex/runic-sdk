using System.Diagnostics.CodeAnalysis;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// An appearance override for a hosted region (the Runic host protocol, <c>region-appearance/1</c>): each member that is
/// not <see langword="null"/> replaces the host's desktop value for the pages of the region's window, such as a wizard
/// step in the design the user picked; <see langword="null"/> keeps the desktop's value.
/// </summary>
/// <remarks>
/// <para>
/// Set it with <see cref="RunicHostedRegionOptions.Appearance"/> when the policy embeds the window, or with
/// <see cref="RunicHostedRegion.SetAppearanceAsync"/> later. The guest merges it with the host's desktop appearance for
/// that window only: windows it owns, the guest's other windows and the host's own pages keep the desktop's. The host
/// page learns only <see cref="ColorScheme"/>, which it sets as the CSS <c>color-scheme</c> of the region's frame or
/// component.
/// </para>
/// <para>Experimental; see <c>docs/experimental.md</c>.</para>
/// </remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed record RunicHostedAppearance
{
    /// <summary>No override: every member keeps the desktop's value.</summary>
    public static RunicHostedAppearance None { get; } = new();

    /// <summary>
    /// <see cref="DesktopColorScheme.Light"/> or <see cref="DesktopColorScheme.Dark"/>; <see langword="null"/>, the
    /// default, for the desktop's. <see cref="DesktopColorScheme.NoPreference"/> reads as <see langword="null"/>, so a
    /// design menu's "System" choice maps to it directly.
    /// </summary>
    public DesktopColorScheme? ColorScheme
    {
        get;
        init => field = value is DesktopColorScheme.NoPreference ? null : value;
    }

    /// <summary>
    /// <see cref="DesktopContrast.NoPreference"/>, <see cref="DesktopContrast.More"/> or
    /// <see cref="DesktopContrast.ForcedColors"/>; <see langword="null"/>, the default, for the desktop's.
    /// </summary>
    public DesktopContrast? Contrast { get; init; }

    /// <summary>Whether the pages reduce motion; <see langword="null"/>, the default, for the desktop's.</summary>
    public bool? ReducedMotion { get; init; }

    /// <summary>The text size as a factor of the default, from 0.5 to 4; <see langword="null"/>, the default, for the desktop's.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not from 0.5 to 4.</exception>
    public double? TextScale
    {
        get;
        init => field = value is null or (>= HostServices.MinimumOverrideTextScale and <= HostServices.MaximumOverrideTextScale)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(TextScale), value,
                $"The text scale is not from {HostServices.MinimumOverrideTextScale} to {HostServices.MaximumOverrideTextScale}.");
    }

    /// <summary>The accent colour; <see langword="null"/>, the default, for the desktop's.</summary>
    public DesktopAccentColor? AccentColor { get; init; }

    /// <summary>
    /// The palette the pages show with <see cref="Contrast"/> <see cref="DesktopContrast.ForcedColors"/>; ignored with any
    /// other contrast. With forced colours and <see langword="null"/>, the desktop's palette when the desktop forces colours
    /// too, else none.
    /// </summary>
    public DesktopSystemColors? SystemColors { get; init; }

    // The override as the shared protocol code reads it: a DesktopAppearance whose unknown members keep the desktop's,
    // or null when every member is null.
    internal DesktopAppearance? ToDesktopAppearance()
    {
        var appearance = new DesktopAppearance(ColorScheme ?? DesktopColorScheme.NoPreference, AccentColor, ReducedMotion: ReducedMotion)
        {
            Contrast = Contrast,
            SystemColors = SystemColors,
            TextScale = TextScale,
        };
        return HostServices.IsUnknown(appearance) ? null : appearance;
    }

    // The colour scheme the host page sets on the region's frame or component: "light", "dark" or null.
    internal static string? SchemeToken(RunicHostedAppearance? appearance) => appearance?.ColorScheme switch
    {
        DesktopColorScheme.Light => "light",
        DesktopColorScheme.Dark => "dark",
        _ => null,
    };
}
