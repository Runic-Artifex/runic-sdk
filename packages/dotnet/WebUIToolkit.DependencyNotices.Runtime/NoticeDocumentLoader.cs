using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using WebUIToolkit.DependencyNotices.Runtime.Serialization;

namespace WebUIToolkit.DependencyNotices.Runtime;

public static class NoticeDocumentLoader
{
    public const int CurrentSchemaVersion = 2;

    public const int MinimumSupportedSchemaVersion = 1;

    public static NoticeDocument Load(string path, NoticeLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Load(stream, options);
    }

    public static NoticeDocument Load(Stream stream, NoticeLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        options ??= new NoticeLoadOptions();
        options.Validate();

        ArrayBufferWriter<byte> buffer = new(Math.Min(options.MaxDocumentBytes, 64 * 1024));
        while (true)
        {
            int remaining = options.MaxDocumentBytes - buffer.WrittenCount;
            if (remaining == 0)
            {
                int extra = stream.ReadByte();
                if (extra >= 0)
                {
                    throw new NoticeDocumentException($"The document exceeds the {options.MaxDocumentBytes} byte limit.");
                }

                break;
            }

            Span<byte> destination = buffer.GetSpan(Math.Min(remaining, 16 * 1024));
            int read = stream.Read(destination[..Math.Min(destination.Length, remaining)]);
            if (read == 0)
            {
                break;
            }

            buffer.Advance(read);
        }

        return Load(buffer.WrittenSpan, options);
    }

    public static NoticeDocument Load(ReadOnlySpan<byte> utf8Json, NoticeLoadOptions? options = null)
    {
        options ??= new NoticeLoadOptions();
        options.Validate();
        if (utf8Json.Length > options.MaxDocumentBytes)
        {
            throw new NoticeDocumentException($"The document exceeds the {options.MaxDocumentBytes} byte limit.");
        }

        try
        {
            List<HashSet<string>> objectShapes = ValidateJsonShape(utf8Json, options);
            NoticeDocumentJson? json = JsonSerializer.Deserialize(utf8Json, NoticeJsonContext.Default.NoticeDocumentJson);
            if (json is null)
            {
                throw new NoticeDocumentException("The document root must be an object.");
            }

            ValidateVersionSpecificShape(json.SchemaVersion, objectShapes);
            return Convert(json, options);
        }
        catch (NoticeDocumentException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new NoticeDocumentException($"Invalid dependency notice JSON: {exception.Message}", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new NoticeDocumentException("Invalid dependency notice JSON string.", exception);
        }
    }

    public static NoticeDocument Load(ReadOnlyMemory<byte> utf8Json, NoticeLoadOptions? options = null) =>
        Load(utf8Json.Span, options);

    private static List<HashSet<string>> ValidateJsonShape(ReadOnlySpan<byte> utf8Json, NoticeLoadOptions options)
    {
        Utf8JsonReader reader = new(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = options.MaxDepth,
        });

        List<HashSet<string>> objectProperties = [];
        List<HashSet<string>> completedObjects = [];
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
            {
                long length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
                if (length > options.MaxStringBytes)
                {
                    throw new NoticeDocumentException($"A JSON string exceeds the {options.MaxStringBytes} byte limit.");
                }

                ValidateSurrogateEscapes(reader.ValueSpan);
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Add(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.PropertyName:
                    if (objectProperties.Count == 0)
                    {
                        throw new NoticeDocumentException("A property appeared outside an object.");
                    }

                    string propertyName = reader.GetString()!;
                    if (!objectProperties[^1].Add(propertyName))
                    {
                        throw new NoticeDocumentException($"Duplicate JSON property '{propertyName}' is not allowed.");
                    }

                    break;
                case JsonTokenType.EndObject:
                    completedObjects.Add(objectProperties[^1]);
                    objectProperties.RemoveAt(objectProperties.Count - 1);
                    break;
            }
        }

        return completedObjects;
    }

    private static void ValidateSurrogateEscapes(ReadOnlySpan<byte> value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != (byte)'\\')
            {
                continue;
            }

            if (index + 1 >= value.Length)
            {
                return;
            }

            if (value[index + 1] != (byte)'u')
            {
                index++;
                continue;
            }

            if (!TryParseHexQuad(value, index + 2, out int codeUnit))
            {
                return;
            }

            if (codeUnit is >= 0xdc00 and <= 0xdfff)
            {
                throw new NoticeDocumentException("JSON string contains an unpaired Unicode surrogate escape.");
            }

            if (codeUnit is >= 0xd800 and <= 0xdbff)
            {
                bool hasLowSurrogate = index + 11 < value.Length
                    && value[index + 6] == (byte)'\\'
                    && value[index + 7] == (byte)'u'
                    && TryParseHexQuad(value, index + 8, out int lowCodeUnit)
                    && lowCodeUnit is >= 0xdc00 and <= 0xdfff;
                if (!hasLowSurrogate)
                {
                    throw new NoticeDocumentException("JSON string contains an unpaired Unicode surrogate escape.");
                }

                index += 11;
                continue;
            }

            index += 5;
        }
    }

    private static bool TryParseHexQuad(ReadOnlySpan<byte> value, int offset, out int codeUnit)
    {
        codeUnit = 0;
        if (offset < 0 || offset + 4 > value.Length)
        {
            return false;
        }

        for (int index = offset; index < offset + 4; index++)
        {
            int digit = value[index] switch
            {
                >= (byte)'0' and <= (byte)'9' => value[index] - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => value[index] - (byte)'a' + 10,
                >= (byte)'A' and <= (byte)'F' => value[index] - (byte)'A' + 10,
                _ => -1,
            };
            if (digit < 0)
            {
                return false;
            }

            codeUnit = (codeUnit << 4) | digit;
        }

        return true;
    }

    private static void ValidateVersionSpecificShape(int schemaVersion, List<HashSet<string>> objectShapes)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            return;
        }

        foreach (HashSet<string> properties in objectShapes)
        {
            if (properties.Contains("schemaVersion"))
            {
                RequireProperties(properties, "document", "schemaVersion", "artifactName", "artifactVersion", "dependencies", "sbom", "diagnostics");
            }
            else if (properties.Contains("packageUrl") && properties.Contains("ecosystem"))
            {
                RequireProperties(
                    properties,
                    "dependency",
                    "packageUrl",
                    "name",
                    "version",
                    "ecosystem",
                    "scope",
                    "isDirect",
                    "observedLicenseExpression",
                    "effectiveLicenseExpression",
                    "selectedLicenseExpression",
                    "assets",
                    "decisions",
                    "sbomComponentReference",
                    "isModified",
                    "modificationNotice");
            }
            else if (properties.Contains("sha256"))
            {
                RequireProperties(properties, "asset", "kind", "sha256", "mediaType", "text", "origin", "isOverride");
            }
            else if (properties.Contains("outcome"))
            {
                RequireProperties(properties, "decision", "subject", "outcome", "rule");
            }
            else if (properties.Contains("documentReference"))
            {
                RequireProperties(properties, "sbom", "format", "documentReference", "serialNumber");
            }
            else if (properties.Contains("code") && properties.Contains("severity"))
            {
                RequireProperties(
                    properties,
                    "diagnostic",
                    "code",
                    "severity",
                    "message",
                    "packageUrl",
                    "source",
                    "offset",
                    "remediation");
            }
        }
    }

    private static void RequireProperties(HashSet<string> properties, string objectName, params string[] required)
    {
        foreach (string property in required)
        {
            if (!properties.Contains(property))
            {
                throw new NoticeDocumentException($"Schema version 2 {objectName} is missing required property '{property}'.");
            }
        }
    }

    private static NoticeDocument Convert(NoticeDocumentJson json, NoticeLoadOptions options)
    {
        if (json.SchemaVersion is < MinimumSupportedSchemaVersion or > CurrentSchemaVersion)
        {
            throw new NoticeDocumentException(
                $"Schema version {json.SchemaVersion} is incompatible; supported versions are {MinimumSupportedSchemaVersion} through {CurrentSchemaVersion}.");
        }

        string artifactName = Required(json.ArtifactName, "artifactName", allowEmpty: false);
        List<NoticeDependencyJson> dependencyJson = json.Dependencies
            ?? throw new NoticeDocumentException("Property 'dependencies' cannot be null.");
        List<NoticeDiagnosticJson> diagnosticJson = json.Diagnostics
            ?? throw new NoticeDocumentException("Property 'diagnostics' cannot be null.");
        EnforceCount(dependencyJson.Count, options.MaxDependencies, "dependencies");
        EnforceCount(diagnosticJson.Count, options.MaxDiagnostics, "diagnostics");

        NoticeDependency[] dependencies = new NoticeDependency[dependencyJson.Count];
        HashSet<string> packageUrls = new(StringComparer.Ordinal);
        for (int index = 0; index < dependencies.Length; index++)
        {
            NoticeDependencyJson dependency = dependencyJson[index]
                ?? throw new NoticeDocumentException($"Dependency at index {index} cannot be null.");
            string packageUrl = Required(dependency.PackageUrl, $"dependencies[{index}].packageUrl", allowEmpty: false);
            if (!packageUrl.StartsWith("pkg:", StringComparison.Ordinal))
            {
                throw new NoticeDocumentException($"Dependency at index {index} has an invalid Package URL.");
            }

            if (!packageUrls.Add(packageUrl))
            {
                throw new NoticeDocumentException($"Duplicate dependency Package URL '{packageUrl}' is not allowed.");
            }

            List<NoticeAssetJson> assetJson = dependency.Assets
                ?? throw new NoticeDocumentException($"Property 'dependencies[{index}].assets' cannot be null.");
            List<NoticeDecisionJson> decisionJson = dependency.Decisions
                ?? throw new NoticeDocumentException($"Property 'dependencies[{index}].decisions' cannot be null.");
            EnforceCount(assetJson.Count, options.MaxAssetsPerDependency, $"dependencies[{index}].assets");
            EnforceCount(decisionJson.Count, options.MaxDecisionsPerDependency, $"dependencies[{index}].decisions");

            NoticeAsset[] assets = new NoticeAsset[assetJson.Count];
            for (int assetIndex = 0; assetIndex < assets.Length; assetIndex++)
            {
                NoticeAssetJson asset = assetJson[assetIndex]
                    ?? throw new NoticeDocumentException($"Asset at dependencies[{index}].assets[{assetIndex}] cannot be null.");
                string digest = Required(asset.Sha256, "asset.sha256", allowEmpty: false);
                if (!IsLowerSha256(digest))
                {
                    throw new NoticeDocumentException($"Asset at dependencies[{index}].assets[{assetIndex}] has an invalid SHA-256 digest.");
                }

                string? text = json.SchemaVersion == CurrentSchemaVersion
                    ? Required(asset.Text, "asset.text", allowEmpty: true)
                    : asset.Text;
                assets[assetIndex] = new NoticeAsset(
                    Required(asset.Kind, "asset.kind", allowEmpty: false),
                    digest,
                    Required(asset.MediaType, "asset.mediaType", allowEmpty: false),
                    text,
                    Required(asset.Origin, "asset.origin", allowEmpty: false),
                    asset.IsOverride);

                if (json.SchemaVersion == 2 && !IsAssetKind(assets[assetIndex].Kind))
                {
                    throw new NoticeDocumentException($"Asset at dependencies[{index}].assets[{assetIndex}] has invalid kind '{assets[assetIndex].Kind}'.");
                }
            }

            NoticeDecision[] decisions = new NoticeDecision[decisionJson.Count];
            for (int decisionIndex = 0; decisionIndex < decisions.Length; decisionIndex++)
            {
                NoticeDecisionJson decision = decisionJson[decisionIndex]
                    ?? throw new NoticeDocumentException($"Decision at dependencies[{index}].decisions[{decisionIndex}] cannot be null.");
                decisions[decisionIndex] = new NoticeDecision(
                    Required(decision.Subject, "decision.subject", allowEmpty: json.SchemaVersion == MinimumSupportedSchemaVersion),
                    ParseOutcome(decision.Outcome, index, decisionIndex),
                    Required(decision.Rule, "decision.rule", allowEmpty: json.SchemaVersion == MinimumSupportedSchemaVersion));
            }

            dependencies[index] = new NoticeDependency(
                packageUrl,
                Required(dependency.Name, $"dependencies[{index}].name", allowEmpty: json.SchemaVersion == MinimumSupportedSchemaVersion),
                Required(dependency.Version, $"dependencies[{index}].version", allowEmpty: json.SchemaVersion == MinimumSupportedSchemaVersion),
                ParseEcosystem(dependency.Ecosystem, index),
                ParseScope(dependency.Scope, index),
                dependency.IsDirect,
                Required(dependency.ObservedLicenseExpression, $"dependencies[{index}].observedLicenseExpression", allowEmpty: json.SchemaVersion == MinimumSupportedSchemaVersion),
                Required(dependency.EffectiveLicenseExpression, $"dependencies[{index}].effectiveLicenseExpression", allowEmpty: json.SchemaVersion == MinimumSupportedSchemaVersion),
                dependency.SelectedLicenseExpression,
                Array.AsReadOnly(assets),
                Array.AsReadOnly(decisions),
                dependency.SbomComponentReference,
                dependency.IsModified,
                dependency.ModificationNotice);
        }

        Array.Sort(dependencies, NoticeDependencyComparer.Instance);

        NoticeDiagnostic[] diagnostics = new NoticeDiagnostic[diagnosticJson.Count];
        for (int index = 0; index < diagnostics.Length; index++)
        {
            NoticeDiagnosticJson diagnostic = diagnosticJson[index]
                ?? throw new NoticeDocumentException($"Diagnostic at index {index} cannot be null.");
            string code = Required(diagnostic.Code, $"diagnostics[{index}].code", allowEmpty: false);
            if (!IsDiagnosticCode(code))
            {
                throw new NoticeDocumentException($"Diagnostic at index {index} has invalid code '{code}'.");
            }

            if (diagnostic.Offset < 0)
            {
                throw new NoticeDocumentException($"Diagnostic at index {index} has a negative offset.");
            }

            diagnostics[index] = new NoticeDiagnostic(
                code,
                ParseSeverity(diagnostic.Severity, index),
                Required(diagnostic.Message, $"diagnostics[{index}].message", allowEmpty: true),
                diagnostic.PackageUrl,
                diagnostic.Source,
                diagnostic.Offset,
                diagnostic.Remediation);
        }

        NoticeSbomLink? sbom = null;
        if (json.Sbom is not null)
        {
            string format = Required(json.Sbom.Format, "sbom.format", allowEmpty: false);
            if (format is not ("cycloneDx" or "spdx"))
            {
                throw new NoticeDocumentException($"SBOM format '{format}' is invalid.");
            }

            sbom = new NoticeSbomLink(
                format,
                Required(json.Sbom.DocumentReference, "sbom.documentReference", allowEmpty: false),
                json.Sbom.SerialNumber);
        }

        return new NoticeDocument(
            json.SchemaVersion,
            artifactName,
            json.ArtifactVersion,
            Array.AsReadOnly(dependencies),
            sbom,
            Array.AsReadOnly(diagnostics));
    }

    private static string Required(string? value, string path, bool allowEmpty)
    {
        if (value is null || (!allowEmpty && value.Length == 0))
        {
            throw new NoticeDocumentException($"Property '{path}' must be a non-null{(allowEmpty ? string.Empty : ", non-empty")} string.");
        }

        return value;
    }

    private static void EnforceCount(int count, int maximum, string path)
    {
        if (count > maximum)
        {
            throw new NoticeDocumentException($"Array '{path}' has {count} entries, exceeding the {maximum} entry limit.");
        }
    }

    private static NoticeEcosystem ParseEcosystem(string? value, int index) => value switch
    {
        "generic" => NoticeEcosystem.Generic,
        "nuget" => NoticeEcosystem.NuGet,
        "npm" => NoticeEcosystem.Npm,
        _ => throw new NoticeDocumentException($"Dependency at index {index} has invalid ecosystem '{value}'."),
    };

    private static NoticeDependencyScope ParseScope(string? value, int index) => value switch
    {
        "runtime" => NoticeDependencyScope.Runtime,
        "development" => NoticeDependencyScope.Development,
        "optional" => NoticeDependencyScope.Optional,
        "peer" => NoticeDependencyScope.Peer,
        "bundled" => NoticeDependencyScope.Bundled,
        "unknown" => NoticeDependencyScope.Unknown,
        _ => throw new NoticeDocumentException($"Dependency at index {index} has invalid scope '{value}'."),
    };

    private static NoticeDecisionOutcome ParseOutcome(string? value, int dependencyIndex, int decisionIndex) => value switch
    {
        "allow" => NoticeDecisionOutcome.Allow,
        "deny" => NoticeDecisionOutcome.Deny,
        "review" => NoticeDecisionOutcome.Review,
        _ => throw new NoticeDocumentException(
            $"Decision at dependencies[{dependencyIndex}].decisions[{decisionIndex}] has invalid outcome '{value}'."),
    };

    private static NoticeDiagnosticSeverity ParseSeverity(string? value, int index) => value switch
    {
        "info" => NoticeDiagnosticSeverity.Info,
        "warning" => NoticeDiagnosticSeverity.Warning,
        "error" => NoticeDiagnosticSeverity.Error,
        _ => throw new NoticeDocumentException($"Diagnostic at index {index} has invalid severity '{value}'."),
    };

    private static bool IsLowerSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAssetKind(string value) => value is
        "license" or "notice" or "attribution" or "authors" or "modification";

    private static bool IsDiagnosticCode(string value)
    {
        const string prefix = "WUTNOTICE";
        if (value.Length != prefix.Length + 4 || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return value[prefix.Length] is >= '1' and <= '7'
            && value[prefix.Length + 1] is >= '0' and <= '9'
            && value[prefix.Length + 2] is >= '0' and <= '9'
            && value[prefix.Length + 3] is >= '0' and <= '9';
    }

    private sealed class NoticeDependencyComparer : IComparer<NoticeDependency>
    {
        public static NoticeDependencyComparer Instance { get; } = new();

        public int Compare(NoticeDependency? x, NoticeDependency? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            int byName = StringComparer.Ordinal.Compare(x.Name, y.Name);
            if (byName != 0)
            {
                return byName;
            }

            int byVersion = StringComparer.Ordinal.Compare(x.Version, y.Version);
            return byVersion != 0
                ? byVersion
                : StringComparer.Ordinal.Compare(x.PackageUrl, y.PackageUrl);
        }
    }
}
