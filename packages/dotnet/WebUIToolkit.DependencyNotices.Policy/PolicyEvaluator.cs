using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Spdx;

namespace WebUIToolkit.DependencyNotices.Policy;

/// <summary>Evaluates versioned organizational policy without making legal conclusions.</summary>
public static class PolicyEvaluator
{
    public static PolicyEvaluationReport Evaluate(
        IEnumerable<PolicyEvaluationInput> inputs,
        PolicyConfiguration policy,
        PolicyEvaluationOptions options)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);
        if (policy.SchemaVersion != 1)
        {
            throw new ArgumentException("Only policy schema version 1 can be evaluated.", nameof(policy));
        }

        List<PolicyEvaluationInput> orderedInputs = [.. inputs];
        orderedInputs.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.PackageUrl.CanonicalValue, right.PackageUrl.CanonicalValue));
        Dictionary<string, List<PolicyOverride>> overridesByPurl = IndexOverrides(policy.Overrides);
        HashSet<string> observedPurls = new(StringComparer.Ordinal);
        List<ComponentPolicyEvaluation> components = [];
        foreach (PolicyEvaluationInput input in orderedInputs)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(input.PackageUrl);
            ArgumentNullException.ThrowIfNull(input.ObservedLicenseExpression);
            ArgumentNullException.ThrowIfNull(input.LicenseEvidenceSha256);
            ArgumentNullException.ThrowIfNull(input.FulfilledObligations);
            observedPurls.Add(input.PackageUrl.CanonicalValue);
            overridesByPurl.TryGetValue(input.PackageUrl.CanonicalValue, out List<PolicyOverride>? candidates);
            components.Add(EvaluateComponent(input, policy, options, candidates));
        }

        List<NoticeDiagnostic> reportDiagnostics = [];
        if (options.ReleaseMode)
        {
            List<PolicyOverride> orderedOverrides = [.. policy.Overrides];
            orderedOverrides.Sort(OverrideComparer.Instance);
            foreach (PolicyOverride policyOverride in orderedOverrides)
            {
                if (!observedPurls.Contains(policyOverride.PackageUrl.CanonicalValue))
                {
                    reportDiagnostics.Add(new NoticeDiagnostic(
                        PolicyDiagnosticCodes.UnusedOverride,
                        ToSeverity(options.UnusedOverride),
                        $"Policy override '{policyOverride.Id}' did not match an inventory component.",
                        policyOverride.PackageUrl.CanonicalValue,
                        Source: policyOverride.Id,
                        Remediation: "Remove the override or evaluate the exact component it targets."));
                }
            }
        }

        reportDiagnostics.Sort(DiagnosticComparer.Instance);
        return new PolicyEvaluationReport(components.AsReadOnly(), reportDiagnostics.AsReadOnly());
    }

    private static ComponentPolicyEvaluation EvaluateComponent(
        PolicyEvaluationInput input,
        PolicyConfiguration policy,
        PolicyEvaluationOptions options,
        List<PolicyOverride>? overrideCandidates)
    {
        List<NoticeDiagnostic> diagnostics = [];
        string? appliedOverrideId = null;
        SpdxExpression observed;
        try
        {
            observed = SpdxParser.Parse(input.ObservedLicenseExpression);
        }
        catch (SpdxParseException exception)
        {
            diagnostics.Add(new NoticeDiagnostic(
                PolicyDiagnosticCodes.InvalidSpdxExpression,
                NoticeDiagnosticSeverity.Error,
                $"Observed SPDX expression is invalid at offset {exception.Offset}; expected {exception.Expected}.",
                input.PackageUrl.CanonicalValue,
                Offset: exception.Offset));
            return new ComponentPolicyEvaluation(
                input.PackageUrl.CanonicalValue,
                input.ObservedLicenseExpression,
                input.ObservedLicenseExpression,
                null,
                null,
                PolicyDecision.Deny,
                Array.Empty<PolicySubjectDecision>(),
                diagnostics.AsReadOnly());
        }

        SpdxExpression effective = observed;
        if (overrideCandidates is { Count: > 1 })
        {
            string ids = JoinOverrideIds(overrideCandidates);
            diagnostics.Add(new NoticeDiagnostic(
                PolicyDiagnosticCodes.ConflictingOverride,
                NoticeDiagnosticSeverity.Error,
                $"Multiple exact overrides match this component: {ids}.",
                input.PackageUrl.CanonicalValue,
                Remediation: "Retain one exact, reviewed override; last-rule-wins behavior is not supported."));
        }
        else if (overrideCandidates is { Count: 1 })
        {
            PolicyOverride candidate = overrideCandidates[0];
            if (CanApplyOverride(candidate, input, options, diagnostics))
            {
                effective = SpdxParser.Parse(candidate.Set.LicenseExpression);
                appliedOverrideId = candidate.Id;
            }
        }

        if (input.LicenseEvidenceSha256.Count == 0)
        {
            diagnostics.Add(new NoticeDiagnostic(
                PolicyDiagnosticCodes.MissingObligationOrEvidence,
                ToSeverity(policy.MissingEvidence),
                "No exact license evidence is linked to this policy subject.",
                input.PackageUrl.CanonicalValue,
                Remediation: "Pin unmodified license evidence by lowercase SHA-256."));
        }

        SelectionResult selection = SelectEffectiveNode(input, effective, policy, diagnostics);
        List<PolicySubjectDecision> subjectDecisions = [];
        PolicyDecision decision = EvaluateNode(
            selection.Node,
            input,
            policy,
            subjectDecisions,
            diagnostics);
        if (ContainsError(diagnostics))
        {
            decision = PolicyDecision.Deny;
        }

        return new ComponentPolicyEvaluation(
            input.PackageUrl.CanonicalValue,
            observed.Canonical,
            PolicySpdxFormatter.Format(selection.Node),
            selection.CanonicalSelection,
            appliedOverrideId,
            decision,
            subjectDecisions.AsReadOnly(),
            diagnostics.AsReadOnly());
    }

    private static bool CanApplyOverride(
        PolicyOverride candidate,
        PolicyEvaluationInput input,
        PolicyEvaluationOptions options,
        List<NoticeDiagnostic> diagnostics)
    {
        if (candidate.CreatedOn > options.EvaluationDate)
        {
            diagnostics.Add(new NoticeDiagnostic(
                PolicyDiagnosticCodes.InvalidOverrideMetadata,
                NoticeDiagnosticSeverity.Error,
                $"Override '{candidate.Id}' has a creation date after the fixed evaluation date.",
                input.PackageUrl.CanonicalValue,
                Source: candidate.Id));
            return false;
        }

        if (DateOnly.TryParseExact(candidate.ExpiresAfter, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly expiry))
        {
            if (options.EvaluationDate > expiry)
            {
                diagnostics.Add(new NoticeDiagnostic(
                    PolicyDiagnosticCodes.ExpiredOverride,
                    ToSeverity(options.ExpiredOverride),
                    $"Override '{candidate.Id}' expired before the fixed evaluation date.",
                    input.PackageUrl.CanonicalValue,
                    Source: candidate.Id,
                    Remediation: "Remove or explicitly re-review the override."));
                return false;
            }
        }
        else if (!StringComparer.Ordinal.Equals(candidate.ExpiresAfter, input.PackageUrl.Version))
        {
            diagnostics.Add(new NoticeDiagnostic(
                PolicyDiagnosticCodes.VersionStaleOverride,
                NoticeDiagnosticSeverity.Error,
                $"Override '{candidate.Id}' is version-stale for the exact component.",
                input.PackageUrl.CanonicalValue,
                Source: candidate.Id,
                Remediation: "Review the new version and create a new exact override."));
            return false;
        }

        if (!input.LicenseEvidenceSha256.Contains(candidate.Set.LicenseEvidenceSha256))
        {
            diagnostics.Add(new NoticeDiagnostic(
                PolicyDiagnosticCodes.OverrideEvidenceMismatch,
                NoticeDiagnosticSeverity.Error,
                $"Override '{candidate.Id}' is not linked to its declared evidence digest.",
                input.PackageUrl.CanonicalValue,
                Source: candidate.Id,
                Remediation: "Acquire and verify the declared evidence bytes before applying the override."));
            return false;
        }

        return true;
    }

    private static SelectionResult SelectEffectiveNode(
        PolicyEvaluationInput input,
        SpdxExpression effective,
        PolicyConfiguration policy,
        List<NoticeDiagnostic> diagnostics)
    {
        SpdxExpressionNode node = effective.Root;
        string? canonicalSelection = null;
        if (!ContainsOr(node) || policy.OrExpressions != OrExpressionPolicy.RequireExplicitSelection)
        {
            return new SelectionResult(node, null);
        }

        if (string.IsNullOrWhiteSpace(input.SelectedLicenseExpression))
        {
            diagnostics.Add(new NoticeDiagnostic(
                PolicyDiagnosticCodes.ExplicitSelectionRequired,
                NoticeDiagnosticSeverity.Error,
                "The effective OR expression requires an explicit selected branch.",
                input.PackageUrl.CanonicalValue,
                Source: effective.Canonical));
            return new SelectionResult(node, null);
        }

        try
        {
            SpdxExpression selected = SpdxParser.Parse(input.SelectedLicenseExpression);
            canonicalSelection = selected.Canonical;
            if (ContainsOr(selected.Root) || !IsSelectionOf(node, selected.Root))
            {
                diagnostics.Add(new NoticeDiagnostic(
                    PolicyDiagnosticCodes.InvalidSelection,
                    NoticeDiagnosticSeverity.Error,
                    "The selected license is not an exact branch of the effective OR expression.",
                    input.PackageUrl.CanonicalValue,
                    Source: effective.Canonical));
                return new SelectionResult(node, canonicalSelection);
            }

            return new SelectionResult(selected.Root, canonicalSelection);
        }
        catch (SpdxParseException exception)
        {
            diagnostics.Add(new NoticeDiagnostic(
                PolicyDiagnosticCodes.InvalidSelection,
                NoticeDiagnosticSeverity.Error,
                $"The selected SPDX expression is invalid at offset {exception.Offset}; expected {exception.Expected}.",
                input.PackageUrl.CanonicalValue,
                Offset: exception.Offset));
            return new SelectionResult(node, null);
        }
    }

    private static PolicyDecision EvaluateNode(
        SpdxExpressionNode node,
        PolicyEvaluationInput input,
        PolicyConfiguration policy,
        List<PolicySubjectDecision> decisions,
        List<NoticeDiagnostic> diagnostics)
    {
        if (node is SpdxAndNode and)
        {
            PolicyDecision left = EvaluateNode(and.Left, input, policy, decisions, diagnostics);
            PolicyDecision right = EvaluateNode(and.Right, input, policy, decisions, diagnostics);
            return Worst(left, right);
        }

        if (node is SpdxOrNode or)
        {
            List<PolicySubjectDecision> leftDecisions = [];
            List<NoticeDiagnostic> leftDiagnostics = [];
            PolicyDecision left = EvaluateNode(or.Left, input, policy, leftDecisions, leftDiagnostics);
            List<PolicySubjectDecision> rightDecisions = [];
            List<NoticeDiagnostic> rightDiagnostics = [];
            PolicyDecision right = EvaluateNode(or.Right, input, policy, rightDecisions, rightDiagnostics);
            bool useLeft = left < right || (left == right && StringComparer.Ordinal.Compare(
                PolicySpdxFormatter.Format(or.Left),
                PolicySpdxFormatter.Format(or.Right)) <= 0);
            decisions.AddRange(useLeft ? leftDecisions : rightDecisions);
            diagnostics.AddRange(useLeft ? leftDiagnostics : rightDiagnostics);
            return useLeft ? left : right;
        }

        string subject = PolicySpdxFormatter.Format(node);
        if (node is SpdxLicenseIdentifierNode identifier &&
            identifier.Identifier.Contains("LicenseRef-", StringComparison.Ordinal) &&
            input.LicenseEvidenceSha256.Count == 0)
        {
            diagnostics.Add(new NoticeDiagnostic(
                PolicyDiagnosticCodes.UnresolvedLicenseReference,
                NoticeDiagnosticSeverity.Error,
                $"Custom license identifier '{identifier.Identifier}' has no exact evidence link.",
                input.PackageUrl.CanonicalValue));
        }

        RuleMatch match = FindDecision(subject, policy);
        IReadOnlyList<string> obligations = FindObligations(subject, policy.Licenses.Obligations);
        PolicyDecision decision = match.Decision;
        if (decision == PolicyDecision.Deny)
        {
            diagnostics.Add(new NoticeDiagnostic(
                PolicyDiagnosticCodes.LicenseDenied,
                NoticeDiagnosticSeverity.Error,
                $"Organizational policy denies subject '{subject}'.",
                input.PackageUrl.CanonicalValue,
                Source: match.Rule));
        }
        else if (decision == PolicyDecision.Review)
        {
            diagnostics.Add(new NoticeDiagnostic(
                PolicyDiagnosticCodes.LicenseReviewRequired,
                NoticeDiagnosticSeverity.Warning,
                $"Organizational policy requires review of subject '{subject}'.",
                input.PackageUrl.CanonicalValue,
                Source: match.Rule));
        }

        foreach (string obligation in obligations)
        {
            if (!input.FulfilledObligations.Contains(obligation))
            {
                diagnostics.Add(new NoticeDiagnostic(
                    PolicyDiagnosticCodes.MissingObligationOrEvidence,
                    NoticeDiagnosticSeverity.Error,
                    $"Required obligation '{obligation}' is not recorded for subject '{subject}'.",
                    input.PackageUrl.CanonicalValue,
                    Source: match.Rule));
                decision = PolicyDecision.Deny;
            }
        }

        decisions.Add(new PolicySubjectDecision(subject, decision, match.Rule, obligations));
        return decision;
    }

    private static RuleMatch FindDecision(string subject, PolicyConfiguration policy)
    {
        string? deny = BestMatch(policy.Licenses.Deny, subject);
        if (deny is not null)
        {
            return new RuleMatch(PolicyDecision.Deny, deny);
        }

        string? allow = BestMatch(policy.Licenses.Allow, subject);
        if (allow is not null)
        {
            return new RuleMatch(PolicyDecision.Allow, allow);
        }

        string? review = BestMatch(policy.Licenses.Review, subject);
        return review is null
            ? new RuleMatch(policy.DefaultDecision, "$default")
            : new RuleMatch(PolicyDecision.Review, review);
    }

    private static string? BestMatch(IReadOnlyList<string> rules, string subject)
    {
        string? result = null;
        foreach (string rule in rules)
        {
            if (!Matches(rule, subject))
            {
                continue;
            }

            if (result is null || IsBetterRule(rule, result))
            {
                result = rule;
            }
        }

        return result;
    }

    private static ReadOnlyCollection<string> FindObligations(
        string subject,
        IReadOnlyDictionary<string, IReadOnlyList<string>> configured)
    {
        SortedSet<string> result = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, IReadOnlyList<string>> entry in configured)
        {
            if (Matches(entry.Key, subject))
            {
                foreach (string obligation in entry.Value)
                {
                    result.Add(obligation);
                }
            }
        }

        return new ReadOnlyCollection<string>([.. result]);
    }

    private static bool Matches(string rule, string subject) => rule.EndsWith('*')
        ? subject.StartsWith(rule.AsSpan(0, rule.Length - 1), StringComparison.Ordinal)
        : StringComparer.Ordinal.Equals(rule, subject);

    private static bool IsBetterRule(string candidate, string current)
    {
        bool candidateExact = !candidate.EndsWith('*');
        bool currentExact = !current.EndsWith('*');
        if (candidateExact != currentExact)
        {
            return candidateExact;
        }

        return candidate.Length > current.Length ||
            (candidate.Length == current.Length && StringComparer.Ordinal.Compare(candidate, current) < 0);
    }

    private static Dictionary<string, List<PolicyOverride>> IndexOverrides(IReadOnlyList<PolicyOverride> overrides)
    {
        Dictionary<string, List<PolicyOverride>> result = new(StringComparer.Ordinal);
        foreach (PolicyOverride policyOverride in overrides)
        {
            string key = policyOverride.PackageUrl.CanonicalValue;
            if (!result.TryGetValue(key, out List<PolicyOverride>? values))
            {
                values = [];
                result.Add(key, values);
            }

            values.Add(policyOverride);
        }

        foreach (List<PolicyOverride> values in result.Values)
        {
            values.Sort(OverrideComparer.Instance);
        }

        return result;
    }

    private static string JoinOverrideIds(List<PolicyOverride> candidates)
    {
        string[] ids = new string[candidates.Count];
        for (int index = 0; index < candidates.Count; index++)
        {
            ids[index] = candidates[index].Id;
        }

        return string.Join(", ", ids);
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

    private static bool ContainsError(List<NoticeDiagnostic> diagnostics)
    {
        foreach (NoticeDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Severity == NoticeDiagnosticSeverity.Error)
            {
                return true;
            }
        }

        return false;
    }

    private static NoticeDiagnosticSeverity ToSeverity(PolicyDiagnosticLevel level) => level switch
    {
        PolicyDiagnosticLevel.Warning => NoticeDiagnosticSeverity.Warning,
        PolicyDiagnosticLevel.Error => NoticeDiagnosticSeverity.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    private static PolicyDecision Worst(PolicyDecision left, PolicyDecision right) =>
        (PolicyDecision)Math.Max((int)left, (int)right);

    private sealed record RuleMatch(PolicyDecision Decision, string Rule);

    private sealed record SelectionResult(SpdxExpressionNode Node, string? CanonicalSelection);

    private sealed class OverrideComparer : IComparer<PolicyOverride>
    {
        public static OverrideComparer Instance { get; } = new();

        public int Compare(PolicyOverride? x, PolicyOverride? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            int byPurl = StringComparer.Ordinal.Compare(x.PackageUrl.CanonicalValue, y.PackageUrl.CanonicalValue);
            return byPurl != 0 ? byPurl : StringComparer.Ordinal.Compare(x.Id, y.Id);
        }
    }

    private sealed class DiagnosticComparer : IComparer<NoticeDiagnostic>
    {
        public static DiagnosticComparer Instance { get; } = new();

        public int Compare(NoticeDiagnostic? x, NoticeDiagnostic? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            int byPurl = StringComparer.Ordinal.Compare(x.PackageUrl, y.PackageUrl);
            if (byPurl != 0)
            {
                return byPurl;
            }

            int byCode = StringComparer.Ordinal.Compare(x.Code, y.Code);
            return byCode != 0 ? byCode : StringComparer.Ordinal.Compare(x.Source, y.Source);
        }
    }
}
