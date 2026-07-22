using System;
using System.Collections.Generic;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Policy;

namespace WebUIToolkit.DependencyNotices.Tests;

internal static class PolicyTests
{
    public static void Register(TestHarness tests)
    {
        tests.Add("policy preserves WITH as one subject", WithIsOneSubject);
        tests.Add("policy evaluates every AND branch", AndDenialWins);
        tests.Add("policy requires exact OR selection", OrRequiresSelection);
        tests.Add("policy resolves nested OR selections", NestedOrSelection);
        tests.Add("policy permitted OR keeps only the accepted branch decision", PermittedOrUsesBestBranch);
        tests.Add("policy maps review and obligations to WUTNOTICE", ReviewAndObligationDiagnostics);
    }

    private static void WithIsOneSubject()
    {
        LicensePolicy policy = LicensePolicy.Create(allowed: ["Apache-2.0 WITH LLVM-exception"]);
        LicensePolicyEvaluation result = LicensePolicyEvaluator.Evaluate(Purl(), "Apache-2.0 WITH LLVM-exception", null, true, policy);
        Assert.Equal(LicensePolicyOutcome.Allow, result.Outcome);
        Assert.Equal("Apache-2.0 WITH LLVM-exception", result.EffectiveExpression);
    }

    private static void AndDenialWins()
    {
        LicensePolicy policy = LicensePolicy.Create(allowed: ["MIT"], denied: ["LicenseRef-Prohibited"]);
        LicensePolicyEvaluation result = LicensePolicyEvaluator.Evaluate(Purl(), "MIT AND LicenseRef-Prohibited", null, true, policy);
        Assert.Equal(LicensePolicyOutcome.Deny, result.Outcome);
        Assert.True(Has(result, NoticeDiagnosticCodes.LicenseDenied));
    }

    private static void OrRequiresSelection()
    {
        LicensePolicy policy = LicensePolicy.Create(allowed: ["MIT", "Apache-2.0"]);
        LicensePolicyEvaluation missing = LicensePolicyEvaluator.Evaluate(Purl(), "MIT OR Apache-2.0", null, true, policy);
        Assert.Equal(LicensePolicyOutcome.Deny, missing.Outcome);
        Assert.True(Has(missing, NoticeDiagnosticCodes.ExplicitLicenseSelectionRequired));

        LicensePolicyEvaluation invalid = LicensePolicyEvaluator.Evaluate(Purl(), "MIT OR Apache-2.0", "BSD-3-Clause", true, policy);
        Assert.True(Has(invalid, NoticeDiagnosticCodes.InvalidLicenseSelection));

        LicensePolicyEvaluation selected = LicensePolicyEvaluator.Evaluate(Purl(), "MIT OR Apache-2.0", "MIT", true, policy);
        Assert.Equal(LicensePolicyOutcome.Allow, selected.Outcome);
        Assert.Equal("MIT", selected.SelectedExpression);
    }

    private static void ReviewAndObligationDiagnostics()
    {
        Dictionary<string, IReadOnlyList<string>> obligations = new(StringComparer.Ordinal)
        {
            ["Apache-2.0"] = ["preserve-notice"],
        };
        LicensePolicy policy = LicensePolicy.Create(review: ["LicenseRef-*"], allowed: ["Apache-2.0"], obligations: obligations);
        LicensePolicyEvaluation review = LicensePolicyEvaluator.Evaluate(Purl(), "LicenseRef-Custom", null, true, policy);
        Assert.Equal(LicensePolicyOutcome.Review, review.Outcome);
        Assert.True(Has(review, NoticeDiagnosticCodes.LicenseReviewRequired));

        LicensePolicyEvaluation obligation = LicensePolicyEvaluator.Evaluate(Purl(), "Apache-2.0", null, true, policy);
        Assert.Equal(LicensePolicyOutcome.Deny, obligation.Outcome);
        Assert.True(Has(obligation, NoticeDiagnosticCodes.MissingLicenseObligation));
    }

    private static void NestedOrSelection()
    {
        LicensePolicy policy = LicensePolicy.Create(allowed: ["MIT", "Apache-2.0"]);
        LicensePolicyEvaluation result = LicensePolicyEvaluator.Evaluate(
            Purl(),
            "MIT AND (Apache-2.0 OR BSD-3-Clause)",
            "MIT AND Apache-2.0",
            true,
            policy);
        Assert.Equal(LicensePolicyOutcome.Allow, result.Outcome);
        Assert.Equal("MIT AND Apache-2.0", result.EffectiveExpression);
    }

    private static void PermittedOrUsesBestBranch()
    {
        LicensePolicy policy = LicensePolicy.Create(
            allowed: ["MIT"],
            denied: ["LicenseRef-Prohibited"],
            requireExplicitOrSelection: false);
        LicensePolicyEvaluation result = LicensePolicyEvaluator.Evaluate(Purl(), "LicenseRef-Prohibited OR MIT", null, true, policy);
        Assert.Equal(LicensePolicyOutcome.Allow, result.Outcome);
        Assert.True(!Has(result, NoticeDiagnosticCodes.LicenseDenied));
    }

    private static bool Has(LicensePolicyEvaluation result, string code)
    {
        foreach (NoticeDiagnostic diagnostic in result.Diagnostics)
        {
            if (diagnostic.Code == code)
            {
                return true;
            }
        }

        return false;
    }

    private static PackageUrl Purl() => PackageUrl.Parse("pkg:generic/widget@1.0.0");
}
