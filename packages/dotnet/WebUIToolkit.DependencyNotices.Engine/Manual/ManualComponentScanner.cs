using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Evidence;
using WebUIToolkit.DependencyNotices.Spdx;

namespace WebUIToolkit.DependencyNotices.Engine;

public static class ManualComponentScanner
{
    private const int MaxConfigBytes = 1_048_576;
    private const int MaxEvidenceBytes = 16_777_216;
    private const int MaxJsonPropertiesAndItems = 100_000;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static ManualScanResult Scan(string rootDirectory, string configRelativePath)
    {
        List<NoticeDiagnostic> diagnostics = [];
        List<ManualDependencyComponent> components = [];
        byte[] configBytes;
        try
        {
            string configPath = SafePath.ResolveContainedPath(rootDirectory, configRelativePath);
            configBytes = ReadBounded(configPath, MaxConfigBytes);
        }
        catch (NoticeSecurityException exception)
        {
            diagnostics.Add(new NoticeDiagnostic(exception.Code, NoticeDiagnosticSeverity.Error, exception.Message, Source: configRelativePath));
            return new ManualScanResult(components, diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            diagnostics.Add(InvalidConfig(configRelativePath, "The manual configuration is unavailable or exceeds its byte limit."));
            return new ManualScanResult(components, diagnostics);
        }

        try
        {
            _ = StrictUtf8.GetString(configBytes);
            using JsonDocument document = JsonDocument.Parse(configBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            JsonElement root = document.RootElement;
            int propertyAndItemCount = 0;
            EnsureNoDuplicateProperties(root, ref propertyAndItemCount);
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number ||
                version.GetInt32() != 1 ||
                !root.TryGetProperty("manualComponents", out JsonElement entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(InvalidConfig(configRelativePath, "Expected schemaVersion 1 and a manualComponents array."));
                return new ManualScanResult(components, diagnostics);
            }

            HashSet<string> identities = new(StringComparer.Ordinal);
            int index = 0;
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                ParseEntry(rootDirectory, entry, index++, identities, components, diagnostics);
            }
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new NoticeDiagnostic(
                NoticeDiagnosticCodes.InvalidManualComponent,
                NoticeDiagnosticSeverity.Error,
                $"Invalid manual component JSON: {exception.Message}",
                Source: configRelativePath,
                Offset: exception.BytePositionInLine is long offset && offset <= int.MaxValue ? (int)offset : null,
                Remediation: "Validate the document against dependency-notices.schema.v1.json."));
        }
        catch (DecoderFallbackException)
        {
            diagnostics.Add(InvalidConfig(configRelativePath, "The manual configuration is not valid UTF-8."));
        }
        catch (InvalidOperationException)
        {
            diagnostics.Add(InvalidConfig(configRelativePath, "The manual configuration contains malformed Unicode or an invalid value kind."));
        }

        components.Sort(DependencyComponentComparer.Instance);
        return new ManualScanResult(components, diagnostics);
    }

    private static void ParseEntry(
        string rootDirectory,
        JsonElement entry,
        int index,
        HashSet<string> identities,
        List<ManualDependencyComponent> components,
        List<NoticeDiagnostic> diagnostics)
    {
        string source = $"/manualComponents/{index}";
        if (entry.ValueKind != JsonValueKind.Object ||
            !TryString(entry, "purl", out string? purlText) ||
            !TryString(entry, "displayName", out string? displayName) ||
            !TryString(entry, "revision", out string? revision) ||
            !TryString(entry, "licenseExpression", out string? licenseExpression))
        {
            diagnostics.Add(InvalidConfig(source, "A manual component requires purl, displayName, revision, and licenseExpression strings."));
            return;
        }

        if (ContainsInvalidControl(displayName!) || ContainsInvalidControl(revision!) || ContainsInvalidControl(licenseExpression!))
        {
            diagnostics.Add(InvalidConfig(source, "Manual component text contains a disallowed control character."));
            return;
        }

        PackageUrl packageUrl;
        try
        {
            packageUrl = PackageUrl.Parse(purlText!);
        }
        catch (FormatException exception)
        {
            diagnostics.Add(new NoticeDiagnostic(NoticeDiagnosticCodes.InvalidPackageUrl, NoticeDiagnosticSeverity.Error, exception.Message, purlText, source));
            return;
        }

        if (!identities.Add(packageUrl.CanonicalValue))
        {
            diagnostics.Add(new NoticeDiagnostic(NoticeDiagnosticCodes.DuplicatePackageUrl, NoticeDiagnosticSeverity.Error, "The canonical Package URL occurs more than once.", packageUrl.CanonicalValue, source));
            return;
        }

        SpdxExpression parsedLicense;
        try
        {
            parsedLicense = SpdxParser.Parse(licenseExpression!);
        }
        catch (SpdxParseException exception)
        {
            diagnostics.Add(new NoticeDiagnostic(NoticeDiagnosticCodes.InvalidSpdxExpression, NoticeDiagnosticSeverity.Error, exception.Message, packageUrl.CanonicalValue, source, exception.Offset, "Correct the SPDX expression syntax."));
            return;
        }

        bool modified = entry.TryGetProperty("modified", out JsonElement modifiedValue) && modifiedValue.ValueKind == JsonValueKind.True;
        string? modificationNotice = TryString(entry, "modificationNotice", out string? notice) ? notice : null;
        if (modified && string.IsNullOrWhiteSpace(modificationNotice))
        {
            diagnostics.Add(InvalidConfig(source, "Modified components require a modificationNotice."));
            return;
        }

        List<NoticeEvidence> evidence = [];
        if (!entry.TryGetProperty("evidence", out JsonElement evidenceEntries) || evidenceEntries.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(new NoticeDiagnostic(NoticeDiagnosticCodes.MissingEvidence, NoticeDiagnosticSeverity.Error, "The component has no evidence array.", packageUrl.CanonicalValue, source));
            return;
        }

        int evidenceIndex = 0;
        foreach (JsonElement evidenceEntry in evidenceEntries.EnumerateArray())
        {
            ParseEvidence(rootDirectory, packageUrl, evidenceEntry, $"{source}/evidence/{evidenceIndex++}", evidence, diagnostics);
        }

        if (evidence.Count == 0)
        {
            diagnostics.Add(new NoticeDiagnostic(NoticeDiagnosticCodes.MissingEvidence, NoticeDiagnosticSeverity.Error, "The component has no valid evidence.", packageUrl.CanonicalValue, source));
            return;
        }

        foreach (string licenseReference in EnumerateLicenseReferences(parsedLicense.Root))
        {
            if (!EvidenceContains(rootDirectory, evidence, licenseReference))
            {
                diagnostics.Add(new NoticeDiagnostic(
                    NoticeDiagnosticCodes.UnresolvedLicenseReference,
                    NoticeDiagnosticSeverity.Error,
                    $"The custom identifier '{licenseReference}' is not linked to evidence that identifies it.",
                    packageUrl.CanonicalValue,
                    source));
                return;
            }
        }

        components.Add(new ManualDependencyComponent(
            packageUrl,
            displayName!,
            packageUrl.Version,
            revision,
            licenseExpression!,
            evidence.AsReadOnly(),
            modified,
            modificationNotice));
    }

    private static void ParseEvidence(
        string rootDirectory,
        PackageUrl packageUrl,
        JsonElement entry,
        string source,
        List<NoticeEvidence> evidence,
        List<NoticeDiagnostic> diagnostics)
    {
        if (!TryString(entry, "kind", out string? kindText) ||
            !Enum.TryParse(kindText, ignoreCase: true, out NoticeAssetKind kind) ||
            !TryString(entry, "path", out string? relativePath) ||
            !TryString(entry, "origin", out string? origin))
        {
            diagnostics.Add(InvalidConfig(source, "Evidence requires a known kind, relative path, and origin."));
            return;
        }

        if (!TryString(entry, "sha256", out string? expectedDigest) || !EvidenceDigest.IsCanonicalSha256(expectedDigest))
        {
            diagnostics.Add(new NoticeDiagnostic(
                NoticeDiagnosticCodes.EvidenceDigestMismatch,
                NoticeDiagnosticSeverity.Error,
                "Evidence SHA-256 must contain exactly 64 lowercase hexadecimal characters.",
                packageUrl.CanonicalValue,
                source));
            return;
        }

        if (ContainsInvalidControl(origin!))
        {
            diagnostics.Add(InvalidConfig(source, "Evidence origin contains a disallowed control character."));
            return;
        }

        if (!IsSafeEvidenceOrigin(origin!))
        {
            diagnostics.Add(InvalidConfig(source, "Evidence origin must be an opaque review identifier or a safe non-file URI."));
            return;
        }

        if (Uri.TryCreate(relativePath, UriKind.Absolute, out Uri? evidenceUri) && evidenceUri.Scheme is "http" or "https")
        {
            diagnostics.Add(new NoticeDiagnostic(
                NoticeDiagnosticCodes.NetworkAccessForbidden,
                NoticeDiagnosticSeverity.Error,
                "Remote evidence cannot be accessed by an offline operation.",
                packageUrl.CanonicalValue,
                source));
            return;
        }

        string fullPath;
        try
        {
            fullPath = SafePath.ResolveContainedPath(rootDirectory, relativePath!);
        }
        catch (NoticeSecurityException exception)
        {
            diagnostics.Add(new NoticeDiagnostic(exception.Code, NoticeDiagnosticSeverity.Error, exception.Message, packageUrl.CanonicalValue, source));
            return;
        }

        if (!File.Exists(fullPath))
        {
            diagnostics.Add(new NoticeDiagnostic(NoticeDiagnosticCodes.MissingEvidence, NoticeDiagnosticSeverity.Error, "The declared evidence file does not exist.", packageUrl.CanonicalValue, source));
            return;
        }

        byte[] bytes;
        try
        {
            bytes = ReadBounded(fullPath, MaxEvidenceBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            diagnostics.Add(new NoticeDiagnostic(
                NoticeDiagnosticCodes.InvalidEvidenceEncoding,
                NoticeDiagnosticSeverity.Error,
                "Evidence is unavailable or exceeds its byte limit.",
                packageUrl.CanonicalValue,
                source));
            return;
        }

        if (kind is NoticeAssetKind.License or NoticeAssetKind.Notice or NoticeAssetKind.Attribution or NoticeAssetKind.Authors or NoticeAssetKind.Modification)
        {
            try
            {
                _ = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                diagnostics.Add(new NoticeDiagnostic(
                    NoticeDiagnosticCodes.InvalidEvidenceEncoding,
                    NoticeDiagnosticSeverity.Error,
                    "Text evidence is not valid UTF-8.",
                    packageUrl.CanonicalValue,
                    source));
                return;
            }
        }

        string actualDigest = EvidenceDigest.ComputeSha256(bytes);
        if (!StringComparer.Ordinal.Equals(expectedDigest, actualDigest))
        {
            diagnostics.Add(new NoticeDiagnostic(NoticeDiagnosticCodes.EvidenceDigestMismatch, NoticeDiagnosticSeverity.Error, $"Evidence digest mismatch; expected {expectedDigest}, actual {actualDigest}.", packageUrl.CanonicalValue, source));
            return;
        }

        evidence.Add(new NoticeEvidence(
            kind,
            expectedDigest!,
            relativePath!.Replace('\\', '/'),
            TryString(entry, "mediaType", out string? mediaType) ? mediaType : null,
            origin));
    }

    private static IEnumerable<string> EnumerateLicenseReferences(SpdxExpressionNode node)
    {
        switch (node)
        {
            case SpdxLicenseIdentifierNode license when license.Identifier.Contains("LicenseRef-", StringComparison.Ordinal):
                yield return EvidenceIdentifier(license.Identifier);
                break;
            case SpdxWithExceptionNode with when with.License.Identifier.Contains("LicenseRef-", StringComparison.Ordinal):
                yield return EvidenceIdentifier(with.License.Identifier);
                break;
            case SpdxAndNode and:
                foreach (string identifier in EnumerateLicenseReferences(and.Left))
                {
                    yield return identifier;
                }

                foreach (string identifier in EnumerateLicenseReferences(and.Right))
                {
                    yield return identifier;
                }

                break;
            case SpdxOrNode or:
                foreach (string identifier in EnumerateLicenseReferences(or.Left))
                {
                    yield return identifier;
                }

                foreach (string identifier in EnumerateLicenseReferences(or.Right))
                {
                    yield return identifier;
                }

                break;
        }
    }

    private static string EvidenceIdentifier(string identifier)
    {
        int separator = identifier.IndexOf(':', StringComparison.Ordinal);
        return separator < 0 ? identifier : identifier[(separator + 1)..];
    }

    private static bool EvidenceContains(string rootDirectory, List<NoticeEvidence> evidence, string value)
    {
        foreach (NoticeEvidence item in evidence)
        {
            byte[] bytes = ReadBounded(SafePath.ResolveContainedPath(rootDirectory, item.Path), MaxEvidenceBytes);
            if (StrictUtf8.GetString(bytes).Contains(value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsInvalidControl(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if ((char.IsControl(character) && character is not '\t' and not '\n' and not '\r') ||
                character == '\uFFFD')
            {
                return true;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return true;
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSafeEvidenceOrigin(string origin)
    {
        if (Path.IsPathRooted(origin) || origin.StartsWith("//", StringComparison.Ordinal) ||
            origin.StartsWith("\\\\", StringComparison.Ordinal) || origin.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            (origin.Length >= 3 && IsAsciiLetter(origin[0]) && origin[1] == ':' && (origin[2] == '/' || origin[2] == '\\')))
        {
            return false;
        }

        if (Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return true;
    }

    private static bool IsAsciiLetter(char value) =>
        (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException($"Input exceeds the {maximumBytes} byte limit.");
        }

        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static bool TryString(JsonElement element, string name, out string? value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }

    private static void EnsureNoDuplicateProperties(JsonElement element, ref int propertyAndItemCount)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                propertyAndItemCount++;
                if (propertyAndItemCount > MaxJsonPropertiesAndItems)
                {
                    throw new JsonException("The JSON property and item limit was exceeded.");
                }

                if (!names.Add(property.Name))
                {
                    throw new JsonException($"Duplicate property '{property.Name}' is not allowed.");
                }

                EnsureNoDuplicateProperties(property.Value, ref propertyAndItemCount);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                propertyAndItemCount++;
                if (propertyAndItemCount > MaxJsonPropertiesAndItems)
                {
                    throw new JsonException("The JSON property and item limit was exceeded.");
                }

                EnsureNoDuplicateProperties(item, ref propertyAndItemCount);
            }
        }
    }

    private static NoticeDiagnostic InvalidConfig(string source, string message) =>
        new(NoticeDiagnosticCodes.InvalidManualComponent, NoticeDiagnosticSeverity.Error, message, Source: source, Remediation: "Validate the document against dependency-notices.schema.v1.json.");
}
