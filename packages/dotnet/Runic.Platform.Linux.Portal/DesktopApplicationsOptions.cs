using System.Collections.Immutable;

namespace Runic.Platform.Linux.Portal;

// Every input of the Linux application database, so tests point it at fixture trees and nothing reads the process
// environment after construction. FromProcess applies the XDG Base Directory defaults.
// https://specifications.freedesktop.org/basedir/latest/
internal sealed record DesktopApplicationsOptions
{
    internal required string HomeDirectory { get; init; }
    internal required string DataHome { get; init; }
    internal required ImmutableArray<string> DataDirectories { get; init; }
    internal required string ConfigHome { get; init; }
    internal required ImmutableArray<string> ConfigDirectories { get; init; }
    /// <summary>XDG_CURRENT_DESKTOP, in order, such as GNOME or KDE.</summary>
    internal ImmutableArray<string> CurrentDesktops { get; init; } = [];
    /// <summary>The directories of PATH, for Exec and TryExec programs named without a path.</summary>
    internal ImmutableArray<string> SearchPath { get; init; } = [];
    /// <summary>The LC_MESSAGES-style locale, such as de_DE.UTF-8@euro; empty for unlocalized values.</summary>
    internal string Locale { get; init; } = "";
    /// <summary>The icon theme to use instead of the desktop's.</summary>
    internal string? IconTheme { get; init; }
    /// <summary>Whether to ask the Settings portal for the GNOME icon theme.</summary>
    internal bool ReadPortalIconTheme { get; init; }
    /// <summary>The session bus address for the Settings portal and D-Bus activation; null for the session bus.</summary>
    internal string? BusAddress { get; init; }
    /// <summary>Whether the process runs in Flatpak or Snap, where the host's applications are out of reach.</summary>
    internal bool Sandboxed { get; init; }
    /// <summary>Unthemed icon directories searched last, such as /usr/share/pixmaps.</summary>
    internal ImmutableArray<string> PixmapDirectories { get; init; } = [];

    /// <summary>XDG_DATA_HOME, then XDG_DATA_DIRS: the order of precedence.</summary>
    internal ImmutableArray<string> DataSearch => [DataHome, .. DataDirectories];

    internal static DesktopApplicationsOptions FromProcess()
    {
        string home = Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h && Path.IsPathFullyQualified(h)
            ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new DesktopApplicationsOptions
        {
            HomeDirectory = home,
            DataHome = Directory("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share"),
            DataDirectories = Directories("XDG_DATA_DIRS", ["/usr/local/share", "/usr/share"]),
            ConfigHome = Directory("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"),
            ConfigDirectories = Directories("XDG_CONFIG_DIRS", ["/etc/xdg"]),
            CurrentDesktops = Split(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), relative: true),
            SearchPath = Split(Environment.GetEnvironmentVariable("PATH"), relative: false),
            Locale = DesktopLocale.FromProcess(),
            ReadPortalIconTheme = true,
            Sandboxed = File.Exists("/.flatpak-info") || Environment.GetEnvironmentVariable("SNAP") is not null,
            PixmapDirectories = ["/usr/share/pixmaps"],
        };
    }

    // A relative path in an XDG variable is invalid and ignored.
    private static string? Directory(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && Path.IsPathFullyQualified(value) ? value : null;

    private static ImmutableArray<string> Directories(string variable, ImmutableArray<string> defaults)
    {
        var values = Split(Environment.GetEnvironmentVariable(variable), relative: false);
        return values.IsEmpty ? defaults : values;
    }

    private static ImmutableArray<string> Split(string? value, bool relative) =>
        string.IsNullOrEmpty(value) ? [] :
        [.. value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(item => relative || Path.IsPathFullyQualified(item)).Distinct(StringComparer.Ordinal)];
}
