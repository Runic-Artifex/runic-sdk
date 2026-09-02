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
    internal static JavaScriptPackageManager Resolve(
        string workspaceRoot,
        string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        string? declared = ReadDeclaration(Path.Combine(workspaceRoot, "package.json"));
        if (declared is null && !StringComparer.Ordinal.Equals(workspaceRoot, packageDirectory))
        {
            declared = ReadDeclaration(Path.Combine(packageDirectory, "package.json"));
        }

        string name = declared ?? InferFromLockFile(workspaceRoot) ?? "npm";
        return name.ToLowerInvariant() switch
        {
            "npm" => new("npm", "npm", "package-lock.json"),
            "pnpm" => new("pnpm", "pnpm", "pnpm-lock.yaml"),
            "bun" => new("bun", "bun", "bun.lock"),
            _ => throw new DevUsageException(
                "RTKDEV1005",
                $"Unsupported JavaScript package manager '{name}'. Use npm, pnpm, or Bun."),
        };
    }

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
                arguments.Add("run");
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

    private static string? InferFromLockFile(string workspaceRoot) =>
        File.Exists(Path.Combine(workspaceRoot, "pnpm-lock.yaml"))
            ? "pnpm"
            : File.Exists(Path.Combine(workspaceRoot, "bun.lock"))
                ? "bun"
                : File.Exists(Path.Combine(workspaceRoot, "package-lock.json"))
                    ? "npm"
                    : null;
}
