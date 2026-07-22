using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Engine;

namespace WebUIToolkit.DependencyNotices.Security.Tests;

internal static class ManualInputSecurityTests
{
    private const string InvalidManual = "WUTNOTICE1002";
    private const string InvalidEvidenceEncoding = "WUTNOTICE2005";

    public static void Register(TestHarness tests)
    {
        tests.Add("manual config rejects oversized JSON with diagnostic", RejectsOversizedConfig);
        tests.Add("manual config rejects excessive JSON depth", RejectsDeepJson);
        tests.Add("manual config rejects duplicate security property", RejectsDuplicateProperty);
        tests.Add("manual config rejects invalid UTF-8", RejectsInvalidUtf8);
        tests.Add("manual config rejects lone Unicode surrogate", RejectsLoneSurrogate);
        tests.Add("manual config enforces token and property budget", RejectsExcessiveTokenBudget);
        tests.Add("manual evidence rejects oversized text with diagnostic", RejectsOversizedEvidence);
        tests.Add("manual evidence rejects invalid UTF-8", RejectsInvalidEvidenceUtf8);
    }

    private static void RejectsOversizedConfig()
    {
        TestFiles.WithTemporaryDirectory(root =>
        {
            string path = Path.Combine(root, "oversized.json");
            using (FileStream stream = File.Create(path))
            {
                stream.SetLength(1_048_577);
            }

            AssertDiagnostic(ManualComponentScanner.Scan(root, "oversized.json"), InvalidManual);
        });
    }

    private static void RejectsDeepJson()
    {
        TestFiles.WithTemporaryDirectory(root =>
        {
            File.Copy(TestFiles.Fixture("deep-json.json"), Path.Combine(root, "deep.json"));
            AssertDiagnostic(ManualComponentScanner.Scan(root, "deep.json"), InvalidManual);
        });
    }

    private static void RejectsDuplicateProperty()
    {
        TestFiles.WithTemporaryDirectory(root =>
        {
            File.Copy(TestFiles.Fixture("duplicate-properties.json"), Path.Combine(root, "duplicate.json"));
            AssertDiagnostic(ManualComponentScanner.Scan(root, "duplicate.json"), InvalidManual);
        });
    }

    private static void RejectsInvalidUtf8()
    {
        TestFiles.WithTemporaryDirectory(root =>
        {
            byte[] bytes = Convert.FromHexString(File.ReadAllText(TestFiles.Fixture("invalid-utf8.hex"), Encoding.ASCII).Trim());
            File.WriteAllBytes(Path.Combine(root, "invalid.json"), bytes);
            AssertDiagnostic(ManualComponentScanner.Scan(root, "invalid.json"), InvalidManual);
        });
    }

    private static void RejectsLoneSurrogate()
    {
        TestFiles.WithTemporaryDirectory(root =>
        {
            File.Copy(TestFiles.Fixture("lone-surrogate.json"), Path.Combine(root, "surrogate.json"));
            AssertDiagnostic(ManualComponentScanner.Scan(root, "surrogate.json"), InvalidManual);
        });
    }

    private static void RejectsExcessiveTokenBudget()
    {
        TestFiles.WithTemporaryDirectory(root =>
        {
            StringBuilder builder = new(250_100);
            _ = builder.Append("{\"schemaVersion\":1,\"manualComponents\":[");
            for (int index = 0; index < 100_001; index++)
            {
                if (index != 0)
                {
                    _ = builder.Append(',');
                }

                _ = builder.Append('0');
            }

            _ = builder.Append("]}");
            TestFiles.WriteUtf8(Path.Combine(root, "tokens.json"), builder.ToString());
            AssertDiagnostic(ManualComponentScanner.Scan(root, "tokens.json"), InvalidManual);
        });
    }

    private static void RejectsOversizedEvidence()
    {
        TestFiles.WithTemporaryDirectory(root =>
        {
            string evidencePath = Path.Combine(root, "evidence.txt");
            using (FileStream stream = File.Create(evidencePath))
            {
                stream.SetLength(16_777_217);
            }

            WriteManualConfig(root, "evidence.txt", new string('0', 64));
            AssertDiagnostic(ManualComponentScanner.Scan(root, "dependency-notices.json"), InvalidEvidenceEncoding);
        });
    }

    private static void RejectsInvalidEvidenceUtf8()
    {
        TestFiles.WithTemporaryDirectory(root =>
        {
            byte[] invalidUtf8 = [0xC3, 0x28];
            File.WriteAllBytes(Path.Combine(root, "evidence.txt"), invalidUtf8);
            WriteManualConfig(root, "evidence.txt", Convert.ToHexStringLower(SHA256.HashData(invalidUtf8)));
            AssertDiagnostic(ManualComponentScanner.Scan(root, "dependency-notices.json"), InvalidEvidenceEncoding);
        });
    }

    private static void WriteManualConfig(string root, string evidencePath, string digest)
    {
        string json = $$"""
        {
          "schemaVersion": 1,
          "manualComponents": [
            {
              "purl": "pkg:generic/hostile@1.0.0",
              "displayName": "Hostile fixture",
              "revision": "fixture",
              "licenseExpression": "MIT",
              "evidence": [
                {
                  "kind": "license",
                  "path": "{{evidencePath}}",
                  "origin": "fixture",
                  "sha256": "{{digest}}"
                }
              ]
            }
          ]
        }
        """;
        TestFiles.WriteUtf8(Path.Combine(root, "dependency-notices.json"), json);
    }

    private static void AssertDiagnostic(ManualScanResult result, string code)
    {
        Assert.False(result.Succeeded, "Hostile input must fail closed.");
        Assert.True(result.Diagnostics.Any(diagnostic => diagnostic.Code == code), $"Expected diagnostic {code}.");
        Assert.True(result.Diagnostics.All(diagnostic => diagnostic.Code.StartsWith("WUTNOTICE", StringComparison.Ordinal)), "All failures must use stable WUTNOTICE codes.");
    }
}
