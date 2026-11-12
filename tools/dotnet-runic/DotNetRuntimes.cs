using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Runic.Application.Tool;

/// <summary>A shared framework that <c>dotnet --list-runtimes</c> reports, such as Microsoft.NETCore.App 10.0.14.</summary>
internal sealed record InstalledRuntime(string Name, string Version);

/// <summary>A shared framework that an application's runtimeconfig.json asks for.</summary>
internal sealed record RequiredRuntime(string Name, string Version);

/// <summary>
/// Whether the .NET runtimes an application needs are installed. With a newer-major SDK, such as SDK 11 for a net10.0
/// application, the build succeeds but the application exits with code 150 unless the target's runtime is installed
/// too (D059.17).
/// </summary>
internal static class DotNetRuntimes
{
    internal const string NetCoreApp = "Microsoft.NETCore.App";
    internal const string AspNetCoreApp = "Microsoft.AspNetCore.App";

    internal static IReadOnlyList<string> ListArguments { get; } = ["--list-runtimes"];

    /// <summary>Reads lines such as <c>Microsoft.NETCore.App 10.0.14 [/usr/share/dotnet/shared/Microsoft.NETCore.App]</c>.</summary>
    internal static IReadOnlyList<InstalledRuntime> Parse(string output)
    {
        var runtimes = new List<InstalledRuntime>();
        foreach (string line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) runtimes.Add(new InstalledRuntime(parts[0], parts[1]));
        }
        return runtimes;
    }

    /// <summary>
    /// Whether an installed runtime satisfies <paramref name="required"/> under the host's roll-forward policy:
    /// <c>Disable</c> needs the exact version, <c>LatestPatch</c> a newer patch, <c>Major</c> and <c>LatestMajor</c> any
    /// newer version, and the default (<c>Minor</c>, as for <c>LatestMinor</c>) a newer one of the same major version.
    /// </summary>
    internal static bool IsSatisfied(IReadOnlyList<InstalledRuntime> installed, RequiredRuntime required, string? rollForward)
    {
        if (!TryParse(required.Version, out Version? requested)) return true;
        string policy = string.IsNullOrWhiteSpace(rollForward) ? "Minor" : rollForward.Trim();
        return installed.Any(runtime =>
            StringComparer.OrdinalIgnoreCase.Equals(runtime.Name, required.Name) &&
            TryParse(runtime.Version, out Version? version) &&
            policy.ToUpperInvariant() switch
            {
                "DISABLE" => StringComparer.OrdinalIgnoreCase.Equals(runtime.Version, required.Version),
                "MAJOR" or "LATESTMAJOR" => version >= requested,
                "LATESTPATCH" => version.Major == requested.Major && version.Minor == requested.Minor && version >= requested,
                _ => version.Major == requested.Major && version >= requested,
            });
    }

    /// <summary>
    /// The shared frameworks and roll-forward policy of a framework-dependent application's runtimeconfig.json, or
    /// null when the file is missing, unreadable or describes a self-contained application.
    /// </summary>
    internal static (IReadOnlyList<RequiredRuntime> Frameworks, string? RollForward)? ReadRuntimeConfig(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!document.RootElement.TryGetProperty("runtimeOptions", out JsonElement options)) return null;
            var frameworks = new List<RequiredRuntime>();
            if (options.TryGetProperty("framework", out JsonElement single)) frameworks.Add(ReadFramework(single));
            if (options.TryGetProperty("frameworks", out JsonElement many) && many.ValueKind == JsonValueKind.Array)
                frameworks.AddRange(many.EnumerateArray().Select(ReadFramework));
            frameworks.RemoveAll(static framework => framework.Name.Length == 0);
            if (frameworks.Count == 0) return null;
            string? rollForward = options.TryGetProperty("rollForward", out JsonElement value) ? value.GetString() : null;
            return (frameworks, rollForward);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }

        static RequiredRuntime ReadFramework(JsonElement framework) => new(
            framework.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty,
            framework.TryGetProperty("version", out JsonElement version) ? version.GetString() ?? string.Empty : string.Empty);
    }

    /// <summary>The runtimes of <paramref name="name"/> that are installed, for a message.</summary>
    internal static string Describe(IReadOnlyList<InstalledRuntime> installed, string name)
    {
        string[] versions = [.. installed.Where(runtime => StringComparer.OrdinalIgnoreCase.Equals(runtime.Name, name)).Select(static runtime => runtime.Version)];
        return versions.Length == 0 ? $"no {name} runtime is installed" : $"installed {name} runtimes: {string.Join(", ", versions)}";
    }

    private static bool TryParse(string value, [NotNullWhen(true)] out Version? version)
    {
        int separator = value.IndexOfAny(['-', '+']);
        return Version.TryParse(separator > 0 ? value[..separator] : value, out version);
    }
}
