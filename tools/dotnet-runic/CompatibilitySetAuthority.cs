using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Runic.Application.Tool;

internal sealed record CompatibilityPackage(string Ecosystem, string Identity, string Version);

internal sealed record CompatibilityToolchain(
    string DotNetSdk,
    string Node,
    string Bun,
    string Npm,
    string Pnpm);

/// <summary>How well Runic supports a Window host on a runtime identifier (eng/support.json).</summary>
internal enum SupportStatus
{
    Unsupported,
    PackagedUnverified,
    CiVerified,
}

internal sealed record SupportTarget(string Rid, SupportStatus Status, string Reason, string? Remediation);

internal sealed record SupportHost(string Id, string Name, string Package, IReadOnlyList<SupportTarget> Targets);

internal sealed record SupportRequirement(string Id, string Os, IReadOnlyList<string> Hosts, string Component, string? Minimum, string Note);

/// <summary>The support matrix embedded in the compatibility set since schema 2.</summary>
internal sealed class SupportMatrix(
    IReadOnlyList<SupportHost> hosts, IReadOnlyList<SupportRequirement> requirements, string ciVerifiedMeaning)
{
    /// <summary>What ci-verified means, for example which CI coverage it stands for.</summary>
    internal string CiVerifiedMeaning { get; } = ciVerifiedMeaning;

    internal IReadOnlyList<SupportHost> Hosts { get; } = hosts;

    internal IReadOnlyList<SupportRequirement> Requirements { get; } = requirements;

    internal SupportRequirement? Requirement(string id) =>
        Requirements.FirstOrDefault(requirement => requirement.Id == id);

    internal static SupportMatrix Parse(JsonElement support)
    {
        var hosts = new List<SupportHost>();
        foreach (JsonElement host in support.GetProperty("hosts").EnumerateArray())
        {
            var targets = new List<SupportTarget>();
            foreach (JsonElement target in host.GetProperty("targets").EnumerateArray())
            {
                targets.Add(new SupportTarget(
                    target.GetProperty("rid").GetString() ?? string.Empty,
                    ParseStatus(target.GetProperty("status").GetString()),
                    target.GetProperty("reason").GetString() ?? string.Empty,
                    target.TryGetProperty("remediation", out JsonElement remediation) ? remediation.GetString() : null));
            }
            hosts.Add(new SupportHost(
                host.GetProperty("id").GetString() ?? string.Empty,
                host.GetProperty("name").GetString() ?? string.Empty,
                host.GetProperty("package").GetString() ?? string.Empty,
                targets));
        }

        var requirements = new List<SupportRequirement>();
        foreach (JsonElement requirement in support.GetProperty("requirements").EnumerateArray())
        {
            requirements.Add(new SupportRequirement(
                requirement.GetProperty("id").GetString() ?? string.Empty,
                requirement.GetProperty("os").GetString() ?? string.Empty,
                [.. requirement.GetProperty("hosts").EnumerateArray().Select(host => host.GetString() ?? string.Empty)],
                requirement.GetProperty("component").GetString() ?? string.Empty,
                requirement.GetProperty("minimum").GetString(),
                requirement.GetProperty("note").GetString() ?? string.Empty));
        }
        return new SupportMatrix(hosts, requirements,
            support.GetProperty("statuses").GetProperty("ci-verified").GetString() ?? string.Empty);
    }

    private static SupportStatus ParseStatus(string? value) =>
        value switch
        {
            "ci-verified" => SupportStatus.CiVerified,
            "packaged-unverified" => SupportStatus.PackagedUnverified,
            "unsupported" => SupportStatus.Unsupported,
            _ => throw new InvalidOperationException($"Unknown support status '{value}' in the embedded compatibility set."),
        };
}

internal sealed class CompatibilitySetAuthority
{
    // Generated from the current SDK inventory and maintained toolchain pins.
    // This offline diagnostic snapshot is not a publication or source-provenance receipt.
    private const string ResourceName = "Runic.Application.Tool.runic.compatibility-set.json";

    private CompatibilitySetAuthority(
        string id,
        string releaseTrainVersion,
        CompatibilityToolchain toolchain,
        FrozenDictionary<string, CompatibilityPackage> nugetPackages,
        FrozenDictionary<string, CompatibilityPackage> npmPackages,
        SupportMatrix support)
    {
        Id = id;
        ReleaseTrainVersion = releaseTrainVersion;
        Toolchain = toolchain;
        NuGetPackages = nugetPackages;
        NpmPackages = npmPackages;
        Support = support;
    }

    internal static CompatibilitySetAuthority Current { get; } = Load();

    internal string Id { get; }

    internal string ReleaseTrainVersion { get; }

    internal CompatibilityToolchain Toolchain { get; }

    internal IReadOnlyDictionary<string, CompatibilityPackage> NuGetPackages { get; }

    internal IReadOnlyDictionary<string, CompatibilityPackage> NpmPackages { get; }

    internal SupportMatrix Support { get; }

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
                toolchain.GetProperty("bun").GetString() ?? string.Empty,
                toolchain.GetProperty("npm").GetString() ?? string.Empty,
                toolchain.GetProperty("pnpm").GetString() ?? string.Empty),
            nuget.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            npm.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            SupportMatrix.Parse(root.GetProperty("support")));
    }
}
