using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WebUIToolkit.DependencyNotices.Runtime.Serialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class NoticeDocumentJson
{
    [JsonRequired]
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonRequired]
    [JsonPropertyName("artifactName")]
    public string? ArtifactName { get; set; }

    [JsonPropertyName("artifactVersion")]
    public string? ArtifactVersion { get; set; }

    [JsonRequired]
    [JsonPropertyName("dependencies")]
    public List<NoticeDependencyJson>? Dependencies { get; set; }

    [JsonRequired]
    [JsonPropertyName("diagnostics")]
    public List<NoticeDiagnosticJson>? Diagnostics { get; set; }

    [JsonPropertyName("sbom")]
    public NoticeSbomJson? Sbom { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class NoticeDependencyJson
{
    [JsonRequired]
    [JsonPropertyName("packageUrl")]
    public string? PackageUrl { get; set; }

    [JsonRequired]
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonRequired]
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonRequired]
    [JsonPropertyName("ecosystem")]
    public string? Ecosystem { get; set; }

    [JsonRequired]
    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonRequired]
    [JsonPropertyName("isDirect")]
    public bool IsDirect { get; set; }

    [JsonRequired]
    [JsonPropertyName("observedLicenseExpression")]
    public string? ObservedLicenseExpression { get; set; }

    [JsonRequired]
    [JsonPropertyName("effectiveLicenseExpression")]
    public string? EffectiveLicenseExpression { get; set; }

    [JsonPropertyName("selectedLicenseExpression")]
    public string? SelectedLicenseExpression { get; set; }

    [JsonRequired]
    [JsonPropertyName("assets")]
    public List<NoticeAssetJson>? Assets { get; set; }

    [JsonRequired]
    [JsonPropertyName("decisions")]
    public List<NoticeDecisionJson>? Decisions { get; set; }

    [JsonRequired]
    [JsonPropertyName("isModified")]
    public bool IsModified { get; set; }

    [JsonPropertyName("modificationNotice")]
    public string? ModificationNotice { get; set; }

    [JsonPropertyName("sbomComponentReference")]
    public string? SbomComponentReference { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class NoticeAssetJson
{
    [JsonRequired]
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonRequired]
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonRequired]
    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    [JsonRequired]
    [JsonPropertyName("origin")]
    public string? Origin { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonRequired]
    [JsonPropertyName("isOverride")]
    public bool IsOverride { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class NoticeSbomJson
{
    [JsonRequired]
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonRequired]
    [JsonPropertyName("documentReference")]
    public string? DocumentReference { get; set; }

    [JsonPropertyName("serialNumber")]
    public string? SerialNumber { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class NoticeDecisionJson
{
    [JsonRequired]
    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonRequired]
    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    [JsonRequired]
    [JsonPropertyName("rule")]
    public string? Rule { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class NoticeDiagnosticJson
{
    [JsonRequired]
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonRequired]
    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonRequired]
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("packageUrl")]
    public string? PackageUrl { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("offset")]
    public int? Offset { get; set; }

    [JsonPropertyName("remediation")]
    public string? Remediation { get; set; }
}
