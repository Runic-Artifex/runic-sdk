using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Evidence;
using WebUIToolkit.DependencyNotices.Spdx;

namespace WebUIToolkit.DependencyNotices.Engine;

public static class ManualComponentScanner
{
    private const int MaxConfigBytes = 1_048_576;
    private const int MaxEvidenceBytes = 16_777_216;

    public static ManualScanResult Scan(string rootDirectory, string configRelativePath)
    {
        string configPath = SafePath.ResolveContainedPath(rootDirectory, configRelativePath);
        byte[] configBytes = ReadBounded(configPath, MaxConfigBytes);
        List<NoticeDiagnostic> diagnostics = [];
        List<ManualDependencyComponent> components = [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(configBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            JsonElement root = document.RootElement;
            EnsureNoDuplicateProperties(root);
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

        byte[] bytes = ReadBounded(fullPath, MaxEvidenceBytes);
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
            if (System.Text.Encoding.UTF8.GetString(bytes).Contains(value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsInvalidControl(string value)
    {
        foreach (char character in value)
        {
            if (char.IsControl(character) && character is not '\t' and not '\n' and not '\r')
            {
                return true;
            }
        }

        return false;
    }

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

    private static void EnsureNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException($"Duplicate property '{property.Name}' is not allowed.");
                }

                EnsureNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                EnsureNoDuplicateProperties(item);
            }
        }
    }

    private static NoticeDiagnostic InvalidConfig(string source, string message) =>
        new(NoticeDiagnosticCodes.InvalidManualComponent, NoticeDiagnosticSeverity.Error, message, Source: source, Remediation: "Validate the document against dependency-notices.schema.v1.json.");
}
