using System.Collections.Generic;

namespace WebUIToolkit.DependencyNotices.Runtime;

public enum NoticeEcosystem
{
    Generic,
    NuGet,
    Npm,
}

public enum NoticeDependencyScope
{
    Runtime,
    Development,
    Optional,
    Peer,
    Bundled,
    Unknown,
}

public enum NoticeDecisionOutcome
{
    Allow,
    Deny,
    Review,
}

public enum NoticeDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record NoticeAsset(
    string Kind,
    string Sha256,
    string MediaType,
    string? Text,
    string Origin,
    bool IsOverride);

public sealed record NoticeSbomLink(
    string Format,
    string DocumentReference,
    string? SerialNumber);

public sealed record NoticeDecision(
    string Subject,
    NoticeDecisionOutcome Outcome,
    string Rule);

public sealed record NoticeDiagnostic(
    string Code,
    NoticeDiagnosticSeverity Severity,
    string Message,
    string? PackageUrl,
    string? Source,
    int? Offset,
    string? Remediation);

public sealed record NoticeDependency(
    string PackageUrl,
    string Name,
    string Version,
    NoticeEcosystem Ecosystem,
    NoticeDependencyScope Scope,
    bool IsDirect,
    string ObservedLicenseExpression,
    string EffectiveLicenseExpression,
    string? SelectedLicenseExpression,
    IReadOnlyList<NoticeAsset> Assets,
    IReadOnlyList<NoticeDecision> Decisions,
    string? SbomComponentReference,
    bool IsModified,
    string? ModificationNotice);

public sealed class NoticeDocument
{
    internal NoticeDocument(
        int schemaVersion,
        string artifactName,
        string? artifactVersion,
        IReadOnlyList<NoticeDependency> dependencies,
        NoticeSbomLink? sbom,
        IReadOnlyList<NoticeDiagnostic> diagnostics)
    {
        SchemaVersion = schemaVersion;
        ArtifactName = artifactName;
        ArtifactVersion = artifactVersion;
        Dependencies = dependencies;
        Sbom = sbom;
        Diagnostics = diagnostics;
    }

    public int SchemaVersion { get; }

    public string ArtifactName { get; }

    public string? ArtifactVersion { get; }

    public IReadOnlyList<NoticeDependency> Dependencies { get; }

    public NoticeSbomLink? Sbom { get; }

    public IReadOnlyList<NoticeDiagnostic> Diagnostics { get; }
}
