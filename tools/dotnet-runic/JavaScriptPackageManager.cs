using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Runic.Application.Tool;

internal sealed record JavaScriptPackageManager(
    string Name,
    string Executable,
    string LockFileName)
{
    /// <summary>
    /// The package manager of a frontend, as the Views targets choose it (devtools-library-dx §2.2):
    /// <paramref name="explicitName"/> (<c>RunicApplicationFrontendPackageManager</c>), else the workspace root's
    /// <c>packageManager</c>, then the root's lock file, then, for a member below the root, its own declaration and lock
    /// file, then npm.
    /// </summary>
    internal static JavaScriptPackageManager Resolve(
        string workspaceRoot,
        string packageDirectory,
        string? explicitName = null) =>
        ResolveWithSource(workspaceRoot, packageDirectory, explicitName).Manager;

    /// <summary><see cref="Resolve"/>, with where the choice came from, such as <c>packageManager in /repo/package.json</c>.</summary>
    internal static (JavaScriptPackageManager Manager, string Source) ResolveWithSource(
        string workspaceRoot,
        string packageDirectory,
        string? explicitName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return (FromName(explicitName.Trim()), "RunicApplicationFrontendPackageManager");
        }
        var folders = new List<string> { workspaceRoot };
        if (!string.IsNullOrWhiteSpace(packageDirectory) &&
            !StringComparer.Ordinal.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory))))
        {
            folders.Add(packageDirectory);
        }
        foreach (string folder in folders)
        {
            string manifest = Path.Combine(folder, "package.json");
            if (ReadDeclaration(manifest) is { } declared) return (FromName(declared), $"packageManager in {manifest}");
            if (InferFromLockFile(folder) is { } locked) return (FromName(locked.Name), locked.Path);
        }
        return (FromName("npm"), "the default (no packageManager and no lock file)");
    }

    /// <summary>The package manager called <paramref name="name"/>: npm, pnpm or bun.</summary>
    internal static JavaScriptPackageManager FromName(string name) =>
        name.ToLowerInvariant() switch
        {
            "npm" => new("npm", "npm", "package-lock.json"),
            "pnpm" => new("pnpm", "pnpm", "pnpm-lock.yaml"),
            "bun" => new("bun", "bun", "bun.lock"),
            _ => throw new DevDevelopmentException(
                "RAPPDEV1005",
                $"Unsupported JavaScript package manager '{name}'. Use npm, pnpm, or Bun."),
        };

    internal IReadOnlyList<string> InstallArguments() => Name switch
    {
        "npm" => ["ci", "--ignore-scripts"],
        "pnpm" => ["install", "--frozen-lockfile", "--ignore-scripts"],
        "bun" => ["install", "--frozen-lockfile", "--ignore-scripts"],
        _ => throw new InvalidOperationException($"Unsupported package manager '{Name}'."),
    };

    internal IReadOnlyList<string> RunScriptArguments(
        string script,
        string workspace,
        IReadOnlyList<string>? scriptArguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        scriptArguments ??= [];
        var arguments = new List<string>();
        switch (Name)
        {
            case "npm":
                arguments.AddRange(["run", script]);
                if (!string.IsNullOrWhiteSpace(workspace) && workspace != ".")
                {
                    arguments.AddRange(["--workspace", workspace]);
                }
                if (scriptArguments.Count != 0)
                {
                    arguments.Add("--");
                }
                break;
            case "pnpm":
                if (!string.IsNullOrWhiteSpace(workspace) && workspace != ".")
                {
                    arguments.AddRange(["--filter", workspace]);
                }
                arguments.AddRange(["run", script]);
                break;
            case "bun":
                arguments.AddRange(["run", "--bun"]);
                if (!string.IsNullOrWhiteSpace(workspace) && workspace != ".")
                {
                    arguments.AddRange(["--filter", workspace]);
                }
                arguments.Add(script);
                break;
            default:
                throw new InvalidOperationException($"Unsupported package manager '{Name}'.");
        }

        arguments.AddRange(scriptArguments);
        return arguments;
    }

    internal static string? ReadDeclaredVersion(string packageJson)
    {
        string? declaration = ReadRawDeclaration(packageJson);
        if (declaration is null)
        {
            return null;
        }

        int separator = declaration.LastIndexOf('@');
        return separator > 0 && separator < declaration.Length - 1
            ? declaration[(separator + 1)..]
            : null;
    }

    private static string? ReadDeclaration(string packageJson)
    {
        string? declaration = ReadRawDeclaration(packageJson);
        if (declaration is null)
        {
            return null;
        }

        int separator = declaration.LastIndexOf('@');
        return separator > 0 ? declaration[..separator] : declaration;
    }

    private static string? ReadRawDeclaration(string packageJson)
    {
        if (!File.Exists(packageJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(packageJson));
            return document.RootElement.TryGetProperty("packageManager", out JsonElement value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string Name, string Path)? InferFromLockFile(string folder)
    {
        foreach ((string file, string name) in (ReadOnlySpan<(string, string)>)[
            ("pnpm-lock.yaml", "pnpm"), ("bun.lock", "bun"), ("bun.lockb", "bun"), ("package-lock.json", "npm"), ("npm-shrinkwrap.json", "npm")])
        {
            string path = System.IO.Path.Combine(folder, file);
            if (File.Exists(path)) return (name, path);
        }
        return null;
    }
}
