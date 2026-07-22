using System;
using System.Collections.Generic;
using System.Text;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Policy;
using WebUIToolkit.DependencyNotices.Spdx;

namespace WebUIToolkit.DependencyNotices.Rendering;

public static class DependencyNoticeRenderer
{
    public const int SupportedDocumentSchemaVersion = 2;

    public static NoticeRenderResult Render(DependencyNoticeDocument document, NoticeRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        List<NoticeDiagnostic> diagnostics = Validate(document, options);
        if (diagnostics.Count != 0)
        {
            return new NoticeRenderResult(Array.Empty<RenderedNoticeOutput>(), diagnostics.AsReadOnly());
        }

        try
        {
            List<RenderedNoticeOutput> outputs =
            [
                Create(NoticeOutputNames.Json, CanonicalJsonNoticeRenderer.Render(document)),
                Create(NoticeOutputNames.Text, ThirdPartyNoticesTextRenderer.Render(document)),
                Create(NoticeOutputNames.Html, StandaloneHtmlNoticeRenderer.Render(document)),
            ];
            byte[] manifest = NoticeManifestRenderer.Render(options, outputs);
            outputs.Add(Create(NoticeOutputNames.Manifest, manifest));
            outputs.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.FileName, right.FileName));
            return new NoticeRenderResult(outputs.AsReadOnly(), Array.Empty<NoticeDiagnostic>());
        }
        catch (ArgumentException)
        {
            return new NoticeRenderResult(
                Array.Empty<RenderedNoticeOutput>(),
                new[] { SchemaError("Document contains text that cannot be represented safely.") });
        }
    }

    private static RenderedNoticeOutput Create(string fileName, byte[] content) =>
        new(fileName, content, RenderingUtilities.Sha256(content));

    private static List<NoticeDiagnostic> Validate(DependencyNoticeDocument document, NoticeRenderOptions options)
    {
        List<NoticeDiagnostic> diagnostics = [];
        if (document.SchemaVersion != SupportedDocumentSchemaVersion)
        {
            diagnostics.Add(new NoticeDiagnostic(
                NoticeDiagnosticCodes.SchemaIncompatible,
                NoticeDiagnosticSeverity.Error,
                $"Document schema version {document.SchemaVersion} is incompatible with renderer schema version {SupportedDocumentSchemaVersion}.",
                Remediation: "Generate a schema version 2 dependency notice document."));
        }

        if (!IsRequired(document.ArtifactName))
        {
            diagnostics.Add(SchemaError("Artifact name must be non-empty."));
        }

        if (document.ArtifactVersion is not null && !IsRequired(document.ArtifactVersion))
        {
            diagnostics.Add(SchemaError("Artifact version must be non-empty when present."));
        }

        if (!IsRequired(options.ToolVersion))
        {
            diagnostics.Add(SchemaError("Tool version must be non-empty."));
        }

        HashSet<string> inputNames = new(StringComparer.Ordinal);
        foreach (NoticeManifestInput? input in options.Inputs ?? Array.Empty<NoticeManifestInput>())
        {
            if (input is null)
            {
                diagnostics.Add(SchemaError("Manifest inputs cannot contain null values."));
                continue;
            }

            if (!RenderingUtilities.IsPortableRelativeName(input.Name))
            {
                diagnostics.Add(UnsafeName("Manifest input name is not a portable relative name."));
            }

            if (!inputNames.Add(input.Name))
            {
                diagnostics.Add(SchemaError("Manifest input names must be unique."));
            }

            if (!RenderingUtilities.IsSha256(input.Sha256))
            {
                diagnostics.Add(SchemaError("Manifest input digest must be lowercase SHA-256."));
            }
        }

        if (options.EvidenceLockSha256 is not null && !RenderingUtilities.IsSha256(options.EvidenceLockSha256))
        {
            diagnostics.Add(SchemaError("Evidence lock digest must be lowercase SHA-256."));
        }

        foreach (string? root in options.SelectedRoots ?? Array.Empty<string>())
        {
            if (root is null || !RenderingUtilities.IsPortableRelativeName(root))
            {
                diagnostics.Add(UnsafeName("Selected root is not a portable relative name."));
            }
        }

        foreach (string? profile in options.Profiles ?? Array.Empty<string>())
        {
            if (!IsRequired(profile))
            {
                diagnostics.Add(SchemaError("Profile names must be non-empty."));
            }
        }

        if (options.Inputs is null || options.SelectedRoots is null || options.Profiles is null)
        {
            diagnostics.Add(SchemaError("Manifest input, root, and profile collections are required."));
        }

        Dictionary<string, string> evidenceByDigest = new(StringComparer.Ordinal);
        HashSet<string> packageUrls = new(StringComparer.Ordinal);
        foreach (DependencyNotice? dependency in document.Dependencies ?? Array.Empty<DependencyNotice>())
        {
            if (dependency is null)
            {
                diagnostics.Add(SchemaError("Dependencies cannot contain null values."));
                continue;
            }

            if (!TryCanonicalPackageUrl(dependency.PackageUrl, out PackageUrl? packageUrl))
            {
                diagnostics.Add(ComponentSchemaError("Dependency Package URL must be canonical and exact-versioned.", dependency.PackageUrl));
            }

            if (!packageUrls.Add(dependency.PackageUrl))
            {
                diagnostics.Add(new NoticeDiagnostic(
                    NoticeDiagnosticCodes.SchemaIncompatible,
                    NoticeDiagnosticSeverity.Error,
                    "Rendered dependency Package URLs must be unique.",
                    dependency.PackageUrl));
            }

            if (!IsRequired(dependency.Name) || !IsRequired(dependency.Version))
            {
                diagnostics.Add(ComponentSchemaError("Dependency name and version must be non-empty.", dependency.PackageUrl));
            }

            if (packageUrl is not null && !StringComparer.Ordinal.Equals(packageUrl.Version, dependency.Version))
            {
                diagnostics.Add(ComponentSchemaError("Dependency version must equal its Package URL version.", dependency.PackageUrl));
            }

            if (!Enum.IsDefined(dependency.Ecosystem) || !Enum.IsDefined(dependency.Scope))
            {
                diagnostics.Add(ComponentSchemaError("Dependency ecosystem and scope must be defined schema values.", dependency.PackageUrl));
            }

            if (packageUrl is not null && !EcosystemMatches(packageUrl.Type, dependency.Ecosystem))
            {
                diagnostics.Add(ComponentSchemaError("Dependency ecosystem must match its Package URL type.", dependency.PackageUrl));
            }

            ValidateSpdx(dependency.ObservedLicenseExpression, "Observed license expression", dependency.PackageUrl, diagnostics);
            ValidateSpdx(dependency.EffectiveLicenseExpression, "Effective license expression", dependency.PackageUrl, diagnostics);
            if (dependency.SelectedLicenseExpression is not null)
            {
                ValidateSpdx(dependency.SelectedLicenseExpression, "Selected license expression", dependency.PackageUrl, diagnostics);
            }

            if (dependency.SbomComponentReference is not null && !IsRequired(dependency.SbomComponentReference))
            {
                diagnostics.Add(ComponentSchemaError("SBOM component reference must be non-empty when present.", dependency.PackageUrl));
            }

            if (dependency.ModificationNotice is not null && !IsRequired(dependency.ModificationNotice))
            {
                diagnostics.Add(ComponentSchemaError("Modification notice must be non-empty when present.", dependency.PackageUrl));
            }

            if (dependency.Assets is null || dependency.Decisions is null)
            {
                diagnostics.Add(ComponentSchemaError("Dependency asset and decision collections are required.", dependency.PackageUrl));
                continue;
            }

            foreach (NoticePolicyDecision? decision in dependency.Decisions)
            {
                if (decision is null || !IsRequired(decision.Subject) || !IsRequired(decision.Rule) || !Enum.IsDefined(decision.Outcome))
                {
                    diagnostics.Add(ComponentSchemaError("Policy decisions require a subject, rule, and defined outcome.", dependency.PackageUrl));
                }
            }

            foreach (NoticeAsset? asset in dependency.Assets)
            {
                if (asset is null)
                {
                    diagnostics.Add(ComponentSchemaError("Notice assets cannot contain null values.", dependency.PackageUrl));
                    continue;
                }

                if (!Enum.IsDefined(asset.Kind) || !IsRequired(asset.MediaType) || !IsRequired(asset.Origin) || asset.Text is null)
                {
                    diagnostics.Add(ComponentSchemaError("Notice assets require a defined kind, media type, text, and origin.", dependency.PackageUrl));
                    continue;
                }

                if (!RenderingUtilities.IsSha256(asset.Sha256))
                {
                    diagnostics.Add(new NoticeDiagnostic(
                        NoticeDiagnosticCodes.SchemaIncompatible,
                        NoticeDiagnosticSeverity.Error,
                        "Notice asset digest must be lowercase SHA-256.",
                        dependency.PackageUrl));
                    continue;
                }

                try
                {
                    string actualDigest = RenderingUtilities.Sha256(RenderingUtilities.Utf8NoBom.GetBytes(asset.Text));
                    if (!StringComparer.Ordinal.Equals(actualDigest, asset.Sha256))
                    {
                        diagnostics.Add(ComponentSchemaError("Notice asset digest does not match its strict UTF-8 text bytes.", dependency.PackageUrl));
                        continue;
                    }
                }
                catch (EncoderFallbackException)
                {
                    diagnostics.Add(ComponentSchemaError("Notice asset text is not valid UTF-16 for strict UTF-8 encoding.", dependency.PackageUrl));
                    continue;
                }

                if (evidenceByDigest.TryGetValue(asset.Sha256, out string? existingText))
                {
                    if (!StringComparer.Ordinal.Equals(existingText, asset.Text))
                    {
                        diagnostics.Add(new NoticeDiagnostic(
                            NoticeDiagnosticCodes.SchemaIncompatible,
                            NoticeDiagnosticSeverity.Error,
                            "One evidence digest identifies conflicting text values.",
                            dependency.PackageUrl));
                    }
                }
                else
                {
                    evidenceByDigest.Add(asset.Sha256, asset.Text);
                }
            }
        }

        if (document.Dependencies is null || document.Diagnostics is null)
        {
            diagnostics.Add(SchemaError("Dependency and diagnostic collections are required."));
        }

        ValidateSbom(document.Sbom, diagnostics);
        foreach (NoticeDiagnostic? diagnostic in document.Diagnostics ?? Array.Empty<NoticeDiagnostic>())
        {
            ValidateDiagnostic(diagnostic, diagnostics);
        }

        diagnostics.Sort(static (left, right) =>
        {
            int comparison = StringComparer.Ordinal.Compare(left.Code, right.Code);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.PackageUrl, right.PackageUrl);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Message, right.Message);
        });
        return diagnostics;
    }

    private static NoticeDiagnostic SchemaError(string message) => new(
        NoticeDiagnosticCodes.SchemaIncompatible,
        NoticeDiagnosticSeverity.Error,
        message);

    private static NoticeDiagnostic UnsafeName(string message) => new(
        NoticeDiagnosticCodes.UnsafeOutputDestination,
        NoticeDiagnosticSeverity.Error,
        message,
        Remediation: "Use a portable repository-relative logical name.");

    private static NoticeDiagnostic ComponentSchemaError(string message, string? packageUrl) => new(
        NoticeDiagnosticCodes.SchemaIncompatible,
        NoticeDiagnosticSeverity.Error,
        message,
        PackageUrl.TryParse(packageUrl, out _) ? packageUrl : null);

    private static bool IsRequired(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool TryCanonicalPackageUrl(string? value, out PackageUrl? packageUrl)
    {
        if (!PackageUrl.TryParse(value, out packageUrl))
        {
            return false;
        }

        return StringComparer.Ordinal.Equals(value, packageUrl.CanonicalValue);
    }

    private static bool EcosystemMatches(string packageType, DependencyEcosystem ecosystem) => ecosystem switch
    {
        DependencyEcosystem.NuGet => packageType == "nuget",
        DependencyEcosystem.Npm => packageType == "npm",
        DependencyEcosystem.Generic => packageType != "nuget" && packageType != "npm",
        _ => false,
    };

    private static void ValidateSpdx(
        string? expression,
        string label,
        string? packageUrl,
        List<NoticeDiagnostic> diagnostics)
    {
        if (!IsRequired(expression))
        {
            diagnostics.Add(ComponentSchemaError(label + " must be non-empty.", packageUrl));
            return;
        }

        try
        {
            _ = SpdxParser.Parse(expression!);
        }
        catch (SpdxParseException)
        {
            diagnostics.Add(ComponentSchemaError(label + " must contain valid SPDX syntax.", packageUrl));
        }
    }

    private static void ValidateSbom(SbomLink? sbom, List<NoticeDiagnostic> diagnostics)
    {
        if (sbom is null)
        {
            return;
        }

        if (sbom.Format is not "cycloneDx" and not "spdx" || !IsRequired(sbom.DocumentReference))
        {
            diagnostics.Add(SchemaError("SBOM format and document reference must be valid schema values."));
        }

        if (sbom.SerialNumber is not null && !IsRequired(sbom.SerialNumber))
        {
            diagnostics.Add(SchemaError("SBOM serial number must be non-empty when present."));
        }
    }

    private static void ValidateDiagnostic(NoticeDiagnostic? diagnostic, List<NoticeDiagnostic> diagnostics)
    {
        if (diagnostic is null)
        {
            diagnostics.Add(SchemaError("Diagnostics cannot contain null values."));
            return;
        }

        if (!IsDiagnosticCode(diagnostic.Code) || !Enum.IsDefined(diagnostic.Severity) || !IsRequired(diagnostic.Message))
        {
            diagnostics.Add(SchemaError("Diagnostics require a valid code, severity, and non-empty message."));
        }

        if (diagnostic.Offset is < 0)
        {
            diagnostics.Add(SchemaError("Diagnostic offsets cannot be negative."));
        }

        if (diagnostic.PackageUrl is not null && !TryCanonicalPackageUrl(diagnostic.PackageUrl, out _))
        {
            diagnostics.Add(SchemaError("Diagnostic Package URL must be canonical when present."));
        }
    }

    private static bool IsDiagnosticCode(string? code)
    {
        if (code is null || code.Length != 13 || !code.StartsWith("WUTNOTICE", StringComparison.Ordinal) || code[9] is < '1' or > '7')
        {
            return false;
        }

        return code[10] is >= '0' and <= '9'
            && code[11] is >= '0' and <= '9'
            && code[12] is >= '0' and <= '9';
    }
}
