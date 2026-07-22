using System;
using System.Collections.Generic;

namespace WebUIToolkit.DependencyNotices.Diagnostics;

public enum NoticeDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record NoticeDiagnostic(
    string Code,
    NoticeDiagnosticSeverity Severity,
    string Message,
    string? PackageUrl = null,
    string? Source = null,
    int? Offset = null,
    string? Remediation = null);

public static class NoticeDiagnosticCodes
{
    public const string InvalidPackageUrl = "WUTNOTICE1001";
    public const string InvalidManualComponent = "WUTNOTICE1002";
    public const string DuplicatePackageUrl = "WUTNOTICE1003";
    public const string AmbiguousTarget = "WUTNOTICE1004";
    public const string LockFileDrift = "WUTNOTICE1005";
    public const string UnsupportedInventoryFormat = "WUTNOTICE1006";
    public const string UnresolvedDependency = "WUTNOTICE1007";
    public const string InvalidDependencyGraph = "WUTNOTICE1008";
    public const string MissingEvidence = "WUTNOTICE2001";
    public const string EvidenceDigestMismatch = "WUTNOTICE2002";
    public const string MultipleEvidenceCandidates = "WUTNOTICE2003";
    public const string UrlOnlyEvidence = "WUTNOTICE2004";
    public const string InvalidEvidenceEncoding = "WUTNOTICE2005";
    public const string InvalidSpdxExpression = "WUTNOTICE3001";
    public const string UnresolvedLicenseReference = "WUTNOTICE3002";
    public const string InvalidPolicyConfiguration = "WUTNOTICE3003";
    public const string UnsupportedPolicySchema = "WUTNOTICE3004";
    public const string LicenseDenied = "WUTNOTICE4001";
    public const string LicenseReviewRequired = "WUTNOTICE4002";
    public const string MissingLicenseObligation = "WUTNOTICE4003";
    public const string ExplicitLicenseSelectionRequired = "WUTNOTICE4004";
    public const string InvalidLicenseSelection = "WUTNOTICE4005";
    public const string ExpiredPolicyOverride = "WUTNOTICE4006";
    public const string VersionStalePolicyOverride = "WUTNOTICE4007";
    public const string ConflictingPolicyOverride = "WUTNOTICE4008";
    public const string UnusedPolicyOverride = "WUTNOTICE4009";
    public const string InvalidPolicyOverrideMetadata = "WUTNOTICE4010";
    public const string PolicyOverrideEvidenceMismatch = "WUTNOTICE4011";
    public const string SbomComponentMissing = "WUTNOTICE5001";
    public const string SbomComponentExtra = "WUTNOTICE5002";
    public const string SbomIdentityMismatch = "WUTNOTICE5003";
    public const string DuplicateSbomReference = "WUTNOTICE5004";
    public const string UnsafePath = "WUTNOTICE6001";
    public const string OutputDrift = "WUTNOTICE6002";
    public const string SchemaIncompatible = "WUTNOTICE6003";
    public const string UnsafeOutputDestination = "WUTNOTICE6004";
    public const string NetworkAccessForbidden = "WUTNOTICE7001";
    public const string AcquisitionOriginBlocked = "WUTNOTICE7002";
    public const string AcquisitionRedirectBlocked = "WUTNOTICE7003";
    public const string AcquisitionSizeLimit = "WUTNOTICE7004";
    public const string AcquisitionDigestMismatch = "WUTNOTICE7005";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        InvalidPackageUrl,
        InvalidManualComponent,
        DuplicatePackageUrl,
        AmbiguousTarget,
        LockFileDrift,
        UnsupportedInventoryFormat,
        UnresolvedDependency,
        InvalidDependencyGraph,
        MissingEvidence,
        EvidenceDigestMismatch,
        MultipleEvidenceCandidates,
        UrlOnlyEvidence,
        InvalidEvidenceEncoding,
        InvalidSpdxExpression,
        UnresolvedLicenseReference,
        InvalidPolicyConfiguration,
        UnsupportedPolicySchema,
        LicenseDenied,
        LicenseReviewRequired,
        MissingLicenseObligation,
        ExplicitLicenseSelectionRequired,
        InvalidLicenseSelection,
        ExpiredPolicyOverride,
        VersionStalePolicyOverride,
        ConflictingPolicyOverride,
        UnusedPolicyOverride,
        InvalidPolicyOverrideMetadata,
        PolicyOverrideEvidenceMismatch,
        SbomComponentMissing,
        SbomComponentExtra,
        SbomIdentityMismatch,
        DuplicateSbomReference,
        UnsafePath,
        OutputDrift,
        SchemaIncompatible,
        UnsafeOutputDestination,
        NetworkAccessForbidden,
        AcquisitionOriginBlocked,
        AcquisitionRedirectBlocked,
        AcquisitionSizeLimit,
        AcquisitionDigestMismatch,
    ]);
}
