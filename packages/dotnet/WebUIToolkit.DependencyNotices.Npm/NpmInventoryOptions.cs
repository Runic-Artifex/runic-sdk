using System;

namespace WebUIToolkit.DependencyNotices.Npm;

public enum NpmInventoryProfile
{
    Runtime,
    Development,
}

public sealed record NpmInventoryOptions
{
    public NpmInventoryOptions(
        string rootDirectory,
        string lockFileRelativePath,
        string workspaceRelativePath,
        NpmInventoryProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFileRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRelativePath);

        RootDirectory = rootDirectory;
        LockFileRelativePath = lockFileRelativePath;
        WorkspaceRelativePath = workspaceRelativePath;
        Profile = profile;
    }

    public string RootDirectory { get; }

    public string LockFileRelativePath { get; }

    public string WorkspaceRelativePath { get; }

    public NpmInventoryProfile Profile { get; }
}
