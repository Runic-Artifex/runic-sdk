using System.Text.Json;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Rendering;

public static class CanonicalJsonNoticeRenderer
{
    public static byte[] Render(DependencyNoticeDocument document)
    {
        return RenderingUtilities.WriteJson(writer => WriteDocument(writer, document));
    }

    private static void WriteDocument(Utf8JsonWriter writer, DependencyNoticeDocument document)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", document.SchemaVersion);
        writer.WriteString("artifactName", document.ArtifactName);
        if (document.ArtifactVersion is null)
        {
            writer.WriteNull("artifactVersion");
        }
        else
        {
            writer.WriteString("artifactVersion", document.ArtifactVersion);
        }

        writer.WritePropertyName("dependencies");
        writer.WriteStartArray();
        foreach (DependencyNotice dependency in NoticeOrdering.Dependencies(document.Dependencies))
        {
            WriteDependency(writer, dependency);
        }

        writer.WriteEndArray();
        writer.WritePropertyName("sbom");
        if (document.Sbom is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("format", document.Sbom.Format);
            writer.WriteString("documentReference", document.Sbom.DocumentReference);
            if (document.Sbom.SerialNumber is null)
            {
                writer.WriteNull("serialNumber");
            }
            else
            {
                writer.WriteString("serialNumber", document.Sbom.SerialNumber);
            }

            writer.WriteEndObject();
        }

        writer.WritePropertyName("diagnostics");
        writer.WriteStartArray();
        foreach (NoticeDiagnostic diagnostic in NoticeOrdering.Diagnostics(document.Diagnostics))
        {
            WriteDiagnostic(writer, diagnostic);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteDependency(Utf8JsonWriter writer, DependencyNotice dependency)
    {
        writer.WriteStartObject();
        writer.WriteString("packageUrl", dependency.PackageUrl);
        writer.WriteString("name", dependency.Name);
        writer.WriteString("version", dependency.Version);
        writer.WriteString("ecosystem", RenderingUtilities.EnumToken(dependency.Ecosystem));
        writer.WriteString("scope", RenderingUtilities.EnumToken(dependency.Scope));
        writer.WriteBoolean("isDirect", dependency.IsDirect);
        writer.WriteString("observedLicenseExpression", dependency.ObservedLicenseExpression);
        writer.WriteString("effectiveLicenseExpression", dependency.EffectiveLicenseExpression);
        WriteNullableString(writer, "selectedLicenseExpression", dependency.SelectedLicenseExpression);
        writer.WritePropertyName("assets");
        writer.WriteStartArray();
        foreach (NoticeAsset asset in NoticeOrdering.Assets(dependency.Assets))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", RenderingUtilities.EnumToken(asset.Kind));
            writer.WriteString("sha256", asset.Sha256);
            writer.WriteString("mediaType", asset.MediaType);
            writer.WriteString("text", asset.Text);
            writer.WriteString("origin", asset.Origin);
            writer.WriteBoolean("isOverride", asset.IsOverride);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("decisions");
        writer.WriteStartArray();
        foreach (NoticePolicyDecision decision in NoticeOrdering.Decisions(dependency.Decisions))
        {
            writer.WriteStartObject();
            writer.WriteString("subject", decision.Subject);
            writer.WriteString("outcome", RenderingUtilities.EnumToken(decision.Outcome));
            writer.WriteString("rule", decision.Rule);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteNullableString(writer, "sbomComponentReference", dependency.SbomComponentReference);
        writer.WriteBoolean("isModified", dependency.IsModified);
        WriteNullableString(writer, "modificationNotice", dependency.ModificationNotice);
        writer.WriteEndObject();
    }

    private static void WriteDiagnostic(Utf8JsonWriter writer, NoticeDiagnostic diagnostic)
    {
        writer.WriteStartObject();
        writer.WriteString("code", diagnostic.Code);
        writer.WriteString("severity", RenderingUtilities.EnumToken(diagnostic.Severity));
        writer.WriteString("message", diagnostic.Message);
        WriteNullableString(writer, "packageUrl", diagnostic.PackageUrl);
        WriteNullableString(writer, "source", diagnostic.Source);
        if (diagnostic.Offset is null)
        {
            writer.WriteNull("offset");
        }
        else
        {
            writer.WriteNumber("offset", diagnostic.Offset.Value);
        }

        WriteNullableString(writer, "remediation", diagnostic.Remediation);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }
}
