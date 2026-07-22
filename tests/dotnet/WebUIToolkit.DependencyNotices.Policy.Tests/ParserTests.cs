using System;
using System.Text;

namespace WebUIToolkit.DependencyNotices.Policy.Tests;

internal static class ParserTests
{
    public static void Register(TestHarness tests)
    {
        tests.Add("parser accepts strict schema v1", ParseValid);
        tests.Add("parser rejects duplicate object properties", RejectDuplicateProperty);
        tests.Add("parser rejects unknown properties", RejectUnknownProperty);
        tests.Add("parser rejects missing required properties", RejectMissingProperty);
        tests.Add("parser reports unsupported schema version", RejectSchemaVersion);
        tests.Add("parser enforces UTF-8 size limit", EnforceSize);
        tests.Add("parser enforces nesting depth limit", EnforceDepth);
        tests.Add("parser enforces JSON value count limit", EnforceValues);
        tests.Add("parser requires canonical exact override PURL", RequireCanonicalPurl);
        tests.Add("parser validates terminal wildcard rules", ValidateWildcard);
        tests.Add("parser validates SPDX rule syntax", ValidateSpdx);
        tests.Add("parser validates override evidence digest", ValidateDigest);
        tests.Add("parser validates override creation date", ValidateDate);
        tests.Add("parser rejects duplicate rule values", RejectDuplicateRule);
        tests.Add("parser rejects malformed UTF-8", RejectMalformedUtf8);
        tests.Add("parser maps lone high surrogate value to policy diagnostic", RejectHighSurrogateValue);
        tests.Add("parser maps lone low surrogate value to policy diagnostic", RejectLowSurrogateValue);
        tests.Add("parser maps lone high surrogate property name to policy diagnostic", RejectHighSurrogatePropertyName);
        tests.Add("parser maps lone low surrogate property name to policy diagnostic", RejectLowSurrogatePropertyName);
        tests.Add("parser maps nested lone surrogate override value to policy diagnostic", RejectNestedSurrogateValue);
        tests.Add("parser accepts valid escaped surrogate pair in metadata", AcceptSurrogatePair);
        tests.Add("UTF-8 parser maps escaped lone surrogate to policy diagnostic", RejectEscapedSurrogateUtf8Input);
    }

    internal static string Valid(string overrides = "[]") => $$"""
        {
          "schemaVersion": 1,
          "defaultDecision": "review",
          "licenses": {
            "allow": ["MIT", "Apache-2.0", "Apache-2.0 WITH LLVM-exception"],
            "deny": ["LicenseRef-Prohibited"],
            "review": ["LicenseRef-*"],
            "obligations": { "Apache-2.0*": ["license-text", "preserve-notice"] }
          },
          "missingEvidence": "error",
          "orExpressions": "require-explicit-selection",
          "overrides": {{overrides}}
        }
        """;

    internal static string Override(
        string id = "approved-metadata-correction",
        string purl = "pkg:generic/widget@1.0.0",
        string expression = "MIT",
        string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string createdOn = "2026-01-01",
        string expiresAfter = "2026-12-31") => $$"""
        {
          "id": "{{id}}",
          "purl": "{{purl}}",
          "set": {
            "licenseExpression": "{{expression}}",
            "licenseEvidenceSha256": "{{digest}}"
          },
          "reason": "Reviewed against the tagged source evidence.",
          "approvedBy": "release-reviewer",
          "createdOn": "{{createdOn}}",
          "expiresAfter": "{{expiresAfter}}"
        }
        """;

    private static void ParseValid()
    {
        PolicyConfiguration policy = PolicyConfigurationParser.Parse(Valid("[" + Override() + "]"));
        Assert.Equal(1, policy.SchemaVersion);
        Assert.Equal(PolicyDecision.Review, policy.DefaultDecision);
        Assert.Equal(1, policy.Overrides.Count);
        Assert.Equal("pkg:generic/widget@1.0.0", policy.Overrides[0].PackageUrl.CanonicalValue);
    }

    private static void RejectDuplicateProperty()
    {
        string json = Valid().Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"schemaVersion\": 1,", StringComparison.Ordinal);
        PolicyConfigurationException error = Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(json));
        Assert.Equal(PolicyDiagnosticCodes.InvalidConfiguration, error.Code);
        Assert.Equal("/schemaVersion", error.JsonPointer);
    }

    private static void RejectUnknownProperty()
    {
        string json = Valid().Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"future\": true,", StringComparison.Ordinal);
        PolicyConfigurationException error = Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(json));
        Assert.Equal("/future", error.JsonPointer);
    }

    private static void RejectMissingProperty()
    {
        string json = Valid().Replace("\"missingEvidence\": \"error\",", "", StringComparison.Ordinal);
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(json));
    }

    private static void RejectSchemaVersion()
    {
        string json = Valid().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);
        PolicyConfigurationException error = Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(json));
        Assert.Equal(PolicyDiagnosticCodes.UnsupportedSchemaVersion, error.Code);
    }

    private static void EnforceSize()
    {
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(Valid(), new PolicyParserLimits(32, 32, 100)));
    }

    private static void EnforceDepth()
    {
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(Valid(), new PolicyParserLimits(10_000, 2, 100)));
    }

    private static void EnforceValues()
    {
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(Valid(), new PolicyParserLimits(10_000, 32, 5)));
    }

    private static void RequireCanonicalPurl()
    {
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(Valid("[" + Override(purl: "pkg:GENERIC/widget@1.0.0") + "]")));
    }

    private static void ValidateWildcard()
    {
        string json = Valid().Replace("LicenseRef-*", "License*Ref", StringComparison.Ordinal);
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(json));
    }

    private static void ValidateSpdx()
    {
        string json = Valid().Replace("\"MIT\"", "\"MIT OR\"", StringComparison.Ordinal);
        PolicyConfigurationException error = Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(json));
        Assert.Equal(PolicyDiagnosticCodes.InvalidSpdxExpression, error.Code);
    }

    private static void ValidateDigest()
    {
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(Valid("[" + Override(digest: "ABC") + "]")));
    }

    private static void ValidateDate()
    {
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(Valid("[" + Override(createdOn: "01/02/2026") + "]")));
    }

    private static void RejectDuplicateRule()
    {
        string json = Valid().Replace("\"MIT\", \"Apache", "\"MIT\", \"MIT\", \"Apache", StringComparison.Ordinal);
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(json));
    }

    private static void RejectMalformedUtf8()
    {
        byte[] bytes = [0x7B, 0x22, 0x78, 0x22, 0x3A, 0x22, 0xFF, 0x22, 0x7D];
        Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(bytes));
    }

    private static void RejectHighSurrogateValue()
    {
        string json = Valid().Replace("\"defaultDecision\": \"review\"", "\"defaultDecision\": \"\\uD800\"", StringComparison.Ordinal);
        AssertInvalidEscapedUnicode(json);
    }

    private static void RejectLowSurrogateValue()
    {
        string json = Valid().Replace("\"defaultDecision\": \"review\"", "\"defaultDecision\": \"\\uDC00\"", StringComparison.Ordinal);
        AssertInvalidEscapedUnicode(json);
    }

    private static void RejectHighSurrogatePropertyName()
    {
        string json = Valid().Replace("\"schemaVersion\"", "\"\\uD800\"", StringComparison.Ordinal);
        AssertInvalidEscapedUnicode(json);
    }

    private static void RejectLowSurrogatePropertyName()
    {
        string json = Valid().Replace("\"schemaVersion\"", "\"\\uDC00\"", StringComparison.Ordinal);
        AssertInvalidEscapedUnicode(json);
    }

    private static void RejectNestedSurrogateValue()
    {
        string policyOverride = Override().Replace(
            "Reviewed against the tagged source evidence.",
            "Reviewed \\uD800 evidence.",
            StringComparison.Ordinal);
        AssertInvalidEscapedUnicode(Valid("[" + policyOverride + "]"));
    }

    private static void AcceptSurrogatePair()
    {
        string policyOverride = Override().Replace(
            "Reviewed against the tagged source evidence.",
            "Reviewed \\uD83D\\uDD0D evidence.",
            StringComparison.Ordinal);
        PolicyConfiguration policy = PolicyConfigurationParser.Parse(Valid("[" + policyOverride + "]"));
        Assert.Equal("Reviewed 🔍 evidence.", policy.Overrides[0].Reason);
    }

    private static void RejectEscapedSurrogateUtf8Input()
    {
        string json = Valid().Replace("\"defaultDecision\": \"review\"", "\"defaultDecision\": \"\\uD800\"", StringComparison.Ordinal);
        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        PolicyConfigurationException error = Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(utf8));
        Assert.Equal(PolicyDiagnosticCodes.InvalidConfiguration, error.Code);
        Assert.True(error.InnerException is InvalidOperationException);
    }

    private static void AssertInvalidEscapedUnicode(string json)
    {
        PolicyConfigurationException error = Assert.Throws<PolicyConfigurationException>(() => PolicyConfigurationParser.Parse(json));
        Assert.Equal(PolicyDiagnosticCodes.InvalidConfiguration, error.Code);
        Assert.Equal("", error.JsonPointer);
        Assert.True(error.InnerException is InvalidOperationException);
    }
}
