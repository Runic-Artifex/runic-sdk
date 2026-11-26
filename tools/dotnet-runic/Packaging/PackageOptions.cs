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

    /// <summary>The runtime identifier to publish, as a list so that several are refused; empty selects the host's.</summary>
    internal IReadOnlyList<string> Runtimes { get; init; } = [];

    internal string? OutputDirectory { get; init; }

    /// <summary><c>--frontend-bundle</c>: the web bundle whose pages the publish takes (split CI F5), as given.</summary>
    internal string? FrontendBundle { get; init; }

    /// <summary><c>--property</c> and <c>-p:</c> values as given; <see cref="PackageApplication"/> parses them into <see cref="Properties"/>.</summary>
    internal IReadOnlyList<string> PropertyValues { get; init; } = [];

    /// <summary>The MSBuild properties that the evaluation and every publish of the target get (D082.5).</summary>
    internal IReadOnlyList<KeyValuePair<string, string>> Properties { get; init; } = [];

    /// <summary>The properties that package sets from its own options; <c>--property</c> cannot set them.</summary>
    internal static readonly string[] OptionProperties =
        ["Configuration", "RuntimeIdentifier", "PublishAot", "RunicApplicationFrontendPrebuiltDirectory"];

    internal static IReadOnlyList<KeyValuePair<string, string>> ParseProperties(IReadOnlyList<string> values) =>
        ToolOptionValues.ParseProperties(values, OptionProperties, PackageCodes.PropertyInvalid, PackageCodes.PropertyInvalid,
            name => name.Equals("RunicApplicationFrontendPrebuiltDirectory", StringComparison.OrdinalIgnoreCase)
                ? "--property cannot set RunicApplicationFrontendPrebuiltDirectory: use --frontend-bundle."
                : $"--property cannot set {name}: use --configuration and --runtime; package always publishes with Native AOT.");

    /// <summary>
    /// The <c>-property:</c> arguments of the evaluation and the publish: <see cref="Properties"/>, and the bundle's full
    /// path as <c>RunicApplicationFrontendPrebuiltDirectory</c> once <paramref name="frontendBundle"/> is resolved.
    /// </summary>
    internal IReadOnlyList<string> PropertyArguments(string? frontendBundle) =>
    [
        .. Properties.Select(ToolOptionValues.PropertyArgument),
        .. frontendBundle is null ? [] : new[] { ToolOptionValues.PropertyArgument(new("RunicApplicationFrontendPrebuiltDirectory", frontendBundle)) },
    ];

    // windows
    internal string? Publisher { get; init; }
    internal string? Certificate { get; init; }
    internal string? Thumbprint { get; init; }
    internal string? TimestampUrl { get; init; }

    // macos
    internal string? SigningIdentity { get; init; }
    internal string? NotaryProfile { get; init; }

    /// <summary><c>--sandbox</c>: sign with the App Sandbox entitlements, as <c>RunicMacSandbox</c> does.</summary>
    internal bool Sandbox { get; init; }

    /// <summary><c>--provisioning-profile</c>: the profile embedded as <c>Contents/embedded.provisionprofile</c>.</summary>
    internal string? ProvisioningProfile { get; init; }

    /// <summary><c>--pkg</c>: also write an installer package with productbuild, as App Store Connect takes.</summary>
    internal bool InstallerPackage { get; init; }

    /// <summary><c>--installer-sign</c>: the installer identity that signs the <c>.pkg</c>.</summary>
    internal string? InstallerSigningIdentity { get; init; }

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

    /// <summary>Splits <c>--runtime</c>; a comma-separated list, which universal macOS bundles took before 0.12, is refused.</summary>
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
