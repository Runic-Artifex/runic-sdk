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
    public const string MissingEvidence = "WUTNOTICE2001";
    public const string EvidenceDigestMismatch = "WUTNOTICE2002";
    public const string InvalidSpdxExpression = "WUTNOTICE3001";
    public const string UnresolvedLicenseReference = "WUTNOTICE3002";
    public const string LicenseDenied = "WUTNOTICE4001";
    public const string LicenseReviewRequired = "WUTNOTICE4002";
    public const string MissingLicenseObligation = "WUTNOTICE4003";
    public const string ExplicitLicenseSelectionRequired = "WUTNOTICE4004";
    public const string InvalidLicenseSelection = "WUTNOTICE4005";
    public const string UnsafePath = "WUTNOTICE6001";
    public const string NetworkAccessForbidden = "WUTNOTICE7001";

    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
    [
        InvalidPackageUrl,
        InvalidManualComponent,
        DuplicatePackageUrl,
        MissingEvidence,
        EvidenceDigestMismatch,
        InvalidSpdxExpression,
        UnresolvedLicenseReference,
        LicenseDenied,
        LicenseReviewRequired,
        MissingLicenseObligation,
        ExplicitLicenseSelectionRequired,
        InvalidLicenseSelection,
        UnsafePath,
        NetworkAccessForbidden,
    ]);
}
