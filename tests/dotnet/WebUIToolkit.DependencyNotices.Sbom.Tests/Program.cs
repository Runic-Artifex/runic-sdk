using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Sbom;

namespace WebUIToolkit.DependencyNotices.Sbom.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        TestHarness tests = new();
        tests.Add("CycloneDX preserves serial, document, and component references", CycloneDxReferences);
        tests.Add("SPDX preserves document and package references", SpdxReferences);
        tests.Add("canonical Package URL matching wins", ExactPurlMatching);
        tests.Add("fallback matches exact ecosystem name and version", FallbackMatching);
        tests.Add("SPDX package-manager references support exact fallback", SpdxFallbackMatching);
        tests.Add("fallback is rejected when inventory identity is ambiguous", AmbiguousFallback);
        tests.Add("missing components report WUTNOTICE5001", Missing);
        tests.Add("extra components report WUTNOTICE5002", Extra);
        tests.Add("version mismatch reports WUTNOTICE5003", Mismatch);
        tests.Add("duplicate component references report WUTNOTICE5004", DuplicateReferences);
        tests.Add("diagnostics are deterministic under input permutation", DeterministicOrdering);
        tests.Add("duplicate JSON properties are rejected", DuplicateProperties);
        tests.Add("property limits are enforced", PropertyLimit);
        tests.Add("depth limits are enforced", DepthLimit);
        tests.Add("byte limits are enforced for streams", ByteLimit);
        tests.Add("component limits include nested CycloneDX components", ComponentLimit);
        tests.Add("invalid PURLs fail closed", InvalidPurl);
        tests.Add("conflicting SPDX PURLs fail closed", ConflictingPurls);
        tests.Add("unknown JSON formats are rejected", UnknownFormat);
        tests.Add("escaped lone surrogate values produce a stable domain diagnostic", LoneSurrogateValue);
        tests.Add("escaped lone surrogate property names produce a stable domain diagnostic", LoneSurrogatePropertyName);
        return await tests.RunAsync().ConfigureAwait(false);
    }

    private static void CycloneDxReferences()
    {
        SbomDocument document = ReadFixture("cyclonedx-match.json");
        Assert.Equal(SbomFormat.CycloneDxJson, document.Format);
        Assert.Equal("urn:uuid:27b2012a-c8f1-4d4e-ae86-5fb15390eb0f", document.DocumentReference);
        Assert.Equal("urn:uuid:27b2012a-c8f1-4d4e-ae86-5fb15390eb0f", document.SerialNumber);
        Assert.Equal("cdx-npm-leftpad", document.Components[0].ComponentReference);
        Assert.Equal("pkg:npm/left-pad@1.3.0", document.Components[0].PackageUrl!.CanonicalValue);
    }

    private static void SpdxReferences()
    {
        SbomDocument document = ReadFixture("spdx-match.json");
        Assert.Equal(SbomFormat.SpdxJson, document.Format);
        Assert.Equal("https://example.invalid/spdx/fixture-app-1", document.DocumentReference);
        Assert.Equal(null, document.SerialNumber);
        Assert.Equal("SPDXRef-Package-Json", document.Components[0].ComponentReference);
        Assert.Equal("pkg:nuget/System.Text.Json@10.0.0", document.Components[0].PackageUrl!.CanonicalValue);
    }

    private static void ExactPurlMatching()
    {
        SbomDocument document = ReadFixture("cyclonedx-match.json");
        SbomReconciliationResult result = SbomReconciler.Reconcile(
            [Identity("pkg:npm/left-pad@1.3.0", "different display name", "9")],
            document);
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Links.Count);
        Assert.Equal("cdx-npm-leftpad", result.Links[0].ComponentReference);
        Assert.Equal(document.SerialNumber, result.SerialNumber);
    }

    private static void FallbackMatching()
    {
        SbomDocument document = ReadFixture("cyclonedx-fallback.json");
        SbomReconciliationResult result = SbomReconciler.Reconcile(
            [Identity("pkg:npm/%40scope/widget@2.0.0", "@scope/widget", "2.0.0")],
            document);
        Assert.True(result.Succeeded);
        Assert.Equal("fallback-widget", result.Links[0].ComponentReference);
    }

    private static void AmbiguousFallback()
    {
        SbomDocument document = ReadFixture("cyclonedx-fallback.json");
        SbomReconciliationResult result = SbomReconciler.Reconcile(
            [
                Identity("pkg:npm/%40scope/widget@2.0.0", "@scope/widget", "2.0.0"),
                Identity("pkg:npm/widget@2.0.0", "@scope/widget", "2.0.0"),
            ],
            document);
        Assert.True(!result.Succeeded);
        Assert.Equal(2, Count(result, NoticeDiagnosticCodes.SbomComponentMissing));
        Assert.Equal(1, Count(result, NoticeDiagnosticCodes.SbomComponentExtra));
    }

    private static void SpdxFallbackMatching()
    {
        SbomReconciliationResult result = SbomReconciler.Reconcile(
            [Identity("pkg:nuget/Example.Library@3.0.0", "Example.Library", "3.0.0")],
            ReadFixture("spdx-fallback.json"));
        Assert.True(result.Succeeded);
        Assert.Equal("SPDXRef-Example", result.Links[0].ComponentReference);
    }

    private static void Missing()
    {
        SbomReconciliationResult result = SbomReconciler.Reconcile(
            [Identity("pkg:nuget/Missing@1.0.0", "Missing", "1.0.0")],
            ReadFixture("empty-cyclonedx.json"));
        AssertCodes(result, NoticeDiagnosticCodes.SbomComponentMissing);
    }

    private static void Extra()
    {
        SbomReconciliationResult result = SbomReconciler.Reconcile([], ReadFixture("cyclonedx-match.json"));
        AssertCodes(result, NoticeDiagnosticCodes.SbomComponentExtra);
    }

    private static void Mismatch()
    {
        SbomReconciliationResult result = SbomReconciler.Reconcile(
            [Identity("pkg:npm/left-pad@2.0.0", "left-pad", "2.0.0")],
            ReadFixture("cyclonedx-match.json"));
        AssertCodes(result, NoticeDiagnosticCodes.SbomIdentityMismatch);
    }

    private static void DuplicateReferences()
    {
        SbomReconciliationResult result = SbomReconciler.Reconcile([], ReadFixture("duplicate-references.json"));
        AssertCodes(result, NoticeDiagnosticCodes.DuplicateSbomReference);
        Assert.True(result.Diagnostics[0].Message.Contains("dup-ref", StringComparison.Ordinal));
    }

    private static void DeterministicOrdering()
    {
        SbomDocument document = ReadFixture("reconciliation-mixed.json");
        SbomInventoryIdentity[] first =
        [
            Identity("pkg:nuget/Absent@1.0.0", "Absent", "1.0.0"),
            Identity("pkg:npm/left-pad@2.0.0", "left-pad", "2.0.0"),
        ];
        SbomReconciliationResult left = SbomReconciler.Reconcile(first, document);
        SbomReconciliationResult right = SbomReconciler.Reconcile(first.Reverse(), new SbomDocument(
            document.Format,
            document.DocumentReference,
            document.SerialNumber,
            document.Components.Reverse().ToArray()));
        Assert.Equal(Signature(left), Signature(right));
        Assert.Equal("WUTNOTICE5001\nWUTNOTICE5002\nWUTNOTICE5003", string.Join('\n', left.Diagnostics.Select(static item => item.Code)));
    }

    private static void DuplicateProperties() =>
        Assert.Throws<SbomFormatException>(() => ReadUtf8("{\"bomFormat\":\"CycloneDX\",\"bomFormat\":\"CycloneDX\"}"));

    private static void PropertyLimit() =>
        Assert.Throws<SbomFormatException>(() => ReadUtf8("{\"bomFormat\":\"CycloneDX\",\"components\":[]}", new SbomReadLimits(MaximumProperties: 1)));

    private static void DepthLimit() =>
        Assert.Throws<SbomFormatException>(() => ReadUtf8("{\"bomFormat\":\"CycloneDX\",\"x\":{\"y\":{\"z\":1}}}", new SbomReadLimits(MaximumDepth: 2)));

    private static void ByteLimit()
    {
        byte[] value = Encoding.UTF8.GetBytes("{\"bomFormat\":\"CycloneDX\"}");
        using MemoryStream stream = new(value, writable: false);
        Assert.Throws<SbomFormatException>(() => SbomReader.Read(stream, new SbomReadLimits(MaximumBytes: value.Length - 1)));
    }

    private static void ComponentLimit() =>
        Assert.Throws<SbomFormatException>(() =>
            SbomReader.Read(File.OpenRead(FixturePath("nested-components.json")), new SbomReadLimits(MaximumComponents: 1)));

    private static void InvalidPurl() =>
        Assert.Throws<SbomFormatException>(() => ReadUtf8("{\"bomFormat\":\"CycloneDX\",\"components\":[{\"bom-ref\":\"x\",\"name\":\"x\",\"version\":\"1\",\"purl\":\"not-a-purl\"}]}"));

    private static void ConflictingPurls() =>
        Assert.Throws<SbomFormatException>(() => ReadFixture("spdx-conflicting-purls.json"));

    private static void UnknownFormat() =>
        Assert.Throws<SbomFormatException>(() => ReadUtf8("{\"format\":\"other\"}"));

    private static void LoneSurrogateValue()
    {
        SbomFormatException exception = Assert.Throws<SbomFormatException>(() =>
            ReadUtf8("{\"bomFormat\":\"CycloneDX\",\"specVersion\":\"1.6\",\"components\":[{\"name\":\"\\uD800\",\"version\":\"1\"}]}"));
        AssertStableDomainDiagnostic(exception);
    }

    private static void LoneSurrogatePropertyName()
    {
        SbomFormatException exception = Assert.Throws<SbomFormatException>(() =>
            ReadUtf8("{\"bomFormat\":\"CycloneDX\",\"\\uD800\":true}"));
        AssertStableDomainDiagnostic(exception);
    }

    private static void AssertStableDomainDiagnostic(SbomFormatException exception)
    {
        Assert.Equal(NoticeDiagnosticCodes.SchemaIncompatible, SbomFormatException.StableDiagnosticCode);
        NoticeDiagnostic diagnostic = exception.ToDiagnostic("fixture.json");
        Assert.Equal("WUTNOTICE6003", diagnostic.Code);
        Assert.Equal(NoticeDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("fixture.json", diagnostic.Source);
    }

    private static SbomDocument ReadFixture(string name)
    {
        using FileStream stream = File.OpenRead(FixturePath(name));
        return SbomReader.Read(stream);
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static SbomDocument ReadUtf8(string json, SbomReadLimits? limits = null) =>
        SbomReader.Read(Encoding.UTF8.GetBytes(json), limits);

    private static SbomInventoryIdentity Identity(string purl, string name, string version) => new(PackageUrl.Parse(purl), name, version);

    private static int Count(SbomReconciliationResult result, string code) => result.Diagnostics.Count(item => item.Code == code);

    private static void AssertCodes(SbomReconciliationResult result, params string[] expected) =>
        Assert.Equal(string.Join('\n', expected), string.Join('\n', result.Diagnostics.Select(static item => item.Code)));

    private static string Signature(SbomReconciliationResult result) => string.Join(
        '\n',
        result.Diagnostics.Select(static item => $"{item.Code}|{item.PackageUrl}|{item.Source}|{item.Message}"));
}

internal sealed class TestHarness
{
    private readonly List<(string Name, Action Test)> _tests = [];

    public void Add(string name, Action test) => _tests.Add((name, test));

    public ValueTask<int> RunAsync()
    {
        int failures = 0;
        foreach ((string name, Action test) in _tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception}");
            }
        }

        Console.WriteLine($"Executed {_tests.Count} tests; {failures} failed.");
        return ValueTask.FromResult(failures == 0 ? 0 : 1);
    }
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
        }
    }

    public static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    public static T Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
