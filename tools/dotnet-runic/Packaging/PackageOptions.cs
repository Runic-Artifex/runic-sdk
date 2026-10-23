using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Runic.Application.Tool.Packaging;

/// <summary>The targets of <c>dotnet runic package</c>.</summary>
internal enum PackageTarget
{
    Windows,
    MacOS,
    Flatpak,
    AppImage,
    Deb,
    Rpm,
    Flathub,
}

/// <summary>The options of one <c>dotnet runic package &lt;target&gt;</c> run.</summary>
internal sealed record PackageOptions(PackageTarget Target)
{
    internal string? Project { get; init; }
    internal string Configuration { get; init; } = "Release";

    /// <summary>The runtime identifiers to publish; several only for a universal macOS bundle. Empty selects the host's.</summary>
    internal IReadOnlyList<string> Runtimes { get; init; } = [];

    internal string? OutputDirectory { get; init; }

    // windows
    internal string? Publisher { get; init; }
    internal string? Certificate { get; init; }
    internal string? Thumbprint { get; init; }
    internal string? TimestampUrl { get; init; }

    // macos
    internal string? SigningIdentity { get; init; }
    internal string? NotaryProfile { get; init; }

    // flatpak
    internal string? GpgKey { get; init; }

    // flathub
    internal string? GitUrl { get; init; }
    internal string? GitCommit { get; init; }
    internal string? GitTag { get; init; }
    internal bool Build { get; init; }

    /// <summary>The target's name on the command line and in the JSON payload.</summary>
    internal static string Name(PackageTarget target) => target switch
    {
        PackageTarget.MacOS => "macos",
        PackageTarget.AppImage => "appimage",
        _ => target.ToString().ToLowerInvariant(),
    };

    /// <summary>The operating system family that builds the target: win, osx or linux; null for any.</summary>
    internal static string? HostFamily(PackageTarget target) => target switch
    {
        PackageTarget.Windows => "win",
        PackageTarget.MacOS => "osx",
        PackageTarget.Flathub => null,
        _ => "linux",
    };

    /// <summary>The RID family the target publishes for.</summary>
    internal static string RuntimeFamily(PackageTarget target) => target switch
    {
        PackageTarget.Windows => "win",
        PackageTarget.MacOS => "osx",
        _ => "linux",
    };

    internal static string CurrentFamily =>
        OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : "unknown";

    /// <summary>The host's architecture as a RID suffix.</summary>
    internal static string CurrentArchitecture => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    /// <summary>Splits <c>--runtime</c>, which takes a comma-separated list for a universal macOS bundle.</summary>
    internal static IReadOnlyList<string> ParseRuntimes(string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    internal static string FamilyName(string family) => family switch
    {
        "win" => "Windows",
        "osx" => "macOS",
        "linux" => "Linux",
        _ => family,
    };
}
