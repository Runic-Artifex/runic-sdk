using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Runic.Application.Tool;

// Private plumbing for the experimental post-MVVM fixture. A selection key
// chooses a contract; this value owns one `dotnet runic dev` build session.
internal sealed class DiscoveryBuildSession
{
    internal const string OwnerProperty = "RunicPostMvvmDiscoveryBuildOwner";
    internal const string OwnerDriverProperty = "RunicPostMvvmDiscoveryOwnerDriver";

    internal DiscoveryBuildSession()
        : this(Guid.NewGuid().ToString("N"))
    {
    }

    internal DiscoveryBuildSession(string owner)
    {
        if (owner.Length != 32 ||
            !System.Text.RegularExpressions.Regex.IsMatch(owner, "^[a-f0-9]{32}$"))
        {
            throw new ArgumentException(
                "A discovery build owner must be a 32-character lowercase hexadecimal GUID.",
                nameof(owner));
        }

        Owner = owner;
    }

    internal string Owner { get; }

    internal void AddMsBuildProperties(
        System.Collections.Generic.ICollection<string> arguments,
        string prefix)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        arguments.Add($"{prefix}{OwnerProperty}={Owner}");
        arguments.Add($"{prefix}{OwnerDriverProperty}=true");
    }

    internal static void AddMsBuildProperties(
        System.Collections.Generic.ICollection<string> arguments,
        string prefix,
        string owner)
    {
        new DiscoveryBuildSession(owner).AddMsBuildProperties(arguments, prefix);
    }

    internal static DiscoveryBuildSession? CreateFor(DevProjectConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.UsesPostMvvmDiscovery ? new DiscoveryBuildSession() : null;
    }

    // Called after the development loop has disposed its host and frontend
    // processes. The evaluated MSBuild path is evidence, not authority: only
    // this fixture's exact owner directory may be removed.
    internal static bool CleanupOwnedOutputs(DevProjectConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.UsesPostMvvmDiscovery ||
            !Regex.IsMatch(configuration.DiscoveryBuildOwner, "^[a-f0-9]{32}$") ||
            !Regex.IsMatch(configuration.DiscoveryOutputKey, "^[A-Za-z0-9_-]+$") ||
            string.IsNullOrWhiteSpace(configuration.DiscoverySdkRoot) ||
            string.IsNullOrWhiteSpace(configuration.DiscoveryOwnerRoot))
        {
            return false;
        }

        string sdkRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(configuration.DiscoverySdkRoot));
        string evaluatedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(configuration.DiscoveryOwnerRoot));
        string expectedRoot = Path.GetFullPath(Path.Combine(
            sdkRoot, "obj", "pmd", configuration.DiscoveryOutputKey,
            configuration.DiscoveryBuildOwner));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(evaluatedRoot, expectedRoot, comparison) ||
            !Directory.Exists(evaluatedRoot))
        {
            return false;
        }

        // Reject junctions and symlinks in the path, including the SDK root.
        // A linked ancestor could make an otherwise exact textual path point
        // outside the fixture namespace.
        for (DirectoryInfo? part = new(evaluatedRoot); part is not null; part = part.Parent)
        {
            if ((part.Attributes & FileAttributes.ReparsePoint) != 0)
                return false;
        }

        Directory.Delete(evaluatedRoot, recursive: true);
        return true;
    }
}
