using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.CommandLine;

namespace Runic.Application.Tool.Packaging;

/// <summary>A NuGet package the offline build needs.</summary>
internal sealed record FlathubNuGetPackage(string Id, string Version);

/// <summary>
/// The flatpak-builder sources of a Flathub source build, generated from committed lock files (D039.17): every
/// package of the NativeAOT NuGet lock file and the runtime packs the SDK downloads besides it, and every package of the
/// frontend's package-lock.json. Each is a file source with the URL and SHA-512 of the file the registry serves.
/// </summary>
internal static class FlathubSources
{
    /// <summary>The runtime identifiers a Flathub build needs: one per architecture Flathub builds.</summary>
    internal static readonly string[] Runtimes = ["linux-x64", "linux-arm64"];

    /// <summary>
    /// The packages of a NuGet lock file, with their content hashes. Project entries are built from source; an entry
    /// without <c>resolved</c> or <c>contentHash</c> cannot be fetched. The packages of every runtime identifier's
    /// section are for every architecture: a restore resolves the graph of each runtime identifier the project lists.
    /// </summary>
    internal static IReadOnlyList<(FlathubNuGetPackage Package, string ContentHash)> ReadNuGetLock(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        var packages = new Dictionary<string, (FlathubNuGetPackage Package, string Hash)>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty section in document.RootElement.GetProperty("dependencies").EnumerateObject())
        foreach (JsonProperty entry in section.Value.EnumerateObject())
        {
            if (entry.Value.TryGetProperty("type", out JsonElement type) && type.GetString() == "Project") continue;
            string? version = entry.Value.TryGetProperty("resolved", out JsonElement resolved) ? resolved.GetString() : null;
            string? hash = entry.Value.TryGetProperty("contentHash", out JsonElement content) ? content.GetString() : null;
            if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(hash))
            {
                throw new DevDevelopmentException(PackageCodes.FlathubSourcesUnavailable,
                    $"The NuGet lock file entry {entry.Name} has no resolved version or content hash; restore the lock file again.", category: CommandExitCategory.Unavailable);
            }
            string key = entry.Name + "/" + version;
            if (packages.TryGetValue(key, out var known) && !string.Equals(known.Hash, hash, StringComparison.Ordinal))
            {
                throw new DevDevelopmentException(PackageCodes.FlathubSourcesUnavailable,
                    $"The NuGet lock file records two content hashes for {entry.Name} {version}; restore the lock file again.", category: CommandExitCategory.Unavailable);
            }
            packages[key] = (new FlathubNuGetPackage(entry.Name, version), hash);
        }
        return [.. packages.Values];
    }

    /// <summary>The packages once each, ordered by ID and version.</summary>
    internal static IReadOnlyList<FlathubNuGetPackage> Merge(IEnumerable<FlathubNuGetPackage> packages) =>
        [.. packages.DistinctBy(package => (package.Id.ToLowerInvariant(), package.Version.ToLowerInvariant()))
            .OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase).ThenBy(package => package.Version, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// <c>nuget-sources.json</c>: the packages from nuget.org's flat container, checked by the SHA-512 of the signed
    /// file that <paramref name="sha512"/> returns (hexadecimal), into <c>nuget-sources/</c>.
    /// </summary>
    internal static string NuGetSources(IEnumerable<FlathubNuGetPackage> packages, Func<FlathubNuGetPackage, string> sha512)
    {
        var sources = new JsonArray();
        foreach (FlathubNuGetPackage package in packages)
        {
            string id = package.Id.ToLowerInvariant();
            string version = package.Version.ToLowerInvariant();
            var source = new JsonObject
            {
                ["type"] = "file",
                ["url"] = $"https://api.nuget.org/v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg",
                ["sha512"] = sha512(package),
                ["dest"] = "nuget-sources",
                ["dest-filename"] = $"{id}.{version}.nupkg",
            };
            sources.Add((JsonNode)source);
        }
        return Serialize(sources);
    }

    /// <summary>
    /// <c>npm-sources.json</c> from a package-lock.json (lockfile version 2 or 3): each registry tarball with its
    /// integrity, into <c>flatpak-node/npm-tarballs/</c>, from which the build fills npm's cache. Packages for another
    /// operating system or for musl are left out, and those for one CPU get only-arches.
    /// </summary>
    internal static string NpmSources(string packageLock)
    {
        using JsonDocument document = JsonDocument.Parse(packageLock);
        if (!document.RootElement.TryGetProperty("packages", out JsonElement packages))
        {
            throw new DevDevelopmentException(PackageCodes.FlathubSourcesUnavailable,
                "The frontend's package-lock.json has no packages section; recreate it with npm 7 or later.", category: CommandExitCategory.Unavailable);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sources = new JsonArray();
        foreach (JsonProperty entry in packages.EnumerateObject())
        {
            if (entry.Name.Length == 0 || !entry.Name.Contains("node_modules/", StringComparison.Ordinal)) continue;
            JsonElement value = entry.Value;
            if (value.TryGetProperty("link", out JsonElement link) && link.ValueKind == JsonValueKind.True) continue;
            if (value.TryGetProperty("inBundle", out JsonElement bundled) && bundled.ValueKind == JsonValueKind.True) continue;
            if (!Matches(value, "os", ["linux"]) || !Matches(value, "libc", ["glibc"])) continue;
            string? resolved = value.TryGetProperty("resolved", out JsonElement url) ? url.GetString() : null;
            string? integrity = value.TryGetProperty("integrity", out JsonElement hash) ? hash.GetString() : null;
            string name = entry.Name[(entry.Name.LastIndexOf("node_modules/", StringComparison.Ordinal) + "node_modules/".Length)..];
            if (resolved is null || !resolved.StartsWith("https://", StringComparison.Ordinal) || integrity is null)
            {
                throw new DevDevelopmentException(PackageCodes.FlathubSourcesUnavailable,
                    $"The frontend's package-lock.json entry {name} has no registry URL and integrity, which an offline build needs (git and local dependencies are not supported).", category: CommandExitCategory.Unavailable);
            }
            (string algorithm, string digest) = Digest(integrity, name);
            if (!seen.Add(digest)) continue;
            string version = value.TryGetProperty("version", out JsonElement v) ? v.GetString() ?? "0" : "0";
            var source = new JsonObject
            {
                ["type"] = "file",
                ["url"] = resolved,
                [algorithm] = digest,
                ["dest"] = "flatpak-node/npm-tarballs",
                ["dest-filename"] = $"{name.Replace("@", "", StringComparison.Ordinal).Replace('/', '-')}-{version}-{digest[..Math.Min(12, digest.Length)]}.tgz",
            };
            string[] architectures = CpuArchitectures(value);
            if (architectures.Length == 0 && value.TryGetProperty("cpu", out _)) continue;
            if (architectures.Length is > 0 and < 2) source["only-arches"] = new JsonArray([.. architectures.Select(arch => (JsonNode)arch)]);
            sources.Add((JsonNode)source);
        }
        return Serialize(sources);
    }

    // Whether a package's os or libc list admits the values; a missing list admits everything, "!x" excludes x.
    private static bool Matches(JsonElement value, string property, string[] accepted)
    {
        if (!value.TryGetProperty(property, out JsonElement list) || list.ValueKind != JsonValueKind.Array) return true;
        string[] entries = [.. list.EnumerateArray().Select(item => item.GetString() ?? "")];
        if (entries.Any(entry => entry.StartsWith('!') && accepted.Contains(entry[1..]))) return false;
        string[] positive = [.. entries.Where(entry => !entry.StartsWith('!'))];
        return positive.Length == 0 || positive.Any(accepted.Contains);
    }

    // The Flatpak architectures a package's cpu list admits; empty without a cpu list.
    private static string[] CpuArchitectures(JsonElement value)
    {
        if (!value.TryGetProperty("cpu", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return [];
        var architectures = new List<string>();
        foreach ((string cpu, string architecture) in new[] { ("x64", "x86_64"), ("arm64", "aarch64") })
        {
            string[] entries = [.. list.EnumerateArray().Select(item => item.GetString() ?? "")];
            bool excluded = entries.Contains("!" + cpu);
            string[] positive = [.. entries.Where(entry => !entry.StartsWith('!'))];
            if (!excluded && (positive.Length == 0 || positive.Contains(cpu))) architectures.Add(architecture);
        }
        return [.. architectures];
    }

    /// <summary>The flatpak-builder checksum of an SRI integrity: sha512 or sha256, in hexadecimal.</summary>
    internal static (string Algorithm, string Digest) Digest(string integrity, string name)
    {
        foreach (string part in integrity.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = part.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0) continue;
            string algorithm = part[..dash];
            if (algorithm is not ("sha512" or "sha256")) continue;
            try
            {
                return (algorithm, Convert.ToHexStringLower(Convert.FromBase64String(part[(dash + 1)..])));
            }
            catch (FormatException)
            {
            }
        }
        throw new DevDevelopmentException(PackageCodes.FlathubSourcesUnavailable,
            $"The frontend's package-lock.json entry {name} has no sha512 or sha256 integrity; recreate the lock file with a current npm.", category: CommandExitCategory.Unavailable);
    }

    /// <summary>The hexadecimal SHA-512 in a NuGet <c>.nupkg.sha512</c> file, which holds it in base64.</summary>
    internal static string HexFromBase64(string base64) => Convert.ToHexStringLower(Convert.FromBase64String(base64.Trim()));

    private static string Serialize(JsonArray sources)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            sources.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
