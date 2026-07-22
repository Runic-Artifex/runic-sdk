using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Npm;

public sealed class NpmInventoryScanner
{
    private const int MaximumJsonBytes = 16 * 1024 * 1024;
    private const int MaximumEvidenceBytes = 16 * 1024 * 1024;
    private const int MaximumProperties = 250_000;
    private const int MaximumDepth = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static InventoryResult Scan(NpmInventoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<NoticeDiagnostic> diagnostics = [];
        List<InventoryComponent> components = [];

        try
        {
            string root = Path.GetFullPath(options.RootDirectory);
            string lockRelative = NormalizeRelativePath(options.LockFileRelativePath, allowDot: false);
            string workspace = NormalizeRelativePath(options.WorkspaceRelativePath, allowDot: true);
            string lockPath = ResolveSafePath(root, lockRelative, requireFile: true);

            string lockName = Path.GetFileName(lockRelative);
            if (!StringComparer.Ordinal.Equals(lockName, "package-lock.json") &&
                !StringComparer.Ordinal.Equals(lockName, "npm-shrinkwrap.json"))
            {
                Add(diagnostics, NoticeDiagnosticCodes.UnsupportedInventoryFormat,
                    "The npm inventory source must be package-lock.json or npm-shrinkwrap.json.", lockRelative);
                return Finish(components, diagnostics);
            }

            using JsonDocument lockDocument = ReadJson(lockPath, lockRelative);
            JsonElement lockRoot = lockDocument.RootElement;
            int lockVersion = RequireInteger(lockRoot, "lockfileVersion", lockRelative);
            if (lockVersion is not (2 or 3))
            {
                Add(diagnostics, NoticeDiagnosticCodes.UnsupportedInventoryFormat,
                    "Only npm lockfile versions 2 and 3 with package entries are supported.", lockRelative);
                return Finish(components, diagnostics);
            }

            JsonElement packagesObject = RequireObject(lockRoot, "packages", lockRelative);
            Dictionary<string, LockEntry> entries = ParseLockEntries(packagesObject, lockRelative);
            string workspaceKey = workspace == "." ? string.Empty : workspace;
            if (!entries.TryGetValue(workspaceKey, out LockEntry? workspaceEntry))
            {
                Add(diagnostics, NoticeDiagnosticCodes.AmbiguousTarget,
                    "The explicitly selected npm workspace has no matching lockfile package entry.", workspace);
                return Finish(components, diagnostics);
            }

            string packageJsonRelative = workspace == "." ? "package.json" : workspace + "/package.json";
            string packageJsonPath = ResolveSafePath(root, packageJsonRelative, requireFile: true);
            using JsonDocument workspacePackageDocument = ReadJson(packageJsonPath, packageJsonRelative);
            VerifyWorkspaceDeclarations(workspacePackageDocument.RootElement, workspaceEntry, packageJsonRelative);

            SortedDictionary<string, RootDependency> roots = ReadRootDependencies(
                workspacePackageDocument.RootElement,
                options.Profile,
                packageJsonRelative);

            Queue<PendingDependency> pending = new();
            foreach ((string name, RootDependency dependency) in roots)
            {
                pending.Enqueue(new PendingDependency(workspaceKey, name, dependency.Scope, true));
            }

            Dictionary<string, IncludedPackage> included = new(StringComparer.Ordinal);
            while (pending.Count != 0)
            {
                PendingDependency requested = pending.Dequeue();
                if (!TryResolvePackage(entries, requested.RequesterKey, requested.Name, out string? key, out LockEntry? entry))
                {
                    Add(diagnostics, NoticeDiagnosticCodes.UnresolvedDependency,
                        $"The exact dependency '{SanitizeName(requested.Name)}' is not resolved in the selected npm lockfile graph.",
                        lockRelative);
                    continue;
                }

                DependencyScope scope = MergeScope(requested.Scope, Classify(entry!));
                if (options.Profile == NpmInventoryProfile.Runtime && scope == DependencyScope.Development)
                {
                    continue;
                }

                if (entry.Link)
                {
                    if (entry.Resolved is null)
                    {
                        Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                            "A workspace link is missing its relative resolved target.", key);
                        continue;
                    }

                    string target = ResolveLinkKey(key!, entry.Resolved);
                    if (!entries.TryGetValue(target, out LockEntry? targetEntry))
                    {
                        Add(diagnostics, NoticeDiagnosticCodes.UnresolvedDependency,
                            "A workspace link target is not present in the lockfile package entries.", key);
                        continue;
                    }

                    key = target;
                    entry = targetEntry;
                }

                if (included.TryGetValue(key!, out IncludedPackage? existing))
                {
                    DependencyScope merged = MergeScope(existing.Scope, scope);
                    included[key] = existing with { Scope = merged, IsDirect = existing.IsDirect || requested.IsDirect };
                    if (merged != existing.Scope)
                    {
                        foreach ((string childName, DependencyScope edgeScope) in
                                 EnumerateDependencyEdges(entry, merged))
                        {
                            pending.Enqueue(new PendingDependency(key, childName, edgeScope, false));
                        }
                    }

                    continue;
                }

                IncludedPackage added = new(entry, scope, requested.IsDirect);
                included.Add(key!, added);

                foreach ((string childName, DependencyScope edgeScope) in EnumerateDependencyEdges(entry, scope))
                {
                    pending.Enqueue(new PendingDependency(key!, childName, edgeScope, false));
                }
            }

            HashSet<string> purls = new(StringComparer.Ordinal);
            foreach ((string key, IncludedPackage package) in included)
            {
                InventoryComponent? component = InspectPackage(root, key, package, diagnostics);
                if (component is null)
                {
                    continue;
                }

                if (!purls.Add(component.PackageUrl.CanonicalValue))
                {
                    Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                        "The selected npm graph contains a duplicate canonical Package URL.", key,
                        component.PackageUrl.CanonicalValue);
                    continue;
                }

                components.Add(component);
            }
        }
        catch (NpmInventoryException exception)
        {
            Add(diagnostics, exception.Code, exception.Message, exception.DiagnosticSource);
        }
        catch (IOException)
        {
            Add(diagnostics, NoticeDiagnosticCodes.MissingEvidence,
                "A required npm lockfile, package manifest, restored directory, or evidence file is missing or unreadable.",
                options.LockFileRelativePath);
        }
        catch (UnauthorizedAccessException)
        {
            Add(diagnostics, NoticeDiagnosticCodes.MissingEvidence,
                "A required npm inventory input is not readable.", options.LockFileRelativePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                "An npm inventory path or input value is invalid.", options.LockFileRelativePath);
        }

        return Finish(components, diagnostics);
    }

    private static InventoryResult Finish(List<InventoryComponent> components, List<NoticeDiagnostic> diagnostics)
    {
        components.Sort(InventoryComponentComparer.Instance);
        diagnostics.Sort(static (left, right) =>
        {
            int byCode = StringComparer.Ordinal.Compare(left.Code, right.Code);
            if (byCode != 0)
            {
                return byCode;
            }

            int bySource = StringComparer.Ordinal.Compare(left.Source, right.Source);
            return bySource != 0 ? bySource : StringComparer.Ordinal.Compare(left.Message, right.Message);
        });
        return new InventoryResult(components.AsReadOnly(), diagnostics.AsReadOnly());
    }

    private static Dictionary<string, LockEntry> ParseLockEntries(JsonElement packages, string source)
    {
        Dictionary<string, LockEntry> entries = new(StringComparer.Ordinal);
        foreach (JsonProperty property in packages.EnumerateObject())
        {
            string key = property.Name.Length == 0 ? string.Empty : NormalizeRelativePath(property.Name, allowDot: false);
            if (!StringComparer.Ordinal.Equals(property.Name, key))
            {
                throw InvalidGraph("An npm package entry path is not canonically normalized.", source);
            }

            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw InvalidGraph("Every npm package entry must be an object.", source);
            }

            if (!entries.TryAdd(key, LockEntry.Parse(property.Value, key)))
            {
                throw InvalidGraph("The npm package map contains a duplicate normalized path.", source);
            }
        }

        return entries;
    }

    private static InventoryComponent? InspectPackage(
        string root,
        string key,
        IncludedPackage included,
        List<NoticeDiagnostic> diagnostics)
    {
        LockEntry entry = included.Entry;
        if (entry.Version is null || !IsExactVersion(entry.Version))
        {
            Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                "An npm package entry is missing an exact resolved version.", key);
            return null;
        }

        if (entry.Integrity is null || !IsValidIntegrity(entry.Integrity))
        {
            Add(diagnostics, NoticeDiagnosticCodes.InvalidDependencyGraph,
                "An npm package entry is missing valid exact Subresource Integrity metadata.", key);
            return null;
        }

        string packageDirectory;
        try
        {
            packageDirectory = ResolveSafePath(root, key, requireFile: false);
        }
        catch (NpmInventoryException exception)
        {
            Add(diagnostics, exception.Code, exception.Message, key);
            return null;
        }

        if (!Directory.Exists(packageDirectory))
        {
            Add(diagnostics, NoticeDiagnosticCodes.MissingEvidence,
                "The selected npm package is not restored beneath node_modules.", key);
            return null;
        }

        string packageJsonRelative = key + "/package.json";
        string packageJsonPath;
        try
        {
            packageJsonPath = ResolveSafePath(root, packageJsonRelative, requireFile: true);
        }
        catch (NpmInventoryException exception)
        {
            Add(diagnostics, exception.Code, exception.Message, packageJsonRelative);
            return null;
        }
        catch (IOException)
        {
            Add(diagnostics, NoticeDiagnosticCodes.MissingEvidence,
                "The restored npm package is missing package.json.", packageJsonRelative);
            return null;
        }

        JsonDocument packageDocument;
        try
        {
            packageDocument = ReadJson(packageJsonPath, packageJsonRelative);
        }
        catch (NpmInventoryException exception)
        {
            Add(diagnostics, exception.Code, exception.Message, exception.DiagnosticSource);
            return null;
        }

        using (packageDocument)
        {
            JsonElement manifest = packageDocument.RootElement;
            string? manifestName = OptionalString(manifest, "name", packageJsonRelative);
            string name = manifestName ?? entry.Name ?? DerivePackageName(key);
            string? manifestVersion = OptionalString(manifest, "version", packageJsonRelative);
            if (manifestVersion is not null && !StringComparer.Ordinal.Equals(manifestVersion, entry.Version))
            {
                Add(diagnostics, NoticeDiagnosticCodes.LockFileDrift,
                    "The restored package.json version does not match the selected npm lockfile entry.", packageJsonRelative);
                return null;
            }

            if (entry.Name is not null && manifestName is not null &&
                !StringComparer.Ordinal.Equals(entry.Name, manifestName))
            {
                Add(diagnostics, NoticeDiagnosticCodes.LockFileDrift,
                    "The restored package.json name does not match the selected npm lockfile entry.", packageJsonRelative);
                return null;
            }

            PackageUrl purl;
            try
            {
                purl = PackageUrl.Parse(CreatePackageUrl(name, entry.Version));
            }
            catch (FormatException)
            {
                Add(diagnostics, NoticeDiagnosticCodes.InvalidPackageUrl,
                    "The npm package name or version cannot form a canonical Package URL.", packageJsonRelative);
                return null;
            }

            string? license = ReadLicense(manifest, packageJsonRelative, purl.CanonicalValue, diagnostics);
            List<NoticeEvidence> evidence = ReadEvidence(packageDirectory, key, purl.CanonicalValue, diagnostics);
            return new InventoryComponent(
                purl,
                name,
                entry.Version,
                InventorySourceKind.Npm,
                included.Scope,
                included.IsDirect,
                license,
                entry.Integrity,
                packageJsonRelative,
                evidence.AsReadOnly());
        }
    }

    private static List<NoticeEvidence> ReadEvidence(
        string packageDirectory,
        string packageKey,
        string purl,
        List<NoticeDiagnostic> diagnostics)
    {
        List<(string Name, NoticeAssetKind Kind)> candidates = [];
        foreach (string path in Directory.EnumerateFiles(packageDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if (TryGetEvidenceKind(name, out NoticeAssetKind kind))
            {
                candidates.Add((name, kind));
            }
        }

        candidates.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        if (candidates.Count == 0)
        {
            Add(diagnostics, NoticeDiagnosticCodes.MissingEvidence,
                "The restored npm package has no LICENSE, NOTICE, or AUTHORS evidence candidate.", packageKey, purl);
            return [];
        }

        int licenseCount = candidates.Count(static candidate => candidate.Kind == NoticeAssetKind.License);
        if (licenseCount > 1)
        {
            Add(diagnostics, NoticeDiagnosticCodes.MultipleEvidenceCandidates,
                "The restored npm package has multiple license evidence candidates; all candidates were preserved.",
                packageKey, purl, NoticeDiagnosticSeverity.Warning);
        }

        List<NoticeEvidence> evidence = [];
        foreach ((string name, NoticeAssetKind kind) in candidates)
        {
            string relative = packageKey + "/" + name;
            string path;
            try
            {
                path = ResolveSafePath(packageDirectory, name, requireFile: true);
            }
            catch (NpmInventoryException exception)
            {
                Add(diagnostics, exception.Code, exception.Message, relative, purl);
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = ReadBounded(
                    path,
                    MaximumEvidenceBytes,
                    NoticeDiagnosticCodes.InvalidEvidenceEncoding,
                    "An npm evidence candidate exceeds the 16 MiB limit.",
                    relative);
            }
            catch (NpmInventoryException exception)
            {
                Add(diagnostics, exception.Code, exception.Message, relative, purl);
                continue;
            }

            try
            {
                _ = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                Add(diagnostics, NoticeDiagnosticCodes.InvalidEvidenceEncoding,
                    "An npm evidence candidate is not valid UTF-8 text.", relative, purl);
                continue;
            }

            string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            evidence.Add(new NoticeEvidence(kind, digest, relative, "text/plain"));
        }

        return evidence;
    }

    private static string? ReadLicense(
        JsonElement manifest,
        string source,
        string purl,
        List<NoticeDiagnostic> diagnostics)
    {
        if (!manifest.TryGetProperty("license", out JsonElement license))
        {
            return null;
        }

        if (license.ValueKind != JsonValueKind.String)
        {
            Add(diagnostics, NoticeDiagnosticCodes.InvalidEvidenceEncoding,
                "The npm package license metadata must be a single SPDX expression string.", source, purl);
            return null;
        }

        string? value = license.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(diagnostics, NoticeDiagnosticCodes.InvalidEvidenceEncoding,
                "The npm package license metadata is empty.", source, purl);
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Add(diagnostics, NoticeDiagnosticCodes.UrlOnlyEvidence,
                "The npm package exposes only license URL metadata; acquisition is a separate explicit operation.",
                source, purl, NoticeDiagnosticSeverity.Warning);
            return null;
        }

        return value;
    }

    private static bool TryGetEvidenceKind(string name, out NoticeAssetKind kind)
    {
        if (MatchesCandidate(name, "LICENSE") || MatchesCandidate(name, "LICENCE") ||
            MatchesCandidate(name, "COPYING"))
        {
            kind = NoticeAssetKind.License;
            return true;
        }

        if (MatchesCandidate(name, "NOTICE"))
        {
            kind = NoticeAssetKind.Notice;
            return true;
        }

        if (MatchesCandidate(name, "AUTHORS"))
        {
            kind = NoticeAssetKind.Authors;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool MatchesCandidate(string name, string prefix) =>
        name.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
        (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
         name[prefix.Length] is '.' or '-' or '_');

    private static SortedDictionary<string, DependencyScope> EnumerateDependencyEdges(
        LockEntry entry,
        DependencyScope inherited)
    {
        SortedDictionary<string, DependencyScope> edges = new(StringComparer.Ordinal);
        foreach (string name in entry.Dependencies.Keys)
        {
            edges[name] = inherited == DependencyScope.Development
                ? DependencyScope.Development
                : DependencyScope.Runtime;
        }

        foreach (string name in entry.OptionalDependencies.Keys)
        {
            edges[name] = DependencyScope.Optional;
        }

        foreach (string name in entry.PeerDependencies.Keys)
        {
            edges[name] = DependencyScope.Peer;
        }

        foreach (string name in entry.BundledDependencies)
        {
            edges[name] = DependencyScope.Bundled;
        }

        return edges;
    }

    private static SortedDictionary<string, RootDependency> ReadRootDependencies(
        JsonElement manifest,
        NpmInventoryProfile profile,
        string source)
    {
        SortedDictionary<string, RootDependency> result = new(StringComparer.Ordinal);
        AddDependencyGroup(manifest, "dependencies", DependencyScope.Runtime, result, source);
        AddDependencyGroup(manifest, "optionalDependencies", DependencyScope.Optional, result, source);
        AddDependencyGroup(manifest, "peerDependencies", DependencyScope.Peer, result, source);
        if (profile == NpmInventoryProfile.Development)
        {
            AddDependencyGroup(manifest, "devDependencies", DependencyScope.Development, result, source);
        }

        return result;
    }

    private static void AddDependencyGroup(
        JsonElement manifest,
        string propertyName,
        DependencyScope scope,
        SortedDictionary<string, RootDependency> result,
        string source)
    {
        if (!manifest.TryGetProperty(propertyName, out JsonElement group))
        {
            return;
        }

        if (group.ValueKind != JsonValueKind.Object)
        {
            throw InvalidGraph($"The npm '{propertyName}' field must be an object.", source);
        }

        foreach (JsonProperty dependency in group.EnumerateObject())
        {
            if (dependency.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(dependency.Value.GetString()))
            {
                throw InvalidGraph($"The npm '{propertyName}' field contains an invalid dependency declaration.", source);
            }

            result[dependency.Name] = new RootDependency(dependency.Value.GetString()!, scope);
        }
    }

    private static void VerifyWorkspaceDeclarations(JsonElement manifest, LockEntry entry, string source)
    {
        foreach (string propertyName in new[] { "dependencies", "devDependencies", "optionalDependencies", "peerDependencies" })
        {
            Dictionary<string, string> manifestGroup = ReadStringMap(manifest, propertyName, source);
            Dictionary<string, string> lockGroup = propertyName switch
            {
                "dependencies" => entry.Dependencies,
                "devDependencies" => entry.DevDependencies,
                "optionalDependencies" => entry.OptionalDependencies,
                _ => entry.PeerDependencies,
            };

            if (manifestGroup.Count != lockGroup.Count ||
                manifestGroup.Any(pair => !lockGroup.TryGetValue(pair.Key, out string? value) ||
                                          !StringComparer.Ordinal.Equals(pair.Value, value)))
            {
                throw new NpmInventoryException(
                    NoticeDiagnosticCodes.LockFileDrift,
                    $"The selected workspace '{propertyName}' declarations do not match the lockfile package entry.",
                    source);
            }
        }
    }

    private static bool TryResolvePackage(
        IReadOnlyDictionary<string, LockEntry> entries,
        string requesterKey,
        string name,
        [NotNullWhen(true)] out string? key,
        [NotNullWhen(true)] out LockEntry? entry)
    {
        string directory = requesterKey;
        while (true)
        {
            string candidate = directory.Length == 0
                ? "node_modules/" + name
                : directory + "/node_modules/" + name;
            if (entries.TryGetValue(candidate, out entry))
            {
                key = candidate;
                return true;
            }

            if (directory.Length == 0)
            {
                break;
            }

            int separator = directory.LastIndexOf('/');
            directory = separator < 0 ? string.Empty : directory[..separator];
        }

        key = null;
        entry = null;
        return false;
    }

    private static string ResolveLinkKey(string key, string resolved)
    {
        string keyDirectory = key.Contains('/') ? key[..key.LastIndexOf('/')] : string.Empty;
        string combined = keyDirectory.Length == 0 ? resolved : keyDirectory + "/" + resolved;
        string[] parts = combined.Split('/');
        List<string> normalized = [];
        foreach (string part in parts)
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (normalized.Count == 0)
                {
                    throw InvalidGraph("A workspace link target escapes the npm root.", key);
                }

                normalized.RemoveAt(normalized.Count - 1);
                continue;
            }

            normalized.Add(part);
        }

        return string.Join('/', normalized);
    }

    private static JsonDocument ReadJson(string path, string source)
    {
        byte[] bytes = ReadBounded(
            path,
            MaximumJsonBytes,
            NoticeDiagnosticCodes.InvalidDependencyGraph,
            "An npm JSON input exceeds the 16 MiB limit.",
            source);
        ValidateEscapedUnicode(bytes, source);
        try
        {
            JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumDepth,
            });
            try
            {
                int propertyCount = 0;
                ValidateJson(document.RootElement, source, 0, ref propertyCount);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw InvalidGraph("An npm JSON input is malformed, too deeply nested, or contains invalid UTF-8.", source);
        }
    }

    private static void ValidateEscapedUnicode(ReadOnlySpan<byte> json, string source)
    {
        bool inString = false;
        for (int index = 0; index < json.Length; index++)
        {
            byte current = json[index];
            if (!inString)
            {
                if (current == (byte)'"')
                {
                    inString = true;
                }

                continue;
            }

            if (current == (byte)'"')
            {
                inString = false;
                continue;
            }

            if (current != (byte)'\\')
            {
                continue;
            }

            if (++index >= json.Length)
            {
                return;
            }

            if (json[index] != (byte)'u')
            {
                continue;
            }

            if (!TryReadHexCodeUnit(json, index + 1, out int codeUnit))
            {
                return;
            }

            index += 4;
            if (codeUnit is >= 0xDC00 and <= 0xDFFF)
            {
                throw InvalidGraph("An npm JSON input contains an unpaired Unicode surrogate escape.", source);
            }

            if (codeUnit is not (>= 0xD800 and <= 0xDBFF))
            {
                continue;
            }

            if (index + 6 >= json.Length || json[index + 1] != (byte)'\\' || json[index + 2] != (byte)'u' ||
                !TryReadHexCodeUnit(json, index + 3, out int lowSurrogate) ||
                lowSurrogate is not (>= 0xDC00 and <= 0xDFFF))
            {
                throw InvalidGraph("An npm JSON input contains an unpaired Unicode surrogate escape.", source);
            }

            index += 6;
        }
    }

    private static bool TryReadHexCodeUnit(ReadOnlySpan<byte> json, int start, out int value)
    {
        value = 0;
        if (start < 0 || start + 4 > json.Length)
        {
            return false;
        }

        for (int index = start; index < start + 4; index++)
        {
            int digit = json[index] switch
            {
                >= (byte)'0' and <= (byte)'9' => json[index] - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => json[index] - (byte)'a' + 10,
                >= (byte)'A' and <= (byte)'F' => json[index] - (byte)'A' + 10,
                _ => -1,
            };
            if (digit < 0)
            {
                return false;
            }

            value = (value << 4) | digit;
        }

        return true;
    }

    private static byte[] ReadBounded(
        string path,
        int maximumBytes,
        string code,
        string limitMessage,
        string source)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
        {
            throw new NpmInventoryException(code, limitMessage, source);
        }

        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new NpmInventoryException(code, limitMessage, source);
        }

        return bytes;
    }

    private static void ValidateJson(JsonElement element, string source, int depth, ref int propertyCount)
    {
        if (depth > MaximumDepth)
        {
            throw InvalidGraph("An npm JSON input exceeds the nesting limit.", source);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> properties = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string propertyName;
                try
                {
                    propertyName = property.Name;
                }
                catch (InvalidOperationException)
                {
                    throw InvalidGraph("An npm JSON property name contains invalid Unicode.", source);
                }

                if (!IsWellFormedUtf16(propertyName))
                {
                    throw InvalidGraph("An npm JSON property name contains invalid Unicode.", source);
                }

                propertyCount++;
                if (propertyCount > MaximumProperties)
                {
                    throw InvalidGraph("An npm JSON input exceeds the property-count limit.", source);
                }

                if (!properties.Add(propertyName))
                {
                    throw InvalidGraph("An npm JSON object contains a duplicate property.", source);
                }

                ValidateJson(property.Value, source, depth + 1, ref propertyCount);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                ValidateJson(item, source, depth + 1, ref propertyCount);
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            string? value;
            try
            {
                value = element.GetString();
            }
            catch (InvalidOperationException)
            {
                throw InvalidGraph("An npm JSON string contains invalid Unicode.", source);
            }

            if (value is null || !IsWellFormedUtf16(value))
            {
                throw InvalidGraph("An npm JSON string contains invalid Unicode.", source);
            }
        }
    }

    private static bool IsWellFormedUtf16(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (char.IsLowSurrogate(current))
            {
                return false;
            }

            if (!char.IsHighSurrogate(current))
            {
                continue;
            }

            if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string ResolveSafePath(string root, string relative, bool requireFile)
    {
        string normalized = NormalizeRelativePath(relative, allowDot: false);
        string candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, PathComparison) || StringComparerForPaths.Equals(candidate, root))
        {
            throw InvalidGraph("An npm inventory path escapes the declared root.", relative);
        }

        string current = root;
        string[] segments = normalized.Split('/');
        for (int index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            bool exists = File.Exists(current) || Directory.Exists(current);
            if (!exists)
            {
                if (requireFile || index != segments.Length - 1)
                {
                    throw new IOException("Required path does not exist.");
                }

                break;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw InvalidGraph("An npm inventory path crosses a symbolic link or reparse point.", relative);
            }
        }

        return candidate;
    }

    private static string NormalizeRelativePath(string path, bool allowDot)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') ||
            path.Contains(':') || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw InvalidGraph("An npm inventory path must be a normalized relative path.", path);
        }

        if (allowDot && StringComparer.Ordinal.Equals(path, "."))
        {
            return ".";
        }

        string[] segments = path.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".." ||
                                           segment.Any(char.IsControl)))
        {
            throw InvalidGraph("An npm inventory path contains an unsafe segment.", path);
        }

        return string.Join('/', segments);
    }

    private static int RequireInteger(JsonElement element, string name, string source)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || !value.TryGetInt32(out int result))
        {
            throw InvalidGraph($"The npm lockfile requires an integer '{name}' property.", source);
        }

        return result;
    }

    private static JsonElement RequireObject(JsonElement element, string name, string source)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidGraph($"The npm lockfile requires an object '{name}' property.", source);
        }

        return value;
    }

    private static string? OptionalString(JsonElement element, string name, string source)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidGraph($"The npm '{name}' property must be a string.", source);
        }

        return value.GetString();
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement element, string name, string source)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return result;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidGraph($"The npm '{name}' property must be an object.", source);
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                throw InvalidGraph($"The npm '{name}' property contains a non-string or empty value.", source);
            }

            result.Add(property.Name, property.Value.GetString()!);
        }

        return result;
    }

    private static bool IsExactVersion(string version)
    {
        int metadata = version.IndexOfAny(['-', '+']);
        string core = metadata < 0 ? version : version[..metadata];
        string suffix = metadata < 0 ? string.Empty : version[metadata..];
        string[] parts = core.Split('.');
        if (parts.Length != 3 || parts.Any(static part =>
                part.Length == 0 || part.Any(static character => character is < '0' or > '9') ||
                (part.Length > 1 && part[0] == '0')))
        {
            return false;
        }

        return suffix.All(static character =>
            (character >= 'a' && character <= 'z') ||
            (character >= 'A' && character <= 'Z') ||
            (character >= '0' && character <= '9') ||
            character is '-' or '+' or '.');
    }

    private static bool IsValidIntegrity(string integrity)
    {
        foreach (string token in integrity.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = token.IndexOf('-');
            if (separator <= 0 || separator == token.Length - 1)
            {
                return false;
            }

            string algorithm = token[..separator];
            if (algorithm is not ("sha256" or "sha384" or "sha512"))
            {
                return false;
            }

            byte[] buffer = new byte[token.Length];
            if (Base64.DecodeFromUtf8(
                    Encoding.ASCII.GetBytes(token[(separator + 1)..]),
                    buffer,
                    out _,
                    out int bytesWritten) != System.Buffers.OperationStatus.Done)
            {
                return false;
            }

            int expectedLength = algorithm switch
            {
                "sha256" => 32,
                "sha384" => 48,
                _ => 64,
            };
            if (bytesWritten != expectedLength)
            {
                return false;
            }
        }

        return integrity.Length != 0;
    }

    private static DependencyScope Classify(LockEntry entry)
    {
        if (entry.InBundle)
        {
            return DependencyScope.Bundled;
        }

        if (entry.Optional)
        {
            return DependencyScope.Optional;
        }

        if (entry.Peer)
        {
            return DependencyScope.Peer;
        }

        return entry.Dev ? DependencyScope.Development : DependencyScope.Runtime;
    }

    private static DependencyScope MergeScope(DependencyScope left, DependencyScope right)
    {
        static int Rank(DependencyScope value) => value switch
        {
            DependencyScope.Bundled => 5,
            DependencyScope.Optional => 4,
            DependencyScope.Peer => 3,
            DependencyScope.Runtime => 2,
            DependencyScope.Development => 1,
            _ => 0,
        };

        return Rank(left) >= Rank(right) ? left : right;
    }

    private static string DerivePackageName(string key)
    {
        int marker = key.LastIndexOf("node_modules/", StringComparison.Ordinal);
        if (marker < 0)
        {
            throw InvalidGraph("An npm package entry has no package name.", key);
        }

        return key[(marker + "node_modules/".Length)..];
    }

    private static string CreatePackageUrl(string name, string version)
    {
        static string Encode(string value) => Uri.EscapeDataString(value).Replace("%2F", "/", StringComparison.Ordinal);

        if (name.StartsWith('@'))
        {
            int slash = name.IndexOf('/');
            if (slash <= 1 || slash == name.Length - 1 || name.IndexOf('/', slash + 1) >= 0)
            {
                throw new FormatException("Invalid scoped npm name.");
            }

            return "pkg:npm/" + Encode(name[..slash]) + "/" + Encode(name[(slash + 1)..]) + "@" + Encode(version);
        }

        if (name.Contains('/'))
        {
            throw new FormatException("Invalid npm name.");
        }

        return "pkg:npm/" + Encode(name) + "@" + Encode(version);
    }

    private static string SanitizeName(string name)
    {
        StringBuilder result = new(Math.Min(name.Length, 128));
        foreach (char character in name.Take(128))
        {
            result.Append(char.IsControl(character) ? '?' : character);
        }

        return result.ToString();
    }

    private static void Add(
        List<NoticeDiagnostic> diagnostics,
        string code,
        string message,
        string? source,
        string? purl = null,
        NoticeDiagnosticSeverity severity = NoticeDiagnosticSeverity.Error) =>
        diagnostics.Add(new NoticeDiagnostic(code, severity, message, purl, source));

    private static NpmInventoryException InvalidGraph(string message, string? source) =>
        new(NoticeDiagnosticCodes.InvalidDependencyGraph, message, source);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer StringComparerForPaths =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record PendingDependency(
        string RequesterKey,
        string Name,
        DependencyScope Scope,
        bool IsDirect);

    private sealed record RootDependency(string Specifier, DependencyScope Scope);

    private sealed record IncludedPackage(LockEntry Entry, DependencyScope Scope, bool IsDirect);

    private sealed record LockEntry(
        string Key,
        string? Name,
        string? Version,
        string? Integrity,
        string? Resolved,
        bool Link,
        bool Dev,
        bool Optional,
        bool Peer,
        bool InBundle,
        Dictionary<string, string> Dependencies,
        Dictionary<string, string> DevDependencies,
        Dictionary<string, string> OptionalDependencies,
        Dictionary<string, string> PeerDependencies,
        ReadOnlyCollection<string> BundledDependencies)
    {
        public static LockEntry Parse(JsonElement element, string key)
        {
            string source = key.Length == 0 ? "package-lock.json" : key;
            return new LockEntry(
                key,
                OptionalString(element, "name", source),
                OptionalString(element, "version", source),
                OptionalString(element, "integrity", source),
                OptionalString(element, "resolved", source),
                ReadBoolean(element, "link", source),
                ReadBoolean(element, "dev", source),
                ReadBoolean(element, "optional", source),
                ReadBoolean(element, "peer", source),
                ReadBoolean(element, "inBundle", source),
                ReadStringMap(element, "dependencies", source),
                ReadStringMap(element, "devDependencies", source),
                ReadStringMap(element, "optionalDependencies", source),
                ReadStringMap(element, "peerDependencies", source),
                ReadStringArray(element, "bundledDependencies", source));
        }

        private static bool ReadBoolean(JsonElement element, string name, string source)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                return false;
            }

            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw InvalidGraph($"The npm '{name}' property must be a boolean.", source);
            }

            return value.GetBoolean();
        }

        private static ReadOnlyCollection<string> ReadStringArray(JsonElement element, string name, string source)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
            {
                return Array.AsReadOnly(Array.Empty<string>());
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                throw InvalidGraph($"The npm '{name}' property must be an array.", source);
            }

            List<string> result = [];
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                {
                    throw InvalidGraph($"The npm '{name}' property contains an invalid package name.", source);
                }

                result.Add(item.GetString()!);
            }

            result.Sort(StringComparer.Ordinal);
            return result.AsReadOnly();
        }
    }

    private sealed class NpmInventoryException(string code, string message, string? diagnosticSource) : Exception(message)
    {
        public string Code { get; } = code;

        public string? DiagnosticSource { get; } = diagnosticSource;
    }
}
