using System;
using System.Collections.Generic;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Spdx;

namespace WebUIToolkit.DependencyNotices.Policy;

public sealed record LicensePolicyEvaluation(
    string ObservedExpression,
    string EffectiveExpression,
    string? SelectedExpression,
    LicensePolicyOutcome Outcome,
    IReadOnlyList<NoticeDiagnostic> Diagnostics);

public static class LicensePolicyEvaluator
{
    public static LicensePolicyEvaluation Evaluate(
        PackageUrl packageUrl,
        string observedExpression,
        string? selectedLicenseExpression,
        bool hasLicenseEvidence,
        LicensePolicy policy,
        IReadOnlySet<string>? fulfilledObligations = null)
    {
        ArgumentNullException.ThrowIfNull(packageUrl);
        ArgumentNullException.ThrowIfNull(policy);
        List<NoticeDiagnostic> diagnostics = [];
        SpdxExpression observed;
        try
        {
            observed = SpdxParser.Parse(observedExpression);
        }
        catch (SpdxParseException exception)
        {
            diagnostics.Add(new NoticeDiagnostic(
                NoticeDiagnosticCodes.InvalidSpdxExpression,
                NoticeDiagnosticSeverity.Error,
                exception.Message,
                packageUrl.CanonicalValue,
                Offset: exception.Offset,
                Remediation: $"Expected {exception.Expected}."));
            return new LicensePolicyEvaluation(observedExpression, observedExpression, null, LicensePolicyOutcome.Deny, diagnostics);
        }

        if (!hasLicenseEvidence)
        {
            diagnostics.Add(new NoticeDiagnostic(
                NoticeDiagnosticCodes.MissingEvidence,
                NoticeDiagnosticSeverity.Error,
                "No exact license evidence is linked to the component.",
                packageUrl.CanonicalValue,
                Remediation: "Pin the raw evidence bytes by lowercase SHA-256."));
        }

        SpdxExpressionNode effectiveNode = observed.Root;
        string? canonicalSelection = null;
        if (ContainsOr(observed.Root) && policy.RequireExplicitOrSelection)
        {
            if (string.IsNullOrWhiteSpace(selectedLicenseExpression))
            {
                diagnostics.Add(new NoticeDiagnostic(
                    NoticeDiagnosticCodes.ExplicitLicenseSelectionRequired,
                    NoticeDiagnosticSeverity.Error,
                    "The OR expression requires an explicit selected license branch.",
                    packageUrl.CanonicalValue));
            }
            else
            {
                try
                {
                    SpdxExpression selected = SpdxParser.Parse(selectedLicenseExpression);
                    canonicalSelection = selected.Canonical;
                    if (ContainsOr(selected.Root) || !IsSelectionOf(observed.Root, selected.Root))
                    {
                        diagnostics.Add(new NoticeDiagnostic(
                            NoticeDiagnosticCodes.InvalidLicenseSelection,
                            NoticeDiagnosticSeverity.Error,
                            "The selected license is not an exact branch of the observed OR expression.",
                            packageUrl.CanonicalValue));
                    }
                    else
                    {
                        effectiveNode = selected.Root;
                    }
                }
                catch (SpdxParseException exception)
                {
                    diagnostics.Add(new NoticeDiagnostic(
                        NoticeDiagnosticCodes.InvalidLicenseSelection,
                        NoticeDiagnosticSeverity.Error,
                        $"The selected license expression is invalid: {exception.Message}",
                        packageUrl.CanonicalValue,
                        Offset: exception.Offset));
                }
            }
        }

        LicensePolicyOutcome outcome = EvaluateNode(packageUrl, effectiveNode, hasLicenseEvidence, policy, fulfilledObligations, diagnostics);
        foreach (NoticeDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Severity == NoticeDiagnosticSeverity.Error)
            {
                outcome = LicensePolicyOutcome.Deny;
                break;
            }
        }

        return new LicensePolicyEvaluation(
            observed.Canonical,
            SpdxExpressionFormatter.Format(effectiveNode),
            canonicalSelection,
            outcome,
            diagnostics.AsReadOnly());
    }

    private static LicensePolicyOutcome EvaluateNode(
        PackageUrl packageUrl,
        SpdxExpressionNode node,
        bool hasLicenseEvidence,
        LicensePolicy policy,
        IReadOnlySet<string>? fulfilledObligations,
        List<NoticeDiagnostic> diagnostics)
    {
        if (node is SpdxAndNode and)
        {
            LicensePolicyOutcome left = EvaluateNode(packageUrl, and.Left, hasLicenseEvidence, policy, fulfilledObligations, diagnostics);
            LicensePolicyOutcome right = EvaluateNode(packageUrl, and.Right, hasLicenseEvidence, policy, fulfilledObligations, diagnostics);
            return Worst(left, right);
        }

        if (node is SpdxOrNode or)
        {
            List<NoticeDiagnostic> leftDiagnostics = [];
            List<NoticeDiagnostic> rightDiagnostics = [];
            LicensePolicyOutcome left = EvaluateNode(packageUrl, or.Left, hasLicenseEvidence, policy, fulfilledObligations, leftDiagnostics);
            LicensePolicyOutcome right = EvaluateNode(packageUrl, or.Right, hasLicenseEvidence, policy, fulfilledObligations, rightDiagnostics);
            bool chooseLeft = left <= right;
            diagnostics.AddRange(chooseLeft ? leftDiagnostics : rightDiagnostics);
            return chooseLeft ? left : right;
        }

        string subject = SpdxExpressionFormatter.Format(node);
        if (node is SpdxLicenseIdentifierNode license && license.Identifier.Contains("LicenseRef-", StringComparison.Ordinal) && !hasLicenseEvidence)
        {
            diagnostics.Add(new NoticeDiagnostic(
                NoticeDiagnosticCodes.UnresolvedLicenseReference,
                NoticeDiagnosticSeverity.Warning,
                $"The custom identifier '{license.Identifier}' has no exact evidence link.",
                packageUrl.CanonicalValue));
        }

        LicensePolicyOutcome outcome;
        if (Matches(policy.Denied, subject))
        {
            outcome = LicensePolicyOutcome.Deny;
            diagnostics.Add(new NoticeDiagnostic(NoticeDiagnosticCodes.LicenseDenied, NoticeDiagnosticSeverity.Error, $"Policy denied '{subject}'.", packageUrl.CanonicalValue));
        }
        else if (Matches(policy.Allowed, subject))
        {
            outcome = LicensePolicyOutcome.Allow;
        }
        else if (Matches(policy.Review, subject))
        {
            outcome = LicensePolicyOutcome.Review;
            diagnostics.Add(new NoticeDiagnostic(NoticeDiagnosticCodes.LicenseReviewRequired, NoticeDiagnosticSeverity.Warning, $"Policy requires review of '{subject}'.", packageUrl.CanonicalValue));
        }
        else
        {
            outcome = policy.DefaultOutcome;
            if (outcome == LicensePolicyOutcome.Deny)
            {
                diagnostics.Add(new NoticeDiagnostic(NoticeDiagnosticCodes.LicenseDenied, NoticeDiagnosticSeverity.Error, $"Default policy denied '{subject}'.", packageUrl.CanonicalValue));
            }
            else if (outcome == LicensePolicyOutcome.Review)
            {
                diagnostics.Add(new NoticeDiagnostic(NoticeDiagnosticCodes.LicenseReviewRequired, NoticeDiagnosticSeverity.Warning, $"Default policy requires review of '{subject}'.", packageUrl.CanonicalValue));
            }
        }

        if (policy.Obligations.TryGetValue(subject, out IReadOnlyList<string>? obligations))
        {
            foreach (string obligation in obligations)
            {
                if (fulfilledObligations is null || !fulfilledObligations.Contains(obligation))
                {
                    diagnostics.Add(new NoticeDiagnostic(
                        NoticeDiagnosticCodes.MissingLicenseObligation,
                        NoticeDiagnosticSeverity.Error,
                        $"The required obligation '{obligation}' is not fulfilled for '{subject}'.",
                        packageUrl.CanonicalValue));
                    outcome = LicensePolicyOutcome.Deny;
                }
            }
        }

        return outcome;
    }

    private static bool ContainsOr(SpdxExpressionNode node) => node switch
    {
        SpdxOrNode => true,
        SpdxAndNode and => ContainsOr(and.Left) || ContainsOr(and.Right),
        _ => false,
    };

    private static bool IsSelectionOf(SpdxExpressionNode observed, SpdxExpressionNode selected) => observed switch
    {
        SpdxOrNode or => IsSelectionOf(or.Left, selected) || IsSelectionOf(or.Right, selected),
        SpdxAndNode observedAnd when selected is SpdxAndNode selectedAnd =>
            IsSelectionOf(observedAnd.Left, selectedAnd.Left) && IsSelectionOf(observedAnd.Right, selectedAnd.Right),
        _ => observed.Equals(selected),
    };

    private static bool Matches(IReadOnlySet<string> values, string subject)
    {
        if (values.Contains(subject))
        {
            return true;
        }

        foreach (string value in values)
        {
            if (value.EndsWith('*') && subject.StartsWith(value.AsSpan(0, value.Length - 1), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static LicensePolicyOutcome Worst(LicensePolicyOutcome left, LicensePolicyOutcome right) =>
        (LicensePolicyOutcome)Math.Max((int)left, (int)right);

}
