using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Runic.Application.Tool;

internal sealed record CompatibilityPackage(string Ecosystem, string Identity, string Version);

internal sealed record CompatibilityToolchain(string DotNetSdk, string Node, string Npm);

internal sealed class CompatibilitySetAuthority
{
    private const string ResourceName = "Runic.Application.Tool.runic.compatibility-set.json";

    private CompatibilitySetAuthority(
        string id,
        string releaseTrainVersion,
        CompatibilityToolchain toolchain,
        FrozenDictionary<string, CompatibilityPackage> nugetPackages,
        FrozenDictionary<string, CompatibilityPackage> npmPackages)
    {
        Id = id;
        ReleaseTrainVersion = releaseTrainVersion;
        Toolchain = toolchain;
        NuGetPackages = nugetPackages;
        NpmPackages = npmPackages;
    }

    internal static CompatibilitySetAuthority Current { get; } = Load();

    internal string Id { get; }

    internal string ReleaseTrainVersion { get; }

    internal CompatibilityToolchain Toolchain { get; }

    internal IReadOnlyDictionary<string, CompatibilityPackage> NuGetPackages { get; }

    internal IReadOnlyDictionary<string, CompatibilityPackage> NpmPackages { get; }

    private static CompatibilitySetAuthority Load()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded compatibility authority '{ResourceName}' is missing.");
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        JsonElement toolchain = root.GetProperty("toolchain");
        var nuget = new Dictionary<string, CompatibilityPackage>(StringComparer.OrdinalIgnoreCase);
        var npm = new Dictionary<string, CompatibilityPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement package in root.GetProperty("packages").EnumerateArray())
        {
            var item = new CompatibilityPackage(
                package.GetProperty("ecosystem").GetString() ?? string.Empty,
                package.GetProperty("identity").GetString() ?? string.Empty,
                package.GetProperty("version").GetString() ?? string.Empty);
            (item.Ecosystem == "nuget" ? nuget : npm).Add(item.Identity, item);
        }

        return new CompatibilitySetAuthority(
            root.GetProperty("id").GetString() ?? string.Empty,
            root.GetProperty("releaseTrainVersion").GetString() ?? string.Empty,
            new CompatibilityToolchain(
                toolchain.GetProperty("dotnetSdk").GetString() ?? string.Empty,
                toolchain.GetProperty("node").GetString() ?? string.Empty,
                toolchain.GetProperty("npm").GetString() ?? string.Empty),
            nuget.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            npm.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }
}
