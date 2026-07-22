using System;
using System.Collections.Generic;
using System.Text;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Policy.Tests;

internal static class EvaluatorTests
{
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static void Register(TestHarness tests)
    {
        tests.Add("evaluator applies exact evidenced override and preserves observed value", ApplyOverride);
        tests.Add("evaluator rejects override without declared evidence", RejectUnevidencedOverride);
        tests.Add("evaluator expires overrides using fixed input date", ExpireOverride);
        tests.Add("evaluator rejects future-dated override metadata", RejectFutureOverride);
        tests.Add("evaluator rejects version-stale override", RejectVersionStaleOverride);
        tests.Add("evaluator rejects multiply matching overrides", RejectConflictingOverrides);
        tests.Add("evaluator reports unused override in release mode", ReportUnusedOverride);
        tests.Add("evaluator omits unused override diagnostic outside release mode", PermitUnusedOverrideOutsideRelease);
        tests.Add("evaluator matches deterministic terminal wildcard", MatchWildcard);
        tests.Add("evaluator gives deny rules precedence", DenyPrecedence);
        tests.Add("evaluator enforces wildcard obligations", EnforceObligations);
        tests.Add("evaluator requires explicit OR selection", RequireOrSelection);
        tests.Add("evaluator validates exact OR selection", ValidateOrSelection);
        tests.Add("evaluator accepts nested exact OR selection", AcceptNestedSelection);
        tests.Add("evaluator evaluates every AND branch", EvaluateAndBranches);
        tests.Add("evaluator deterministically chooses permitted OR branch", ChoosePermittedOr);
        tests.Add("evaluator keeps WITH as one policy subject", PreserveWith);
        tests.Add("evaluator applies configured missing evidence warning", MissingEvidenceWarning);
        tests.Add("evaluator reports LicenseRef without evidence", ReportLicenseRefEvidence);
        tests.Add("evaluator sorts components and report diagnostics", DeterministicOrdering);
        tests.Add("evaluator expiry severity does not revive an override", WarningExpiryDoesNotApply);
        tests.Add("evaluator outcome changes only with supplied date", ExplicitDateControlsExpiry);
        tests.Add("evaluator emits no legal conclusions", AvoidLegalConclusions);
    }

    private static void ApplyOverride()
    {
        PolicyConfiguration policy = ParseWithOverride(ParserTests.Override());
        ComponentPolicyEvaluation result = One(policy, Input("GPL-3.0-only"));
        Assert.Equal("GPL-3.0-only", result.ObservedExpression);
        Assert.Equal("MIT", result.EffectiveExpression);
        Assert.Equal("approved-metadata-correction", result.AppliedOverrideId);
        Assert.Equal(PolicyDecision.Allow, result.Decision);
    }

    private static void RejectUnevidencedOverride()
    {
        PolicyConfiguration policy = ParseWithOverride(ParserTests.Override());
        ComponentPolicyEvaluation result = One(policy, Input("MIT", evidence: Set()));
        Assert.True(HasCode(result, PolicyDiagnosticCodes.OverrideEvidenceMismatch));
        Assert.Equal("MIT", result.ObservedExpression);
        Assert.Equal(null, result.AppliedOverrideId);
        Assert.Equal(PolicyDecision.Deny, result.Decision);
    }

    private static void ExpireOverride()
    {
        PolicyConfiguration policy = ParseWithOverride(ParserTests.Override(expiresAfter: "2026-01-31"));
        ComponentPolicyEvaluation result = One(policy, Input("LicenseRef-Custom"), new DateOnly(2026, 2, 1));
        Assert.True(HasCode(result, PolicyDiagnosticCodes.ExpiredOverride));
        Assert.Equal(null, result.AppliedOverrideId);
    }

    private static void RejectFutureOverride()
    {
        PolicyConfiguration policy = ParseWithOverride(ParserTests.Override(createdOn: "2027-01-01", expiresAfter: "2028-01-01"));
        ComponentPolicyEvaluation result = One(policy, Input("MIT"));
        Assert.True(HasCode(result, PolicyDiagnosticCodes.InvalidOverrideMetadata));
    }

    private static void RejectVersionStaleOverride()
    {
        PolicyConfiguration policy = ParseWithOverride(ParserTests.Override(expiresAfter: "2.0.0"));
        ComponentPolicyEvaluation result = One(policy, Input("MIT"));
        Assert.True(HasCode(result, PolicyDiagnosticCodes.VersionStaleOverride));
        Assert.Equal(null, result.AppliedOverrideId);
    }

    private static void RejectConflictingOverrides()
    {
        string overrides = "[" + ParserTests.Override(id: "a") + "," + ParserTests.Override(id: "b", expression: "Apache-2.0") + "]";
        PolicyConfiguration policy = PolicyConfigurationParser.Parse(ParserTests.Valid(overrides));
        ComponentPolicyEvaluation result = One(policy, Input("MIT"));
        Assert.True(HasCode(result, PolicyDiagnosticCodes.ConflictingOverride));
        Assert.Equal(null, result.AppliedOverrideId);
    }

    private static void ReportUnusedOverride()
    {
        PolicyConfiguration policy = ParseWithOverride(ParserTests.Override());
        PolicyEvaluationReport report = PolicyEvaluator.Evaluate(
            [Input("MIT", purl: "pkg:generic/other@1.0.0")],
            policy,
            Options());
        Assert.True(HasCode(report.Diagnostics, PolicyDiagnosticCodes.UnusedOverride));
    }

    private static void PermitUnusedOverrideOutsideRelease()
    {
        PolicyConfiguration policy = ParseWithOverride(ParserTests.Override());
        PolicyEvaluationReport report = PolicyEvaluator.Evaluate(
            [Input("MIT", purl: "pkg:generic/other@1.0.0")],
            policy,
            Options() with { ReleaseMode = false });
        Assert.True(!HasCode(report.Diagnostics, PolicyDiagnosticCodes.UnusedOverride));
    }

    private static void MatchWildcard()
    {
        ComponentPolicyEvaluation result = One(Parse(), Input("LicenseRef-NeedsReview"));
        Assert.Equal(PolicyDecision.Review, result.Decision);
        Assert.Equal("LicenseRef-*", result.SubjectDecisions[0].MatchedRule);
        Assert.True(HasCode(result, PolicyDiagnosticCodes.LicenseReviewRequired));
    }

    private static void DenyPrecedence()
    {
        string json = ParserTests.Valid().Replace(
            "\"deny\": [\"LicenseRef-Prohibited\"]",
            "\"deny\": [\"LicenseRef-*\"]",
            StringComparison.Ordinal);
        ComponentPolicyEvaluation result = One(PolicyConfigurationParser.Parse(json), Input("LicenseRef-Prohibited"));
        Assert.Equal(PolicyDecision.Deny, result.Decision);
        Assert.True(HasCode(result, PolicyDiagnosticCodes.LicenseDenied));
    }

    private static void EnforceObligations()
    {
        ComponentPolicyEvaluation missing = One(Parse(), Input("Apache-2.0 WITH LLVM-exception"));
        Assert.Equal(PolicyDecision.Deny, missing.Decision);
        Assert.True(HasCode(missing, PolicyDiagnosticCodes.MissingObligationOrEvidence));

        ComponentPolicyEvaluation fulfilled = One(Parse(), Input(
            "Apache-2.0 WITH LLVM-exception",
            obligations: Set("license-text", "preserve-notice")));
        Assert.Equal(PolicyDecision.Allow, fulfilled.Decision);
    }

    private static void RequireOrSelection()
    {
        ComponentPolicyEvaluation result = One(Parse(), Input("MIT OR Apache-2.0"));
        Assert.True(HasCode(result, PolicyDiagnosticCodes.ExplicitSelectionRequired));
        Assert.Equal(PolicyDecision.Deny, result.Decision);
    }

    private static void ValidateOrSelection()
    {
        ComponentPolicyEvaluation result = One(Parse(), Input("MIT OR Apache-2.0", selected: "BSD-3-Clause"));
        Assert.True(HasCode(result, PolicyDiagnosticCodes.InvalidSelection));
        Assert.Equal(PolicyDecision.Deny, result.Decision);
    }

    private static void AcceptNestedSelection()
    {
        ComponentPolicyEvaluation result = One(Parse(), Input(
            "MIT AND (Apache-2.0 OR LicenseRef-Prohibited)",
            selected: "MIT AND Apache-2.0",
            obligations: Set("license-text", "preserve-notice")));
        Assert.Equal("MIT AND Apache-2.0", result.EffectiveExpression);
        Assert.Equal("MIT AND Apache-2.0", result.SelectedExpression);
        Assert.Equal(PolicyDecision.Allow, result.Decision);
    }

    private static void EvaluateAndBranches()
    {
        ComponentPolicyEvaluation result = One(Parse(), Input("MIT AND LicenseRef-Prohibited"));
        Assert.Equal(2, result.SubjectDecisions.Count);
        Assert.Equal(PolicyDecision.Deny, result.Decision);
    }

    private static void ChoosePermittedOr()
    {
        string json = ParserTests.Valid().Replace("require-explicit-selection", "allow", StringComparison.Ordinal);
        ComponentPolicyEvaluation result = One(PolicyConfigurationParser.Parse(json), Input("LicenseRef-Prohibited OR MIT"));
        Assert.Equal(PolicyDecision.Allow, result.Decision);
        Assert.Equal("MIT", result.SubjectDecisions[0].Subject);
        Assert.True(!HasCode(result, PolicyDiagnosticCodes.LicenseDenied));
    }

    private static void PreserveWith()
    {
        ComponentPolicyEvaluation result = One(Parse(), Input(
            "Apache-2.0 WITH LLVM-exception",
            obligations: Set("license-text", "preserve-notice")));
        Assert.Equal("Apache-2.0 WITH LLVM-exception", result.SubjectDecisions[0].Subject);
        Assert.Equal(PolicyDecision.Allow, result.Decision);
    }

    private static void MissingEvidenceWarning()
    {
        string json = ParserTests.Valid().Replace("\"missingEvidence\": \"error\"", "\"missingEvidence\": \"warning\"", StringComparison.Ordinal);
        ComponentPolicyEvaluation result = One(PolicyConfigurationParser.Parse(json), Input("MIT", evidence: Set()));
        Assert.Equal(PolicyDecision.Allow, result.Decision);
        Assert.Equal(NoticeDiagnosticSeverity.Warning, Find(result, PolicyDiagnosticCodes.MissingObligationOrEvidence).Severity);
    }

    private static void ReportLicenseRefEvidence()
    {
        string json = ParserTests.Valid().Replace("\"missingEvidence\": \"error\"", "\"missingEvidence\": \"warning\"", StringComparison.Ordinal);
        ComponentPolicyEvaluation result = One(PolicyConfigurationParser.Parse(json), Input("LicenseRef-Custom", evidence: Set()));
        Assert.True(HasCode(result, PolicyDiagnosticCodes.UnresolvedLicenseReference));
        Assert.Equal(PolicyDecision.Deny, result.Decision);
    }

    private static void DeterministicOrdering()
    {
        string overrides = "[" + ParserTests.Override(id: "z", purl: "pkg:generic/z@1.0.0") + "," +
            ParserTests.Override(id: "a", purl: "pkg:generic/a@1.0.0") + "]";
        PolicyConfiguration policy = PolicyConfigurationParser.Parse(ParserTests.Valid(overrides));
        PolicyEvaluationReport report = PolicyEvaluator.Evaluate(
            [Input("MIT", purl: "pkg:generic/c@1.0.0"), Input("MIT", purl: "pkg:generic/b@1.0.0")],
            policy,
            Options());
        Assert.Equal("pkg:generic/b@1.0.0", report.Components[0].PackageUrl);
        Assert.Equal("pkg:generic/c@1.0.0", report.Components[1].PackageUrl);
        Assert.Equal("pkg:generic/a@1.0.0", report.Diagnostics[0].PackageUrl);
        Assert.Equal("pkg:generic/z@1.0.0", report.Diagnostics[1].PackageUrl);
    }

    private static void WarningExpiryDoesNotApply()
    {
        PolicyConfiguration policy = ParseWithOverride(ParserTests.Override(expression: "LicenseRef-Prohibited", expiresAfter: "2026-01-01"));
        ComponentPolicyEvaluation result = One(
            policy,
            Input("MIT"),
            new DateOnly(2026, 2, 1),
            Options() with { ExpiredOverride = PolicyDiagnosticLevel.Warning });
        Assert.Equal(null, result.AppliedOverrideId);
        Assert.Equal("MIT", result.EffectiveExpression);
        Assert.Equal(PolicyDecision.Allow, result.Decision);
        Assert.Equal(NoticeDiagnosticSeverity.Warning, Find(result, PolicyDiagnosticCodes.ExpiredOverride).Severity);
    }

    private static void ExplicitDateControlsExpiry()
    {
        PolicyConfiguration policy = ParseWithOverride(ParserTests.Override(expiresAfter: "2026-06-01"));
        ComponentPolicyEvaluation before = One(policy, Input("LicenseRef-Custom"), new DateOnly(2026, 5, 31));
        ComponentPolicyEvaluation after = One(policy, Input("LicenseRef-Custom"), new DateOnly(2026, 6, 2));
        Assert.Equal("approved-metadata-correction", before.AppliedOverrideId);
        Assert.Equal(null, after.AppliedOverrideId);
        Assert.True(HasCode(after, PolicyDiagnosticCodes.ExpiredOverride));
    }

    private static void AvoidLegalConclusions()
    {
        ComponentPolicyEvaluation result = One(Parse(), Input("LicenseRef-Prohibited"));
        StringBuilder messages = new();
        foreach (NoticeDiagnostic diagnostic in result.Diagnostics)
        {
            messages.Append(diagnostic.Message);
        }

        string text = messages.ToString();
        Assert.True(!text.Contains("legal", StringComparison.OrdinalIgnoreCase));
        Assert.True(!text.Contains("compatible", StringComparison.OrdinalIgnoreCase));
        Assert.True(!text.Contains("permitted by law", StringComparison.OrdinalIgnoreCase));
    }

    private static PolicyConfiguration Parse() => PolicyConfigurationParser.Parse(ParserTests.Valid());

    private static PolicyConfiguration ParseWithOverride(string value) =>
        PolicyConfigurationParser.Parse(ParserTests.Valid("[" + value + "]"));

    private static PolicyEvaluationOptions Options() => new(new DateOnly(2026, 2, 1));

    private static ComponentPolicyEvaluation One(
        PolicyConfiguration policy,
        PolicyEvaluationInput input,
        DateOnly? date = null,
        PolicyEvaluationOptions? options = null)
    {
        PolicyEvaluationReport report = PolicyEvaluator.Evaluate(
            [input],
            policy,
            options ?? new PolicyEvaluationOptions(date ?? new DateOnly(2026, 2, 1)));
        return report.Components[0];
    }

    private static PolicyEvaluationInput Input(
        string expression,
        string purl = "pkg:generic/widget@1.0.0",
        string? selected = null,
        IReadOnlySet<string>? evidence = null,
        IReadOnlySet<string>? obligations = null) =>
        new(
            PackageUrl.Parse(purl),
            expression,
            selected,
            evidence ?? new HashSet<string>([Digest], StringComparer.Ordinal),
            obligations ?? new HashSet<string>(StringComparer.Ordinal));

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    private static bool HasCode(ComponentPolicyEvaluation evaluation, string code) => HasCode(evaluation.Diagnostics, code);

    private static bool HasCode(IReadOnlyList<NoticeDiagnostic> diagnostics, string code)
    {
        foreach (NoticeDiagnostic diagnostic in diagnostics)
        {
            if (StringComparer.Ordinal.Equals(diagnostic.Code, code))
            {
                return true;
            }
        }

        return false;
    }

    private static NoticeDiagnostic Find(ComponentPolicyEvaluation evaluation, string code)
    {
        foreach (NoticeDiagnostic diagnostic in evaluation.Diagnostics)
        {
            if (StringComparer.Ordinal.Equals(diagnostic.Code, code))
            {
                return diagnostic;
            }
        }

        throw new InvalidOperationException($"Diagnostic {code} was not found.");
    }
}
