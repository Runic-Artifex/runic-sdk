using System.Collections.Generic;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Policy;

namespace WebUIToolkit.DependencyNotices;

public sealed record DependencyNoticeDocument(
    int SchemaVersion,
    string ArtifactName,
    string? ArtifactVersion,
    IReadOnlyList<DependencyNotice> Dependencies,
    SbomLink? Sbom,
    IReadOnlyList<NoticeDiagnostic> Diagnostics);

public sealed record DependencyNotice(
    string PackageUrl,
    string Name,
    string Version,
    DependencyEcosystem Ecosystem,
    DependencyScope Scope,
    bool IsDirect,
    string ObservedLicenseExpression,
    string EffectiveLicenseExpression,
    string? SelectedLicenseExpression,
    IReadOnlyList<NoticeAsset> Assets,
    IReadOnlyList<NoticePolicyDecision> Decisions,
    string? SbomComponentReference,
    bool IsModified,
    string? ModificationNotice);

public sealed record NoticeAsset(
    NoticeAssetKind Kind,
    string Sha256,
    string MediaType,
    string Text,
    string Origin,
    bool IsOverride);

public sealed record NoticePolicyDecision(
    string Subject,
    LicensePolicyOutcome Outcome,
    string Rule);

public sealed record SbomLink(
    string Format,
    string DocumentReference,
    string? SerialNumber);
