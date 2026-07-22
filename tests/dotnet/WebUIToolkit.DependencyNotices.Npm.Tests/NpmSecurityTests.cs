using System;
using System.IO;
using System.Linq;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Npm.Tests;

internal static class NpmSecurityTests
{
    public static void Register(TestHarness tests)
    {
        tests.Add("npm duplicate JSON properties are rejected", DuplicatePropertiesAreRejected);
        tests.Add("npm lone surrogate string is rejected", LoneSurrogateStringIsRejected);
        tests.Add("npm lone surrogate property name is rejected", LoneSurrogatePropertyNameIsRejected);
        tests.Add("npm manifest lone surrogate never leaks", ManifestLoneSurrogateNeverLeaks);
        tests.Add("npm valid surrogate pair remains accepted", ValidSurrogatePairIsAccepted);
        tests.Add("npm unsafe package entry paths are rejected", UnsafePackageEntryPathsAreRejected);
        tests.Add("npm escaping workspace links are rejected", EscapingWorkspaceLinksAreRejected);
        tests.Add("npm invalid integrity is rejected", InvalidIntegrityIsRejected);
        tests.Add("npm malformed license metadata is diagnosed", MalformedLicenseMetadataIsDiagnosed);
        tests.Add("npm URL-only license metadata never acquires", UrlOnlyLicenseNeverAcquires);
        tests.Add("npm traversal selection is rejected", TraversalSelectionIsRejected);
        tests.Add("npm source diagnostics contain no absolute root", DiagnosticsContainNoAbsoluteRoot);
    }

    private static void DuplicatePropertiesAreRejected() =>
        AssertCode("duplicate-property", NoticeDiagnosticCodes.InvalidDependencyGraph);

    private static void LoneSurrogateStringIsRejected() =>
        AssertCode("lone-surrogate-string", NoticeDiagnosticCodes.InvalidDependencyGraph);

    private static void LoneSurrogatePropertyNameIsRejected() =>
        AssertCode("lone-surrogate-property", NoticeDiagnosticCodes.InvalidDependencyGraph);

    private static void ManifestLoneSurrogateNeverLeaks() =>
        AssertCode("lone-surrogate-manifest", NoticeDiagnosticCodes.InvalidDependencyGraph);

    private static void ValidSurrogatePairIsAccepted()
    {
        InventoryResult result = NpmInventoryTests.Scan("valid-surrogate-pair", NpmInventoryProfile.Runtime);
        Assert.Equal(0, result.Diagnostics.Count);
    }

    private static void UnsafePackageEntryPathsAreRejected() =>
        AssertCode("unsafe-path", NoticeDiagnosticCodes.InvalidDependencyGraph);

    private static void EscapingWorkspaceLinksAreRejected() =>
        AssertCode("unsafe-link", NoticeDiagnosticCodes.InvalidDependencyGraph);

    private static void InvalidIntegrityIsRejected() =>
        AssertCode("invalid-integrity", NoticeDiagnosticCodes.InvalidDependencyGraph);

    private static void MalformedLicenseMetadataIsDiagnosed() =>
        AssertCode("invalid-license", NoticeDiagnosticCodes.InvalidEvidenceEncoding);

    private static void UrlOnlyLicenseNeverAcquires()
    {
        InventoryResult result = NpmInventoryTests.Scan("url-license", NpmInventoryProfile.Runtime);
        Assert.True(result.Diagnostics.Any(static diagnostic => diagnostic.Code == NoticeDiagnosticCodes.UrlOnlyEvidence));
        Assert.Equal(null, result.Components[0].ObservedLicenseExpression);
    }

    private static void TraversalSelectionIsRejected()
    {
        InventoryResult result = NpmInventoryScanner.Scan(new NpmInventoryOptions(
            NpmInventoryTests.Fixture("basic"), "package-lock.json", "../outside", NpmInventoryProfile.Runtime));
        Assert.Equal(NoticeDiagnosticCodes.InvalidDependencyGraph, Assert.Single(result.Diagnostics).Code);
    }

    private static void DiagnosticsContainNoAbsoluteRoot()
    {
        string root = NpmInventoryTests.Fixture("missing-node-modules");
        InventoryResult result = NpmInventoryTests.Scan("missing-node-modules", NpmInventoryProfile.Runtime);
        Assert.True(result.Diagnostics.All(diagnostic =>
            !diagnostic.Message.Contains(root, StringComparison.OrdinalIgnoreCase) &&
            !(diagnostic.Source?.Contains(root, StringComparison.OrdinalIgnoreCase) ?? false)));
    }

    private static void AssertCode(string fixture, string code)
    {
        InventoryResult result = NpmInventoryTests.Scan(fixture, NpmInventoryProfile.Runtime);
        Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == code),
            $"Expected diagnostic {code}; actual: {string.Join(',', result.Diagnostics.Select(static item => item.Code))}");
    }
}
