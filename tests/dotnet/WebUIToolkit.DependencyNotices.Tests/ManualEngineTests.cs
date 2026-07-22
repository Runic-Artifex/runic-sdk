using System;
using System.IO;
using System.Text;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Engine;
using WebUIToolkit.DependencyNotices.Evidence;

namespace WebUIToolkit.DependencyNotices.Tests;

internal static class ManualEngineTests
{
    public static void Register(TestHarness tests)
    {
        tests.Add("manual scan verifies evidence and sorts deterministically", ManualScanSucceeds);
        tests.Add("manual scan fails closed on digest mismatch", DigestMismatch);
        tests.Add("manual scan rejects duplicate canonical identities", DuplicateIdentity);
        tests.Add("manual scan rejects duplicate JSON properties", DuplicateJsonProperty);
    }

    private static void ManualScanSucceeds()
    {
        WithFixture((root, evidencePath, digest) =>
        {
            WriteConfig(root, $$"""
                {"schemaVersion":1,"manualComponents":[
                  {"purl":"pkg:generic/zeta@2","displayName":"Zeta","revision":"z2","licenseExpression":"MIT","evidence":[{"kind":"license","sha256":"{{digest}}","path":"{{evidencePath}}","origin":"fixture://test"}]},
                  {"purl":"pkg:generic/alpha@1","displayName":"Alpha","revision":"a1","licenseExpression":"Apache-2.0 WITH LLVM-exception","evidence":[{"kind":"license","sha256":"{{digest}}","path":"{{evidencePath}}","origin":"fixture://test"}]}
                ]}
                """);

            ManualScanResult result = ManualComponentScanner.Scan(root, "dependency-notices.json");
            Assert.True(result.Succeeded);
            Assert.Equal(2, result.Components.Count);
            Assert.Equal("Alpha", result.Components[0].DisplayName);
            Assert.Equal("Zeta", result.Components[1].DisplayName);
            Assert.Equal(0, result.Diagnostics.Count);
        });
    }

    private static void DigestMismatch()
    {
        WithFixture((root, evidencePath, _) =>
        {
            WriteConfig(root, $$"""
                {"schemaVersion":1,"manualComponents":[
                  {"purl":"pkg:generic/widget@1","displayName":"Widget","revision":"w1","licenseExpression":"MIT","evidence":[{"kind":"license","sha256":"{{new string('0', 64)}}","path":"{{evidencePath}}","origin":"fixture://test"}]}
                ]}
                """);
            ManualScanResult result = ManualComponentScanner.Scan(root, "dependency-notices.json");
            Assert.True(!result.Succeeded);
            Assert.Equal(NoticeDiagnosticCodes.EvidenceDigestMismatch, result.Diagnostics[0].Code);
        });
    }

    private static void DuplicateIdentity()
    {
        WithFixture((root, evidencePath, digest) =>
        {
            WriteConfig(root, $$"""
                {"schemaVersion":1,"manualComponents":[
                  {"purl":"pkg:generic/widget@1?b=2&a=1","displayName":"Widget","revision":"w1","licenseExpression":"MIT","evidence":[{"kind":"license","sha256":"{{digest}}","path":"{{evidencePath}}","origin":"fixture://test"}]},
                  {"purl":"pkg:generic/widget@1?a=1&b=2","displayName":"Widget duplicate","revision":"w1","licenseExpression":"MIT","evidence":[{"kind":"license","sha256":"{{digest}}","path":"{{evidencePath}}","origin":"fixture://test"}]}
                ]}
                """);
            ManualScanResult result = ManualComponentScanner.Scan(root, "dependency-notices.json");
            Assert.True(!result.Succeeded);
            Assert.Equal(NoticeDiagnosticCodes.DuplicatePackageUrl, result.Diagnostics[0].Code);
        });
    }

    private static void DuplicateJsonProperty()
    {
        WithFixture((root, _, _) =>
        {
            WriteConfig(root, "{\"schemaVersion\":1,\"schemaVersion\":1,\"manualComponents\":[]}");
            ManualScanResult result = ManualComponentScanner.Scan(root, "dependency-notices.json");
            Assert.True(!result.Succeeded);
            Assert.Equal(NoticeDiagnosticCodes.InvalidManualComponent, result.Diagnostics[0].Code);
        });
    }

    private static void WithFixture(Action<string, string, string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "wut-notices-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        try
        {
            const string relativeEvidence = "assets/license.txt";
            byte[] bytes = Encoding.UTF8.GetBytes("Synthetic fixture evidence.\n");
            File.WriteAllBytes(Path.Combine(root, "assets", "license.txt"), bytes);
            action(root, relativeEvidence, EvidenceDigest.ComputeSha256(bytes));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteConfig(string root, string content) =>
        File.WriteAllText(Path.Combine(root, "dependency-notices.json"), content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
