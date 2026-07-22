using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using WebUIToolkit.DependencyNotices.Engine;

namespace WebUIToolkit.DependencyNotices.Tests;

internal static class FixtureCorpusTests
{
    public static void Register(TestHarness tests)
    {
        tests.Add("all versioned schemas are valid JSON without an id", SchemasAreVersionedJson);
        tests.Add("manual corpus has 30 or more declared cases", CorpusHasRequiredBreadth);
        tests.Add("manual corpus evidence filenames match exact bytes", EvidenceDigestsMatch);
        tests.Add("manual corpus scanner outcomes match the manifest", CorpusOutcomesMatch);
    }

    private static void SchemasAreVersionedJson()
    {
        string specificationRoot = SpecificationRoot();
        string[] schemas = Directory.GetFiles(specificationRoot, "*.schema.v1.json", SearchOption.TopDirectoryOnly);
        Assert.Equal(5, schemas.Length);
        foreach (string schema in schemas)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(schema));
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.True(!document.RootElement.TryGetProperty("$id", out _), "Schema IDs must remain unset until the domain is owned.");
        }
    }

    private static void CorpusHasRequiredBreadth()
    {
        using JsonDocument manifest = ReadManifest();
        JsonElement cases = manifest.RootElement.GetProperty("cases");
        Assert.True(cases.GetArrayLength() >= 30);
        HashSet<string> ids = new(StringComparer.Ordinal);
        string corpusRoot = CorpusRoot();
        foreach (JsonElement item in cases.EnumerateArray())
        {
            Assert.True(ids.Add(item.GetProperty("id").GetString()!));
            string file = item.GetProperty("file").GetString()!;
            Assert.True(File.Exists(SafePath.ResolveContainedPath(corpusRoot, file)), $"Missing fixture {file}.");
        }
    }

    private static void EvidenceDigestsMatch()
    {
        using JsonDocument manifest = ReadManifest();
        string corpusRoot = CorpusRoot();
        string evidenceRoot = manifest.RootElement.GetProperty("evidenceRoot").GetString()!;
        foreach (JsonElement item in manifest.RootElement.GetProperty("evidence").EnumerateArray())
        {
            string digest = item.GetProperty("sha256").GetString()!;
            string file = SafePath.ResolveContainedPath(corpusRoot, $"{evidenceRoot}/{digest}");
            byte[] bytes = File.ReadAllBytes(file);
            Assert.Equal(item.GetProperty("length").GetInt32(), bytes.Length);
            Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    private static void CorpusOutcomesMatch()
    {
        using JsonDocument manifest = ReadManifest();
        string corpusRoot = CorpusRoot();
        foreach (JsonElement item in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            string id = item.GetProperty("id").GetString()!;
            string file = item.GetProperty("file").GetString()!;
            JsonElement expected = item.GetProperty("expected");
            bool inputValid = expected.GetProperty("inputValid").GetBoolean();
            ManualScanResult result = ManualComponentScanner.Scan(corpusRoot, file);
            Assert.True(
                inputValid == result.Succeeded,
                $"Case {id} expected inputValid={inputValid} but Succeeded={result.Succeeded}; diagnostics: {string.Join(", ", result.Diagnostics.Select(diagnostic => diagnostic.Code))}.");

            if (!inputValid)
            {
                HashSet<string> expectedCodes = expected.GetProperty("diagnostics")
                    .EnumerateArray()
                    .Select(value => value.GetString()!)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.True(result.Diagnostics.Any(diagnostic => expectedCodes.Contains(diagnostic.Code)), $"Case {id} did not report one of its expected diagnostics.");
                continue;
            }

            string[] expectedPurls = expected.GetProperty("canonicalPurls")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            string[] actualPurls = result.Components
                .Select(component => component.PackageUrl.CanonicalValue)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(string.Join('\n', expectedPurls), string.Join('\n', actualPurls));
        }
    }

    private static JsonDocument ReadManifest() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(CorpusRoot(), "case-manifest.json")));

    private static string CorpusRoot() => Path.Combine(SpecificationRoot(), "fixtures", "manual");

    private static string SpecificationRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "spec", "dependency-notices");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate spec/dependency-notices from the test executable.");
    }
}
