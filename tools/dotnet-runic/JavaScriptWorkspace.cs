using System;
using System.IO;
using System.Text.Json;

namespace Runic.Application.Tool;

/// <summary>
/// The JavaScript workspace a frontend belongs to, found as the Views targets find it (devtools-library-dx M1): a
/// frontend without a lock file of its own below a folder that declares a workspace (a <c>package.json</c> with
/// <c>workspaces</c>, or <c>pnpm-workspace.yaml</c>) installs and runs with that root's package manager. The nearest
/// such folder wins; <c>RunicApplicationFrontendWorkspaceRoot</c> names one (a folder, or <c>none</c>).
/// </summary>
internal static class JavaScriptWorkspace
{
    internal static readonly string[] LockFiles =
        ["bun.lock", "bun.lockb", "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml"];

    /// <summary>
    /// The workspace root of <paramref name="frontend"/>, or <paramref name="frontend"/> itself when it is its own root.
    /// <paramref name="explicitRoot"/> is <c>RunicApplicationFrontendWorkspaceRoot</c>, already a full path, or
    /// <c>none</c>.
    /// </summary>
    internal static string FindRoot(string frontend, string? explicitRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frontend);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(frontend));
        if (string.Equals(explicitRoot, "none", StringComparison.OrdinalIgnoreCase)) return full;
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return Path.TrimEndingDirectorySeparator(Path.GetFullPath(explicitRoot));
        if (HasLockFile(full)) return full;
        for (DirectoryInfo? folder = new DirectoryInfo(full).Parent; folder is not null; folder = folder.Parent)
        {
            if (DeclaresWorkspace(folder.FullName)) return Path.TrimEndingDirectorySeparator(folder.FullName);
        }
        return full;
    }

    /// <summary>Whether <paramref name="folder"/> holds a lock file of npm, pnpm or Bun.</summary>
    internal static bool HasLockFile(string folder) =>
        Array.Exists(LockFiles, name => File.Exists(Path.Combine(folder, name)));

    /// <summary>Whether <paramref name="folder"/> declares a workspace: pnpm-workspace.yaml, or package.json "workspaces".</summary>
    internal static bool DeclaresWorkspace(string folder) =>
        File.Exists(Path.Combine(folder, "pnpm-workspace.yaml")) || DeclaresPackageJsonWorkspaces(folder);

    /// <summary>Whether <paramref name="folder"/>'s package.json has <c>workspaces</c>.</summary>
    internal static bool DeclaresPackageJsonWorkspaces(string folder)
    {
        string manifest = Path.Combine(folder, "package.json");
        if (!File.Exists(manifest)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifest));
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("workspaces", out _);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
