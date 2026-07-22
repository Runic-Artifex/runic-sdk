using System;
using System.Collections.Generic;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Policy;

public sealed record PolicyEvaluationOptions(
    DateOnly EvaluationDate,
    bool ReleaseMode = true,
    PolicyDiagnosticLevel ExpiredOverride = PolicyDiagnosticLevel.Error,
    PolicyDiagnosticLevel UnusedOverride = PolicyDiagnosticLevel.Error);

public sealed record PolicyEvaluationInput(
    PackageUrl PackageUrl,
    string ObservedLicenseExpression,
    string? SelectedLicenseExpression,
    IReadOnlySet<string> LicenseEvidenceSha256,
    IReadOnlySet<string> FulfilledObligations);

public sealed record PolicySubjectDecision(
    string Subject,
    PolicyDecision Decision,
    string MatchedRule,
    IReadOnlyList<string> RequiredObligations);

public sealed record ComponentPolicyEvaluation(
    string PackageUrl,
    string ObservedExpression,
    string EffectiveExpression,
    string? SelectedExpression,
    string? AppliedOverrideId,
    PolicyDecision Decision,
    IReadOnlyList<PolicySubjectDecision> SubjectDecisions,
    IReadOnlyList<NoticeDiagnostic> Diagnostics);

public sealed record PolicyEvaluationReport(
    IReadOnlyList<ComponentPolicyEvaluation> Components,
    IReadOnlyList<NoticeDiagnostic> Diagnostics)
{
    public bool HasErrors
    {
        get
        {
            foreach (NoticeDiagnostic diagnostic in Diagnostics)
            {
                if (diagnostic.Severity == NoticeDiagnosticSeverity.Error)
                {
                    return true;
                }
            }

            foreach (ComponentPolicyEvaluation component in Components)
            {
                foreach (NoticeDiagnostic diagnostic in component.Diagnostics)
                {
                    if (diagnostic.Severity == NoticeDiagnosticSeverity.Error)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
