using System;

namespace WebUIToolkit.DependencyNotices.NuGet;

public sealed record NuGetInventoryOptions(
    string LockFilePath,
    string AssetsFilePath,
    string TargetFramework,
    string? RuntimeIdentifier = null,
    string? PackagesRoot = null)
{
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(LockFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(AssetsFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetFramework);
        if (RuntimeIdentifier is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(RuntimeIdentifier);
        }
    }
}
