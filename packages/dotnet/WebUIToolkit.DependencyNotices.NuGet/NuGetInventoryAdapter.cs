using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.NuGet;

/// <summary>Reads an already-restored NuGet graph without invoking restore or using the network.</summary>
public sealed class NuGetInventoryAdapter
{
    private const int MaximumInventoryBytes = 32 * 1024 * 1024;
    private const int MaximumEvidenceBytes = 4 * 1024 * 1024;
    private const int MaximumJsonDepth = 128;
    private const int MaximumJsonProperties = 200_000;
    private const int MaximumJsonValues = 500_000;
    private const int MaximumJsonStringLength = 2 * 1024 * 1024;
    private const long MaximumJsonTextLength = 24L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static InventoryResult Scan(NuGetInventoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        List<NoticeDiagnostic> diagnostics = [];
        using JsonDocument? lockDocument = ReadJson(options.LockFilePath, "packages.lock.json", diagnostics);
        using JsonDocument? assetsDocument = ReadJson(options.AssetsFilePath, "project.assets.json", diagnostics);
        if (lockDocument is null || assetsDocument is null)
        {
            return Result([], diagnostics);
        }

        if (!HasSupportedFormatVersion(lockDocument.RootElement, 1, 2) ||
            !HasSupportedFormatVersion(assetsDocument.RootElement, 3))
        {
            Add(diagnostics, NoticeDiagnosticCodes.UnsupportedInventoryFormat,
                "The NuGet lock or assets document has an unsupported format version.", "inventory#version");
            return Result([], diagnostics);
        }

        if (!TryObject(lockDocument.RootElement, "dependencies", out JsonElement lockTargets) ||
            !TryObject(assetsDocument.RootElement, "targets", out JsonElement assetTargets))
        {
            Add(diagnostics, NoticeDiagnosticCodes.UnsupportedInventoryFormat,
                "NuGet inventory is missing a required target map.", "inventory");
            return Result([], diagnostics);
        }

        string target = options.RuntimeIdentifier is null
            ? options.TargetFramework
            : string.Concat(options.TargetFramework, "/", options.RuntimeIdentifier);

        if (!TrySelectTarget(lockTargets, target, "packages.lock.json", diagnostics, out JsonElement lockTarget) |
            !TrySelectTarget(assetTargets, target, "project.assets.json", diagnostics, out JsonElement assetTarget))
        {
            return Result([], diagnostics);
        }

        Dictionary<string, LockPackage> locked = ParseLockTarget(lockTarget, diagnostics);
        Dictionary<string, AssetPackage> assets = ParseAssetTarget(assetTarget, diagnostics);
        HydrateAssetLibraries(assets, assetsDocument.RootElement, diagnostics);
        ValidateDependencyEdges(locked, diagnostics);
        CrossCheckGraphs(locked, assets, diagnostics);

        string? packagesRoot = ResolvePackagesRoot(options, assetsDocument.RootElement, diagnostics);
        List<InventoryComponent> components = [];
        foreach (LockPackage package in locked.Values.OrderBy(static value => value.Id, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static value => value.Id, StringComparer.Ordinal))
        {
            string key = MakeKey(package.Id, package.Version);
            if (!assets.TryGetValue(key, out AssetPackage? asset))
            {
                continue;
            }

            PackageMetadata metadata = packagesRoot is null
                ? PackageMetadata.Empty
                : InspectPackage(packagesRoot, package, asset, diagnostics);
            PackageUrl purl;
            try
            {
                purl = PackageUrl.Parse(string.Concat("pkg:nuget/", EncodePurl(package.Id), "@", EncodePurl(package.Version)));
            }
            catch (FormatException)
            {
                Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                    "NuGet package identity cannot be represented as a canonical Package URL.", package.Id);
                continue;
            }

            string? integrity = package.ContentHash is null ? asset.Sha512 : package.ContentHash;
            if (integrity is not null)
            {
                integrity = string.Concat("sha512-", integrity);
            }

            components.Add(new InventoryComponent(
                purl,
                package.Id,
                package.Version,
                InventorySourceKind.NuGet,
                metadata.IsDevelopmentOnly || asset.IsDevelopmentOnly ? DependencyScope.Development : DependencyScope.Runtime,
                package.IsDirect,
                metadata.LicenseExpression,
                integrity,
                string.Concat("packages.lock.json#", target, "/", package.Id),
                metadata.Evidence));
        }

        return Result(components, diagnostics);
    }

    private static JsonDocument? ReadJson(string path, string label, List<NoticeDiagnostic> diagnostics)
    {
        try
        {
            byte[] bytes = ReadBoundedFile(path, MaximumInventoryBytes);
            ReadOnlyMemory<byte> json = bytes.AsMemory();
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
            {
                json = bytes.AsMemory(3);
            }

            JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth,
            });
            if (!ValidateJsonShape(document.RootElement, label, diagnostics))
            {
                document.Dispose();
                return null;
            }

            return document;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Add(diagnostics, NoticeDiagnosticCodes.UnsupportedInventoryFormat,
                string.Concat("Cannot read a supported ", label, " document."), label);
            return null;
        }
    }

    private static bool ValidateJsonShape(
        JsonElement root,
        string label,
        List<NoticeDiagnostic> diagnostics)
    {
        try
        {
            Stack<JsonElement> pending = new();
            pending.Push(root);
            int propertyCount = 0;
            int valueCount = 1;
            long textLength = 0;

            while (pending.Count != 0)
            {
                JsonElement element = pending.Pop();
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                    {
                        HashSet<string> names = new(StringComparer.Ordinal);
                        foreach (JsonProperty property in element.EnumerateObject())
                        {
                            string name = property.Name;
                            if (!IsWellFormedUtf16(name) || !names.Add(name))
                            {
                                return RejectJsonShape(diagnostics, label,
                                    "The NuGet inventory contains a duplicate or malformed property name.");
                            }

                            propertyCount++;
                            textLength += name.Length;
                            if (propertyCount > MaximumJsonProperties || textLength > MaximumJsonTextLength)
                            {
                                return RejectJsonShape(diagnostics, label,
                                    "The NuGet inventory exceeds its property or text budget.");
                            }

                            if (++valueCount > MaximumJsonValues)
                            {
                                return RejectJsonShape(diagnostics, label,
                                    "The NuGet inventory exceeds its value-count budget.");
                            }

                            pending.Push(property.Value);
                        }

                        break;
                    }
                    case JsonValueKind.Array:
                        foreach (JsonElement item in element.EnumerateArray())
                        {
                            if (++valueCount > MaximumJsonValues)
                            {
                                return RejectJsonShape(diagnostics, label,
                                    "The NuGet inventory exceeds its value-count budget.");
                            }

                            pending.Push(item);
                        }

                        break;
                    case JsonValueKind.String:
                    {
                        string value = element.GetString() ?? string.Empty;
                        if (value.Length > MaximumJsonStringLength || !IsWellFormedUtf16(value))
                        {
                            return RejectJsonShape(diagnostics, label,
                                "The NuGet inventory contains an oversized or malformed string value.");
                        }

                        textLength += value.Length;
                        if (textLength > MaximumJsonTextLength)
                        {
                            return RejectJsonShape(diagnostics, label,
                                "The NuGet inventory exceeds its text budget.");
                        }

                        break;
                    }
                    case JsonValueKind.Number:
                        textLength += element.GetRawText().Length;
                        if (textLength > MaximumJsonTextLength)
                        {
                            return RejectJsonShape(diagnostics, label,
                                "The NuGet inventory exceeds its text budget.");
                        }

                        break;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                    case JsonValueKind.Null:
                        break;
                    default:
                        return RejectJsonShape(diagnostics, label,
                            "The NuGet inventory contains an unsupported JSON token.");
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            return RejectJsonShape(diagnostics, label,
                "The NuGet inventory contains malformed Unicode or JSON values.");
        }
    }

    private static bool RejectJsonShape(
        List<NoticeDiagnostic> diagnostics,
        string label,
        string message)
    {
        Add(diagnostics, NoticeDiagnosticCodes.UnsupportedInventoryFormat, message, label);
        return false;
    }

    private static bool IsWellFormedUtf16(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException("The input exceeds its byte limit.");
        }

        int length = checked((int)stream.Length);
        byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
        int offset = 0;
        while (offset != bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("The input changed while it was being read.");
            }

            offset += read;
        }

        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("The input changed or exceeds its byte limit.");
        }

        return bytes;
    }

    private static bool TrySelectTarget(
        JsonElement targets,
        string requested,
        string source,
        List<NoticeDiagnostic> diagnostics,
        out JsonElement selected)
    {
        selected = default;
        List<JsonProperty> matches = [];
        foreach (JsonProperty property in targets.EnumerateObject())
        {
            if (string.Equals(property.Name, requested, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(property);
            }
        }

        if (matches.Count != 1 || matches[0].Value.ValueKind != JsonValueKind.Object)
        {
            Add(diagnostics, NoticeDiagnosticCodes.AmbiguousTarget,
                matches.Count == 0
                    ? "The explicitly requested NuGet target is missing."
                    : "The explicitly requested NuGet target is ambiguous.",
                string.Concat(source, "#", requested));
            return false;
        }

        selected = matches[0].Value;
        return true;
    }

    private static Dictionary<string, LockPackage> ParseLockTarget(
        JsonElement target,
        List<NoticeDiagnostic> diagnostics)
    {
        Dictionary<string, LockPackage> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in target.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                    "A locked dependency entry must be an object.", property.Name);
                continue;
            }

            string? version = StringProperty(property.Value, "resolved");
            string? type = StringProperty(property.Value, "type");
            if (version is null || !IsExactVersion(version) || type is null)
            {
                Add(diagnostics, NoticeDiagnosticCodes.UnresolvedDependency,
                    "A locked dependency must have an exact resolved version and dependency type.", property.Name);
                continue;
            }

            List<string> dependencies = [];
            if (property.Value.TryGetProperty("dependencies", out JsonElement dependencyMap))
            {
                if (dependencyMap.ValueKind != JsonValueKind.Object)
                {
                    Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                        "A locked dependency edge map must be an object.", property.Name);
                    continue;
                }

                foreach (JsonProperty dependency in dependencyMap.EnumerateObject())
                {
                    dependencies.Add(dependency.Name);
                }
            }

            LockPackage package = new(
                property.Name,
                version,
                string.Equals(type, "Direct", StringComparison.OrdinalIgnoreCase),
                StringProperty(property.Value, "contentHash"),
                dependencies);
            if (package.ContentHash is null)
            {
                Add(diagnostics, NoticeDiagnosticCodes.UnresolvedDependency,
                    "A locked package is missing its immutable content hash.", property.Name);
            }

            if (!result.TryAdd(property.Name, package))
            {
                Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                    "The lock target contains duplicate package identities that differ only by case.", property.Name);
            }
        }

        return result;
    }

    private static Dictionary<string, AssetPackage> ParseAssetTarget(
        JsonElement target,
        List<NoticeDiagnostic> diagnostics)
    {
        Dictionary<string, AssetPackage> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in target.EnumerateObject())
        {
            int separator = property.Name.LastIndexOf('/');
            if (separator <= 0 || separator == property.Name.Length - 1 || property.Value.ValueKind != JsonValueKind.Object)
            {
                Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                    "An assets target entry must use an exact name/version identity.", property.Name);
                continue;
            }

            string id = property.Name[..separator];
            string version = property.Name[(separator + 1)..];
            if (!IsExactVersion(version))
            {
                Add(diagnostics, NoticeDiagnosticCodes.UnresolvedDependency,
                    "An assets target entry has no exact resolved version.", id);
                continue;
            }

            string? type = StringProperty(property.Value, "type");
            if (type is not null && !string.Equals(type, "package", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool hasRuntime = HasNonEmptyObject(property.Value, "runtime") ||
                              HasNonEmptyObject(property.Value, "native") ||
                              HasNonEmptyObject(property.Value, "runtimeTargets") ||
                              HasNonEmptyObject(property.Value, "compile");
            bool hasDevelopment = HasNonEmptyObject(property.Value, "build") ||
                                  HasNonEmptyObject(property.Value, "buildMultiTargeting") ||
                                  HasNonEmptyObject(property.Value, "buildTransitive") ||
                                  HasNonEmptyObject(property.Value, "analyzers");
            SortedDictionary<string, string> dependencies = new(StringComparer.OrdinalIgnoreCase);
            if (property.Value.TryGetProperty("dependencies", out JsonElement dependencyMap))
            {
                if (dependencyMap.ValueKind != JsonValueKind.Object)
                {
                    Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                        "An assets dependency edge map must be an object.", property.Name);
                    continue;
                }

                foreach (JsonProperty dependency in dependencyMap.EnumerateObject())
                {
                    if (dependency.Value.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(dependency.Value.GetString()) ||
                        !dependencies.TryAdd(dependency.Name, dependency.Value.GetString()!))
                    {
                        Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                            "An assets dependency edge must have a unique name and resolved version.", property.Name);
                    }
                }
            }

            AssetPackage package = new(id, version, hasDevelopment && !hasRuntime, null, null, [], dependencies);
            if (!result.TryAdd(MakeKey(id, version), package))
            {
                Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                    "The assets target contains duplicate package identities that differ only by case.", property.Name);
            }
        }

        return result;
    }

    private static void HydrateAssetLibraries(
        Dictionary<string, AssetPackage> targetAssets,
        JsonElement assetsRoot,
        List<NoticeDiagnostic> diagnostics)
    {
        if (!TryObject(assetsRoot, "libraries", out JsonElement libraries))
        {
            Add(diagnostics, NoticeDiagnosticCodes.UnsupportedInventoryFormat,
                "The assets document is missing its restored library map.", "project.assets.json#libraries");
            return;
        }

        foreach (JsonProperty property in libraries.EnumerateObject())
        {
            int separator = property.Name.LastIndexOf('/');
            if (separator <= 0 || separator == property.Name.Length - 1 || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string key = MakeKey(property.Name[..separator], property.Name[(separator + 1)..]);
            if (!targetAssets.TryGetValue(key, out AssetPackage? package))
            {
                continue;
            }

            List<string> files = [];
            if (property.Value.TryGetProperty("files", out JsonElement fileArray))
            {
                if (fileArray.ValueKind != JsonValueKind.Array)
                {
                    Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                        "A restored package file list must be an array.", property.Name);
                    continue;
                }

                foreach (JsonElement file in fileArray.EnumerateArray())
                {
                    if (file.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(file.GetString()))
                    {
                        Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                            "A restored package file path must be a non-empty string.", property.Name);
                        continue;
                    }

                    files.Add(file.GetString()!);
                }
            }

            targetAssets[key] = package with
            {
                Sha512 = StringProperty(property.Value, "sha512"),
                Path = StringProperty(property.Value, "path"),
                Files = files.Order(StringComparer.Ordinal).ToArray(),
            };
        }
    }

    private static void CrossCheckGraphs(
        Dictionary<string, LockPackage> locked,
        Dictionary<string, AssetPackage> targetAssets,
        List<NoticeDiagnostic> diagnostics)
    {
        HashSet<string> lockedKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (LockPackage package in locked.Values)
        {
            lockedKeys.Add(MakeKey(package.Id, package.Version));
        }

        foreach (string key in lockedKeys)
        {
            if (!targetAssets.ContainsKey(key))
            {
                Add(diagnostics, NoticeDiagnosticCodes.LockFileDrift,
                    "The locked package is absent from the selected restored target.", DisplayKey(key));
            }
        }

        foreach (LockPackage package in locked.Values)
        {
            if (!targetAssets.TryGetValue(MakeKey(package.Id, package.Version), out AssetPackage? asset))
            {
                continue;
            }

            if (package.ContentHash is not null && asset.Sha512 is not null &&
                !string.Equals(package.ContentHash, asset.Sha512, StringComparison.Ordinal))
            {
                Add(diagnostics, NoticeDiagnosticCodes.LockFileDrift,
                    "The restored package hash differs from the locked package hash.", package.Id);
            }
            else if (asset.Sha512 is null || asset.Path is null)
            {
                Add(diagnostics, NoticeDiagnosticCodes.LockFileDrift,
                    "The restored package is missing immutable hash or path metadata.", package.Id);
            }

            string[] lockedEdges = package.Dependencies.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] assetEdges = asset.Dependencies.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!lockedEdges.SequenceEqual(assetEdges, StringComparer.OrdinalIgnoreCase))
            {
                Add(diagnostics, NoticeDiagnosticCodes.LockFileDrift,
                    "The restored dependency edges differ from the locked dependency edges.", package.Id);
            }

            foreach ((string dependencyId, string dependencyVersion) in asset.Dependencies)
            {
                if (!locked.TryGetValue(dependencyId, out LockPackage? lockedDependency) ||
                    !string.Equals(lockedDependency.Version, dependencyVersion, StringComparison.OrdinalIgnoreCase))
                {
                    Add(diagnostics, NoticeDiagnosticCodes.LockFileDrift,
                        "A restored dependency edge resolves to a version different from the lock target.",
                        string.Concat(package.Id, " -> ", dependencyId));
                }
            }
        }

        foreach (string key in targetAssets.Keys)
        {
            if (!lockedKeys.Contains(key))
            {
                Add(diagnostics, NoticeDiagnosticCodes.LockFileDrift,
                    "The restored target contains a package absent from the selected lock target.", DisplayKey(key));
            }
        }
    }

    private static void ValidateDependencyEdges(
        Dictionary<string, LockPackage> locked,
        List<NoticeDiagnostic> diagnostics)
    {
        foreach (LockPackage package in locked.Values)
        {
            foreach (string dependency in package.Dependencies.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!locked.ContainsKey(dependency))
                {
                    Add(diagnostics, NoticeDiagnosticCodes.UnresolvedDependency,
                        "A locked dependency edge does not resolve inside the selected target.",
                        string.Concat(package.Id, " -> ", dependency));
                }
            }
        }
    }

    private static string? ResolvePackagesRoot(
        NuGetInventoryOptions options,
        JsonElement assetsRoot,
        List<NoticeDiagnostic> diagnostics)
    {
        if (options.PackagesRoot is not null)
        {
            return Path.GetFullPath(options.PackagesRoot);
        }

        if (!TryObject(assetsRoot, "packageFolders", out JsonElement folders))
        {
            Add(diagnostics, NoticeDiagnosticCodes.MissingEvidence,
                "No local NuGet package root was declared for evidence inspection.", "project.assets.json#packageFolders");
            return null;
        }

        string[] roots = folders.EnumerateObject().Select(static property => property.Name).ToArray();
        if (roots.Length != 1)
        {
            Add(diagnostics, NoticeDiagnosticCodes.MultipleEvidenceCandidates,
                "A single local NuGet package root is required for deterministic evidence inspection.",
                "project.assets.json#packageFolders");
            return null;
        }

        try
        {
            return Path.GetFullPath(roots[0]);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                "The local NuGet package root is invalid.", "project.assets.json#packageFolders");
            return null;
        }
    }

    private static PackageMetadata InspectPackage(
        string packagesRoot,
        LockPackage package,
        AssetPackage targetAsset,
        List<NoticeDiagnostic> diagnostics)
    {
        // project.assets.json supplies the restored relative path; resolve it only beneath the declared root.
        string packageRelative = targetAsset.Path ??
            string.Concat(package.Id.ToLowerInvariant(), "/", package.Version.ToLowerInvariant());
        if (!TryContainedPath(packagesRoot, packageRelative, out string? packageDirectory) || packageDirectory is null)
        {
            Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                "The restored package path is not contained by the declared package root.", package.Id);
            return PackageMetadata.Empty;
        }

        string[] nuspecCandidates = targetAsset.Files
            .Where(static file => file.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (nuspecCandidates.Length > 1)
        {
            Add(diagnostics, NoticeDiagnosticCodes.MultipleEvidenceCandidates,
                "The restored package has multiple manifest candidates.", package.Id);
            return PackageMetadata.Empty;
        }

        string nuspecName = nuspecCandidates.Length == 1
            ? nuspecCandidates[0]
            : string.Concat(package.Id.ToLowerInvariant(), ".nuspec");
        if (!TryContainedPath(packageDirectory, nuspecName, out string? nuspecPath) || !File.Exists(nuspecPath))
        {
            Add(diagnostics, NoticeDiagnosticCodes.MissingEvidence,
                "The restored package manifest is missing.", package.Id);
            return PackageMetadata.Empty;
        }

        NuspecMetadata nuspec;
        try
        {
            nuspec = ReadNuspec(nuspecPath);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or DecoderFallbackException)
        {
            Add(diagnostics, NoticeDiagnosticCodes.InvalidEvidenceEncoding,
                "The restored package manifest is malformed or has invalid text encoding.", package.Id);
            return PackageMetadata.Empty;
        }

        if (!string.Equals(nuspec.Id, package.Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(nuspec.Version, package.Version, StringComparison.OrdinalIgnoreCase))
        {
            Add(diagnostics, NoticeDiagnosticCodes.LockFileDrift,
                "The restored package manifest identity differs from the locked identity.", package.Id);
        }

        List<NoticeEvidence> evidence = [];
        string? licenseCandidate = nuspec.LicenseFile;
        bool hasAmbiguousLicenseCandidates = false;
        if (licenseCandidate is null)
        {
            string[] conventionalCandidates = targetAsset.Files
                .Where(IsConventionalLicenseFile)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (conventionalCandidates.Length > 1)
            {
                hasAmbiguousLicenseCandidates = true;
                Add(diagnostics, NoticeDiagnosticCodes.MultipleEvidenceCandidates,
                    "The restored package has multiple local license evidence candidates.", package.Id);
            }
            else if (conventionalCandidates.Length == 1)
            {
                licenseCandidate = conventionalCandidates[0];
            }
        }

        if (licenseCandidate is not null)
        {
            if (!TryContainedPath(packageDirectory, licenseCandidate, out string? licensePath))
            {
                Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                    "The package license file path is unsafe.", package.Id);
            }
            else if (!File.Exists(licensePath))
            {
                Add(diagnostics, NoticeDiagnosticCodes.MissingEvidence,
                    "The declared package license file is missing.", package.Id);
            }
            else
            {
                try
                {
                    byte[] bytes = ReadBoundedFile(licensePath, MaximumEvidenceBytes);
                    _ = StrictUtf8.GetString(bytes);
                    evidence.Add(new NoticeEvidence(
                        NoticeAssetKind.License,
                        Convert.ToHexStringLower(SHA256.HashData(bytes)),
                        string.Concat(packageRelative.Replace('\\', '/'), "/", licenseCandidate.Replace('\\', '/')),
                        "text/plain"));
                }
                catch (DecoderFallbackException)
                {
                    Add(diagnostics, NoticeDiagnosticCodes.InvalidEvidenceEncoding,
                        "The package license file is not valid UTF-8 text.", package.Id);
                }
                catch (InvalidDataException)
                {
                    Add(diagnostics, NoticeDiagnosticCodes.InvalidEvidenceEncoding,
                        "The package license file exceeds the safe evidence byte limit.", package.Id);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Add(diagnostics, NoticeDiagnosticCodes.MissingEvidence,
                        "The package license file cannot be read.", package.Id);
                }
            }
        }
        else if (hasAmbiguousLicenseCandidates)
        {
            // The ambiguity diagnostic above is sufficient; do not select by filesystem enumeration order.
        }
        else if (nuspec.LicenseUrl is not null)
        {
            Add(diagnostics, NoticeDiagnosticCodes.UrlOnlyEvidence,
                "The package declares only a remote license URL; acquisition is required.", package.Id);
        }
        else
        {
            Add(diagnostics, NoticeDiagnosticCodes.MissingEvidence,
                "The package has no local license evidence candidate.", package.Id);
        }

        return new PackageMetadata(nuspec.LicenseExpression, nuspec.DevelopmentDependency, evidence);
    }

    private static bool IsConventionalLicenseFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("LICENCE", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase);
    }

    private static NuspecMetadata ReadNuspec(string path)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4 * 1024 * 1024,
            MaxCharactersFromEntities = 0,
        };
        byte[] nuspecBytes = ReadBoundedFile(path, MaximumEvidenceBytes);
        using MemoryStream stream = new(nuspecBytes, writable: false);
        using XmlReader reader = XmlReader.Create(stream, settings);

        string? id = null;
        string? version = null;
        string? licenseExpression = null;
        string? licenseFile = null;
        string? licenseUrl = null;
        bool developmentDependency = false;
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            string localName = reader.LocalName;
            if (localName == "license")
            {
                string? type = reader.GetAttribute("type");
                string value = reader.ReadElementContentAsString().Trim();
                if (string.Equals(type, "expression", StringComparison.OrdinalIgnoreCase))
                {
                    licenseExpression = value;
                }
                else if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
                {
                    licenseFile = value;
                }

                continue;
            }

            if (localName is not ("id" or "version" or "licenseUrl" or "developmentDependency"))
            {
                reader.Read();
                continue;
            }

            string content = reader.ReadElementContentAsString().Trim();
            switch (localName)
            {
                case "id": id = content; break;
                case "version": version = content; break;
                case "licenseUrl": licenseUrl = content; break;
                case "developmentDependency":
                    developmentDependency = string.Equals(content, "true", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        return new NuspecMetadata(id, version, licenseExpression, licenseFile, licenseUrl, developmentDependency);
    }

    private static bool TryContainedPath(string root, string relative, out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
        {
            return false;
        }

        string normalized = relative.Replace('\\', '/');
        foreach (string segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        try
        {
            string fullRoot = Path.GetFullPath(root);
            string candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
            string prefix = Path.EndsInDirectorySeparator(fullRoot)
                ? fullRoot
                : string.Concat(fullRoot, Path.DirectorySeparatorChar);
            if (!candidate.StartsWith(prefix, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                return false;
            }

            if (IsReparsePoint(fullRoot))
            {
                return false;
            }

            string current = fullRoot;
            foreach (string segment in normalized.Split('/'))
            {
                current = Path.Combine(current, segment);
                if ((File.Exists(current) || Directory.Exists(current)) && IsReparsePoint(current))
                {
                    return false;
                }
            }

            resolved = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static InventoryResult Result(
        IEnumerable<InventoryComponent> components,
        IEnumerable<NoticeDiagnostic> diagnostics)
    {
        InventoryComponent[] orderedComponents = components.Order(InventoryComponentComparer.Instance).ToArray();
        NoticeDiagnostic[] orderedDiagnostics = diagnostics
            .OrderBy(static value => value.Code, StringComparer.Ordinal)
            .ThenBy(static value => value.PackageUrl, StringComparer.Ordinal)
            .ThenBy(static value => value.Source, StringComparer.Ordinal)
            .ThenBy(static value => value.Message, StringComparer.Ordinal)
            .ToArray();
        return new InventoryResult(orderedComponents, orderedDiagnostics);
    }

    private static bool TryObject(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool HasSupportedFormatVersion(JsonElement root, params int[] supported)
    {
        if (!root.TryGetProperty("version", out JsonElement version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out int actual))
        {
            return false;
        }

        return Array.IndexOf(supported, actual) >= 0;
    }

    private static string? StringProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool HasNonEmptyObject(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().MoveNext();

    private static bool IsExactVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character is '[' or ']' or '(' or ')' or '*' or ',' || char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private static string MakeKey(string id, string version) => string.Concat(id, "\u001f", version);

    private static string DisplayKey(string key) => key.Replace('\u001f', '/');

    private static string EncodePurl(string value)
    {
        StringBuilder builder = new();
        Span<byte> buffer = stackalloc byte[4];
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (rune.IsAscii && (char.IsAsciiLetterOrDigit((char)rune.Value) || rune.Value is '-' or '.' or '_' or '~'))
            {
                builder.Append((char)rune.Value);
                continue;
            }

            int length = rune.EncodeToUtf8(buffer);
            for (int index = 0; index < length; index++)
            {
                builder.Append('%').Append(buffer[index].ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static void Add(List<NoticeDiagnostic> diagnostics, string code, string message, string source) =>
        diagnostics.Add(new NoticeDiagnostic(code, NoticeDiagnosticSeverity.Error, message, Source: source));

    private sealed record LockPackage(
        string Id,
        string Version,
        bool IsDirect,
        string? ContentHash,
        IReadOnlyList<string> Dependencies);

    private sealed record AssetPackage(
        string Id,
        string Version,
        bool IsDevelopmentOnly,
        string? Sha512,
        string? Path,
        IReadOnlyList<string> Files,
        IReadOnlyDictionary<string, string> Dependencies);

    private sealed record NuspecMetadata(
        string? Id,
        string? Version,
        string? LicenseExpression,
        string? LicenseFile,
        string? LicenseUrl,
        bool DevelopmentDependency);

    private sealed record PackageMetadata(
        string? LicenseExpression,
        bool IsDevelopmentOnly,
        IReadOnlyList<NoticeEvidence> Evidence)
    {
        public static PackageMetadata Empty { get; } = new(null, false, []);
    }
}
