using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Policy;

/// <summary>Stable diagnostic identifiers emitted by the versioned policy layer.</summary>
public static class PolicyDiagnosticCodes
{
    public const string InvalidSpdxExpression = NoticeDiagnosticCodes.InvalidSpdxExpression;
    public const string UnresolvedLicenseReference = NoticeDiagnosticCodes.UnresolvedLicenseReference;
    public const string InvalidConfiguration = NoticeDiagnosticCodes.InvalidPolicyConfiguration;
    public const string UnsupportedSchemaVersion = NoticeDiagnosticCodes.UnsupportedPolicySchema;
    public const string LicenseDenied = NoticeDiagnosticCodes.LicenseDenied;
    public const string LicenseReviewRequired = NoticeDiagnosticCodes.LicenseReviewRequired;
    public const string MissingObligationOrEvidence = NoticeDiagnosticCodes.MissingLicenseObligation;
    public const string ExplicitSelectionRequired = NoticeDiagnosticCodes.ExplicitLicenseSelectionRequired;
    public const string InvalidSelection = NoticeDiagnosticCodes.InvalidLicenseSelection;
    public const string ExpiredOverride = NoticeDiagnosticCodes.ExpiredPolicyOverride;
    public const string VersionStaleOverride = NoticeDiagnosticCodes.VersionStalePolicyOverride;
    public const string ConflictingOverride = NoticeDiagnosticCodes.ConflictingPolicyOverride;
    public const string UnusedOverride = NoticeDiagnosticCodes.UnusedPolicyOverride;
    public const string InvalidOverrideMetadata = NoticeDiagnosticCodes.InvalidPolicyOverrideMetadata;
    public const string OverrideEvidenceMismatch = NoticeDiagnosticCodes.PolicyOverrideEvidenceMismatch;
}
