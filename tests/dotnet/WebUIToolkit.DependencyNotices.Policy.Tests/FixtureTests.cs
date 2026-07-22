using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Policy.Tests;

internal static class FixtureTests
{
    public static void Register(TestHarness tests) => tests.Add("policy fixture manifest matches parser and evaluator", VerifyManifest);

    private static void VerifyManifest()
    {
        string fixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", "policy");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "fixture-manifest.json")));
        DateOnly date = DateOnly.ParseExact(manifest.RootElement.GetProperty("evaluationDate").GetString()!, "yyyy-MM-dd");
        foreach (JsonElement fixture in manifest.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            string file = fixture.GetProperty("file").GetString()!;
            bool parseValid = fixture.GetProperty("parseValid").GetBoolean();
            JsonElement expectedElement = fixture.GetProperty("expectedDiagnostic");
            string? expectedCode = expectedElement.ValueKind == JsonValueKind.Null ? null : expectedElement.GetString();
            string json = File.ReadAllText(Path.Combine(fixtureRoot, file.Replace('/', Path.DirectorySeparatorChar)));
            if (!parseValid)
            {
                PolicyConfigurationException error = Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(json));
                Assert.Equal(expectedCode, error.Code);
                continue;
            }

            PolicyConfiguration policy = PolicyConfigurationParser.Parse(json);
            PolicyEvaluationReport report = PolicyEvaluator.Evaluate(
                [new PolicyEvaluationInput(
                    PackageUrl.Parse("pkg:generic/widget@1.0.0"),
                    "LicenseRef-Custom",
                    null,
                    new HashSet<string>(["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"], StringComparer.Ordinal),
                    new HashSet<string>(StringComparer.Ordinal))],
                policy,
                new PolicyEvaluationOptions(date));
            if (expectedCode is not null)
            {
                Assert.True(HasCode(report, expectedCode), $"Fixture '{file}' did not emit {expectedCode}.");
            }
        }
    }

    private static bool HasCode(PolicyEvaluationReport report, string code)
    {
        foreach (NoticeDiagnostic diagnostic in report.Diagnostics)
        {
            if (StringComparer.Ordinal.Equals(code, diagnostic.Code))
            {
                return true;
            }
        }

        foreach (ComponentPolicyEvaluation component in report.Components)
        {
            foreach (NoticeDiagnostic diagnostic in component.Diagnostics)
            {
                if (StringComparer.Ordinal.Equals(code, diagnostic.Code))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
