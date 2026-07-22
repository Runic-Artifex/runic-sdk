using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebUIToolkit.DependencyNotices;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Policy;
using WebUIToolkit.DependencyNotices.Rendering;

namespace WebUIToolkit.DependencyNotices.Rendering.Tests;

internal static class Program
{
    private static readonly string GoldenRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", "expected");
    private static int passed;
    private static int failed;

    private static int Main(string[] args)
    {
        if (args.Length == 2 && StringComparer.Ordinal.Equals(args[0], "--write-goldens"))
        {
            Directory.CreateDirectory(args[1]);
            foreach (RenderedNoticeOutput output in RenderSample().Outputs)
            {
                File.WriteAllBytes(Path.Combine(args[1], output.FileName), output.Content);
            }

            return 0;
        }

        Run("canonical JSON golden", GoldenJson);
        Run("text golden", GoldenText);
        Run("HTML golden", GoldenHtml);
        Run("manifest golden", GoldenManifest);
        Run("repeat render is byte identical", RepeatRender);
        Run("input order does not affect bytes", InputOrderIndependent);
        Run("culture does not affect bytes", CultureIndependent);
        Run("all outputs are UTF-8 without BOM", Utf8WithoutBom);
        Run("generated fixture uses LF", GoldenUsesLf);
        Run("canonical JSON is complete and ordered", CanonicalJsonComplete);
        Run("evidence text survives JSON", JsonPreservesEvidence);
        Run("text evidence is digest-linked and deduplicated", TextDeduplicatesByDigest);
        Run("HTML is escaped and standalone", HtmlIsSafe);
        Run("direct HTML rendering escapes hostile digest", HostileDigestIsEscaped);
        Run("HTML is semantic and printable", HtmlIsSemantic);
        Run("schema v1 is rejected with WUTNOTICE6003", RejectsSchemaV1);
        Run("invalid evidence digest is rejected", RejectsInvalidDigest);
        Run("evidence digest mismatch is rejected", RejectsDigestMismatch);
        Run("conflicting digest-linked text is rejected", RejectsConflictingDigestText);
        Run("invalid UTF-16 is rejected", RejectsInvalidUtf16);
        Run("duplicate dependency identity is rejected", RejectsDuplicateIdentity);
        Run("noncanonical Package URL is rejected", RejectsNoncanonicalPackageUrl);
        Run("required document string is rejected", RejectsMissingRequiredString);
        Run("invalid SPDX expression is rejected", RejectsInvalidSpdx);
        Run("invalid schema enum is rejected", RejectsInvalidEnum);
        Run("invalid SBOM format is rejected", RejectsInvalidSbom);
        Run("host path is rejected with WUTNOTICE6004", RejectsHostPath);
        Run("manifest entries use ordinal ordering", ManifestIsOrdered);
        Run("manifest excludes clock and host state", ManifestHasNoAmbientState);
        Run("verifier accepts exact bytes", VerifierAcceptsExact);
        Run("verifier reports WUTNOTICE6002 drift", VerifierReportsDrift);
        Run("writer writes exact bytes", WriterWritesExactBytes);
        Run("writer rejects traversal with WUTNOTICE6004", WriterRejectsTraversal);
        Run("writer rejects duplicate names before writing", WriterRejectsDuplicates);
        Run("writer rejects linked output-root ancestor", WriterRejectsLinkedAncestor);
        Run("writer rejects linked destination", WriterRejectsLinkedDestination);

        Console.WriteLine($"Rendering tests: {passed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static void GoldenJson() => AssertGolden(NoticeOutputNames.Json);

    private static void GoldenText() => AssertGolden(NoticeOutputNames.Text);

    private static void GoldenHtml() => AssertGolden(NoticeOutputNames.Html);

    private static void GoldenManifest() => AssertGolden(NoticeOutputNames.Manifest);

    private static void RepeatRender()
    {
        NoticeRenderResult first = RenderSample();
        NoticeRenderResult second = RenderSample();
        AssertBundlesEqual(first, second);
    }

    private static void InputOrderIndependent()
    {
        DependencyNoticeDocument sample = SampleDocument();
        DependencyNoticeDocument reversed = sample with
        {
            Dependencies = sample.Dependencies.Reverse().ToArray(),
            Diagnostics = sample.Diagnostics.Reverse().ToArray(),
        };
        AssertBundlesEqual(RenderSample(), DependencyNoticeRenderer.Render(reversed, SampleOptions(reverse: true)));
    }

    private static void CultureIndependent()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            NoticeRenderResult turkish = RenderSample();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            AssertBundlesEqual(turkish, RenderSample());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static void Utf8WithoutBom()
    {
        foreach (RenderedNoticeOutput output in RenderSample().Outputs)
        {
            Assert(output.Content.Length >= 3, output.FileName + " unexpectedly empty");
            Assert(!(output.Content[0] == 0xef && output.Content[1] == 0xbb && output.Content[2] == 0xbf), output.FileName + " has BOM");
            _ = new UTF8Encoding(false, true).GetString(output.Content);
        }
    }

    private static void GoldenUsesLf()
    {
        foreach (RenderedNoticeOutput output in RenderSample().Outputs)
        {
            Assert(Array.IndexOf(output.Content, (byte)'\r') < 0, output.FileName + " contains CR");
            Assert(output.Content[^1] == (byte)'\n', output.FileName + " lacks trailing LF");
        }
    }

    private static void CanonicalJsonComplete()
    {
        using JsonDocument json = JsonDocument.Parse(Output(NoticeOutputNames.Json).Content);
        JsonElement root = json.RootElement;
        Assert(root.GetProperty("schemaVersion").GetInt32() == 2, "schema version");
        Assert(root.GetProperty("sbom").GetProperty("serialNumber").GetString() == "urn:uuid:test", "SBOM missing");
        JsonElement dependencies = root.GetProperty("dependencies");
        Assert(dependencies[0].GetProperty("name").GetString() == "Alpha & <Co>", "dependency order");
        Assert(dependencies[0].GetProperty("sbomComponentReference").GetString() == "component-alpha", "component ref missing");
        Assert(dependencies[0].GetProperty("assets")[0].TryGetProperty("text", out _), "asset text missing");
    }

    private static void JsonPreservesEvidence()
    {
        using JsonDocument json = JsonDocument.Parse(Output(NoticeOutputNames.Json).Content);
        string? actual = json.RootElement.GetProperty("dependencies")[0].GetProperty("assets")[0].GetProperty("text").GetString();
        Assert(StringComparer.Ordinal.Equals(actual, SharedEvidence), "evidence text changed");
    }

    private static void TextDeduplicatesByDigest()
    {
        string text = Encoding.UTF8.GetString(Output(NoticeOutputNames.Text).Content);
        Assert(Count(text, "BEGIN EVIDENCE sha256:" + SharedDigest) == 1, "shared evidence printed more than once");
        Assert(Count(text, "Evidence: license sha256:" + SharedDigest) == 2, "component digest links missing");
        Assert(text.Contains(SharedEvidence, StringComparison.Ordinal), "evidence body changed");
    }

    private static void HtmlIsSafe()
    {
        string html = Encoding.UTF8.GetString(Output(NoticeOutputNames.Html).Content);
        Assert(html.Contains("Alpha &amp; &lt;Co&gt;", StringComparison.Ordinal), "name not escaped");
        Assert(html.Contains("Line &lt;two&gt; &amp; &quot;quoted&quot;", StringComparison.Ordinal), "evidence not escaped");
        Assert(!html.Contains("<script", StringComparison.OrdinalIgnoreCase), "script element emitted");
        Assert(!html.Contains("<link", StringComparison.OrdinalIgnoreCase), "external stylesheet link emitted");
        Assert(!html.Contains(" src=", StringComparison.OrdinalIgnoreCase), "remote source attribute emitted");
        Assert(!html.Contains("url(", StringComparison.OrdinalIgnoreCase), "CSS remote URL hook emitted");
    }

    private static void HostileDigestIsEscaped()
    {
        NoticeAsset hostile = new(NoticeAssetKind.License, "\" onfocus=\"alert(1)", "text/plain", "safe", "origin", false);
        DependencyNotice dependency = SampleDocument().Dependencies[0] with { Assets = new[] { hostile } };
        string html = Encoding.UTF8.GetString(StandaloneHtmlNoticeRenderer.Render(SampleDocument() with { Dependencies = new[] { dependency } }));
        Assert(!html.Contains("\" onfocus=\"", StringComparison.Ordinal), "attribute injection remained raw");
        Assert(html.Contains("&quot; onfocus=&quot;", StringComparison.Ordinal), "hostile digest was not encoded");
    }

    private static void HtmlIsSemantic()
    {
        string html = Encoding.UTF8.GetString(Output(NoticeOutputNames.Html).Content);
        Assert(html.Contains("<main id=\"content\">", StringComparison.Ordinal), "main missing");
        Assert(html.Contains("<article aria-labelledby=", StringComparison.Ordinal), "article label missing");
        Assert(html.Contains("<table><caption>Component details</caption>", StringComparison.Ordinal), "semantic table missing");
        Assert(html.Contains("@media print", StringComparison.Ordinal), "print CSS missing");
        Assert(html.Contains("class=\"skip\"", StringComparison.Ordinal), "skip link missing");
    }

    private static void RejectsSchemaV1()
    {
        NoticeRenderResult result = DependencyNoticeRenderer.Render(SampleDocument() with { SchemaVersion = 1 }, SampleOptions());
        AssertSingleDiagnostic(result, NoticeDiagnosticCodes.SchemaIncompatible);
    }

    private static void RejectsInvalidDigest()
    {
        NoticeAsset invalid = SampleDocument().Dependencies[0].Assets[0] with { Sha256 = "ABC" };
        DependencyNotice dependency = SampleDocument().Dependencies[0] with { Assets = new[] { invalid } };
        NoticeRenderResult result = DependencyNoticeRenderer.Render(SampleDocument() with { Dependencies = new[] { dependency } }, SampleOptions());
        AssertSingleDiagnostic(result, NoticeDiagnosticCodes.SchemaIncompatible);
    }

    private static void RejectsDigestMismatch()
    {
        NoticeAsset invalid = SampleDocument().Dependencies[0].Assets[0] with { Text = "tampered evidence\n" };
        DependencyNotice dependency = SampleDocument().Dependencies[0] with { Assets = new[] { invalid } };
        NoticeRenderResult result = DependencyNoticeRenderer.Render(SampleDocument() with { Dependencies = new[] { dependency } }, SampleOptions());
        AssertSingleDiagnostic(result, NoticeDiagnosticCodes.SchemaIncompatible);
        Assert(result.Diagnostics[0].Message.Contains("strict UTF-8", StringComparison.Ordinal), "digest verification context missing");
    }

    private static void RejectsConflictingDigestText()
    {
        DependencyNoticeDocument document = SampleDocument();
        NoticeAsset conflict = document.Dependencies[1].Assets[0] with { Text = "different" };
        DependencyNotice dependency = document.Dependencies[1] with { Assets = new[] { conflict } };
        NoticeRenderResult result = DependencyNoticeRenderer.Render(document with { Dependencies = new[] { document.Dependencies[0], dependency } }, SampleOptions());
        AssertSingleDiagnostic(result, NoticeDiagnosticCodes.SchemaIncompatible);
    }

    private static void RejectsInvalidUtf16()
    {
        DependencyNotice dependency = SampleDocument().Dependencies[0] with { Name = "invalid-\ud800" };
        NoticeRenderResult result = DependencyNoticeRenderer.Render(SampleDocument() with { Dependencies = new[] { dependency } }, SampleOptions());
        AssertSingleDiagnostic(result, NoticeDiagnosticCodes.SchemaIncompatible);
    }

    private static void RejectsDuplicateIdentity()
    {
        DependencyNotice dependency = SampleDocument().Dependencies[0];
        NoticeRenderResult result = DependencyNoticeRenderer.Render(SampleDocument() with { Dependencies = new[] { dependency, dependency } }, SampleOptions());
        AssertSingleDiagnostic(result, NoticeDiagnosticCodes.SchemaIncompatible);
    }

    private static void RejectsNoncanonicalPackageUrl()
    {
        DependencyNotice dependency = SampleDocument().Dependencies[0] with { PackageUrl = "PKG:nuget/Zeta@10.0.0" };
        NoticeRenderResult result = DependencyNoticeRenderer.Render(SampleDocument() with { Dependencies = new[] { dependency } }, SampleOptions());
        AssertSingleDiagnostic(result, NoticeDiagnosticCodes.SchemaIncompatible);
    }

    private static void RejectsMissingRequiredString()
    {
        NoticeRenderResult result = DependencyNoticeRenderer.Render(SampleDocument() with { ArtifactName = " \t" }, SampleOptions());
        AssertSingleDiagnostic(result, NoticeDiagnosticCodes.SchemaIncompatible);
    }

    private static void RejectsInvalidSpdx()
    {
        DependencyNotice dependency = SampleDocument().Dependencies[0] with { EffectiveLicenseExpression = "MIT AND" };
        NoticeRenderResult result = DependencyNoticeRenderer.Render(SampleDocument() with { Dependencies = new[] { dependency } }, SampleOptions());
        AssertSingleDiagnostic(result, NoticeDiagnosticCodes.SchemaIncompatible);
    }

    private static void RejectsInvalidEnum()
    {
        DependencyNotice dependency = SampleDocument().Dependencies[0] with { Scope = (DependencyScope)999 };
        NoticeRenderResult result = DependencyNoticeRenderer.Render(SampleDocument() with { Dependencies = new[] { dependency } }, SampleOptions());
        Assert(!result.Succeeded && result.Outputs.Count == 0, "invalid enum emitted outputs");
        Assert(result.Diagnostics.All(static diagnostic => diagnostic.Code == NoticeDiagnosticCodes.SchemaIncompatible), "invalid enum diagnostic code");
    }

    private static void RejectsInvalidSbom()
    {
        NoticeRenderResult result = DependencyNoticeRenderer.Render(
            SampleDocument() with { Sbom = new SbomLink("CycloneDX", "bom.json", null) },
            SampleOptions());
        AssertSingleDiagnostic(result, NoticeDiagnosticCodes.SchemaIncompatible);
    }

    private static void RejectsHostPath()
    {
        NoticeRenderOptions options = SampleOptions() with { SelectedRoots = new[] { "C:\\secret\\project" } };
        NoticeRenderResult result = DependencyNoticeRenderer.Render(SampleDocument(), options);
        AssertSingleDiagnostic(result, NoticeDiagnosticCodes.UnsafeOutputDestination);
        Assert(!result.Diagnostics[0].Message.Contains("secret", StringComparison.Ordinal), "host path leaked into message");
        Assert(result.Diagnostics[0].Source is null, "host path leaked into source");
    }

    private static void ManifestIsOrdered()
    {
        using JsonDocument json = JsonDocument.Parse(Output(NoticeOutputNames.Manifest).Content);
        JsonElement inputs = json.RootElement.GetProperty("inputs");
        Assert(inputs[0].GetProperty("name").GetString() == "dependency-notices.lock.json", "inputs not sorted");
        JsonElement outputs = json.RootElement.GetProperty("outputs");
        Assert(outputs[0].GetProperty("name").GetString() == NoticeOutputNames.Text, "outputs not sorted ordinally");
    }

    private static void ManifestHasNoAmbientState()
    {
        string manifest = Encoding.UTF8.GetString(Output(NoticeOutputNames.Manifest).Content);
        Assert(!manifest.Contains("timestamp", StringComparison.OrdinalIgnoreCase), "timestamp present");
        Assert(!manifest.Contains("C:\\", StringComparison.Ordinal), "host path present");
        Assert(!manifest.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase), "machine name present");
    }

    private static void VerifierAcceptsExact()
    {
        NoticeRenderResult rendered = RenderSample();
        Dictionary<string, ReadOnlyMemory<byte>> expected = rendered.Outputs.ToDictionary(
            static output => output.FileName,
            static output => new ReadOnlyMemory<byte>(output.Content),
            StringComparer.Ordinal);
        Assert(NoticeOutputVerifier.Verify(expected, rendered.Outputs).Succeeded, "exact verification failed");
    }

    private static void VerifierReportsDrift()
    {
        NoticeRenderResult rendered = RenderSample();
        Dictionary<string, ReadOnlyMemory<byte>> expected = rendered.Outputs.ToDictionary(
            static output => output.FileName,
            static output => new ReadOnlyMemory<byte>(output.Content),
            StringComparer.Ordinal);
        expected[NoticeOutputNames.Text] = Encoding.UTF8.GetBytes("changed\n");
        NoticeVerificationResult result = NoticeOutputVerifier.Verify(expected, rendered.Outputs);
        Assert(result.Diagnostics.Any(static diagnostic => diagnostic.Code == NoticeDiagnosticCodes.OutputDrift), "drift code missing");
        Assert(result.Diagnostics[0].Message.Contains("expected sha256:", StringComparison.Ordinal), "digest context missing");
    }

    private static void WriterWritesExactBytes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "wut-notice-rendering-" + Guid.NewGuid().ToString("N"));
        try
        {
            NoticeRenderResult rendered = RenderSample();
            Assert(NoticeOutputWriter.Write(directory, rendered.Outputs).Count == 0, "write failed");
            foreach (RenderedNoticeOutput output in rendered.Outputs)
            {
                Assert(File.ReadAllBytes(Path.Combine(directory, output.FileName)).SequenceEqual(output.Content), "written bytes changed");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static void WriterRejectsTraversal()
    {
        string directory = Path.Combine(Path.GetTempPath(), "wut-notice-rendering-" + Guid.NewGuid().ToString("N"));
        try
        {
            RenderedNoticeOutput hostile = new("../escape.txt", Encoding.UTF8.GetBytes("x"), SharedDigest);
            IReadOnlyList<NoticeDiagnostic> diagnostics = NoticeOutputWriter.Write(directory, new[] { hostile });
            Assert(diagnostics.Count == 1 && diagnostics[0].Code == NoticeDiagnosticCodes.UnsafeOutputDestination, "unsafe destination accepted");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static void WriterRejectsDuplicates()
    {
        string directory = Path.Combine(Path.GetTempPath(), "wut-notice-rendering-" + Guid.NewGuid().ToString("N"));
        try
        {
            RenderedNoticeOutput first = new("duplicate.txt", Encoding.UTF8.GetBytes("one"), SharedDigest);
            RenderedNoticeOutput second = new("duplicate.txt", Encoding.UTF8.GetBytes("two"), SharedDigest);
            IReadOnlyList<NoticeDiagnostic> diagnostics = NoticeOutputWriter.Write(directory, new[] { first, second });
            Assert(diagnostics.Count == 1 && diagnostics[0].Code == NoticeDiagnosticCodes.UnsafeOutputDestination, "duplicate destination accepted");
            Assert(!File.Exists(Path.Combine(directory, "duplicate.txt")), "writer performed a partial write");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static void WriterRejectsLinkedAncestor()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "wut-notice-rendering-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(sandbox, "target");
        string link = Path.Combine(sandbox, "linked-root");
        try
        {
            Directory.CreateDirectory(target);
            try
            {
                _ = Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                return;
            }

            IReadOnlyList<NoticeDiagnostic> diagnostics = NoticeOutputWriter.Write(Path.Combine(link, "nested"), RenderSample().Outputs);
            Assert(diagnostics.Count == 1 && diagnostics[0].Code == NoticeDiagnosticCodes.UnsafeOutputDestination, "linked ancestor accepted");
            Assert(!Directory.Exists(Path.Combine(target, "nested")), "writer traversed linked ancestor before rejection");
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, true);
            }
        }
    }

    private static void WriterRejectsLinkedDestination()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "wut-notice-rendering-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(sandbox, "target.txt");
        string destination = Path.Combine(sandbox, NoticeOutputNames.Json);
        try
        {
            Directory.CreateDirectory(sandbox);
            File.WriteAllText(target, "outside");
            try
            {
                _ = File.CreateSymbolicLink(destination, target);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                return;
            }

            RenderedNoticeOutput output = RenderSample().Outputs.Single(static item => item.FileName == NoticeOutputNames.Json);
            IReadOnlyList<NoticeDiagnostic> diagnostics = NoticeOutputWriter.Write(sandbox, new[] { output });
            Assert(diagnostics.Count == 1 && diagnostics[0].Code == NoticeDiagnosticCodes.UnsafeOutputDestination, "linked destination accepted");
            Assert(File.ReadAllText(target) == "outside", "linked target was overwritten");
        }
        finally
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, true);
            }
        }
    }

    private static void AssertGolden(string outputName)
    {
        byte[] expected = File.ReadAllBytes(Path.Combine(GoldenRoot, outputName));
        Assert(expected.SequenceEqual(Output(outputName).Content), outputName + " golden mismatch");
    }

    private static RenderedNoticeOutput Output(string name) =>
        RenderSample().Outputs.Single(output => StringComparer.Ordinal.Equals(output.FileName, name));

    private static NoticeRenderResult RenderSample()
    {
        NoticeRenderResult result = DependencyNoticeRenderer.Render(SampleDocument(), SampleOptions(reverse: true));
        Assert(result.Succeeded, "sample render failed");
        return result;
    }

    private static NoticeRenderOptions SampleOptions(bool reverse = false)
    {
        NoticeManifestInput lockInput = new("dependency-notices.lock.json", Digest("lock\n"));
        NoticeManifestInput policyInput = new("policy/dependency-notices.policy.json", Digest("policy\n"));
        return new NoticeRenderOptions(
            "2.0.0-test",
            reverse ? new[] { policyInput, lockInput } : new[] { lockInput, policyInput },
            Digest("evidence-lock\n"),
            reverse ? ReverseRoots : ForwardRoots,
            reverse ? ReverseProfiles : ForwardProfiles);
    }

    private static DependencyNoticeDocument SampleDocument()
    {
        NoticeAsset shared = new(NoticeAssetKind.License, SharedDigest, "text/plain; charset=utf-8", SharedEvidence, "package/LICENSE", false);
        DependencyNotice zeta = new(
            "pkg:nuget/Zeta@10.0.0",
            "Zeta",
            "10.0.0",
            DependencyEcosystem.NuGet,
            DependencyScope.Runtime,
            false,
            "Apache-2.0",
            "Apache-2.0",
            null,
            new[] { shared },
            new[] { new NoticePolicyDecision("Apache-2.0", LicensePolicyOutcome.Allow, "allow-apache") },
            "component-zeta",
            false,
            null);
        DependencyNotice alpha = new(
            "pkg:npm/%40scope/alpha@2.0.0?x=%22%3E%3C",
            "Alpha & <Co>",
            "2.0.0",
            DependencyEcosystem.Npm,
            DependencyScope.Runtime,
            true,
            "MIT OR LicenseRef-Internal",
            "MIT",
            "MIT",
            new[] { shared },
            new[] { new NoticePolicyDecision("MIT", LicensePolicyOutcome.Allow, "allow-mit") },
            "component-alpha",
            true,
            "Patched <locally> & reviewed.");
        NoticeDiagnostic diagnostic = new(
            NoticeDiagnosticCodes.LicenseReviewRequired,
            NoticeDiagnosticSeverity.Warning,
            "Review owner: <legal> & security.",
            zeta.PackageUrl,
            "policy/review.json",
            4,
            "Record review.");
        return new DependencyNoticeDocument(
            2,
            "Demo <Suite>",
            "2.0.0",
            new[] { zeta, alpha },
            new SbomLink("cycloneDx", "bom.json", "urn:uuid:test"),
            new[] { diagnostic });
    }

    private static void AssertBundlesEqual(NoticeRenderResult first, NoticeRenderResult second)
    {
        Assert(first.Succeeded && second.Succeeded, "render failed");
        Assert(first.Outputs.Count == second.Outputs.Count, "output count differs");
        for (int index = 0; index < first.Outputs.Count; index++)
        {
            Assert(first.Outputs[index].FileName == second.Outputs[index].FileName, "output name differs");
            Assert(first.Outputs[index].Content.SequenceEqual(second.Outputs[index].Content), first.Outputs[index].FileName + " differs");
        }
    }

    private static void AssertSingleDiagnostic(NoticeRenderResult result, string code)
    {
        Assert(!result.Succeeded, "result unexpectedly succeeded");
        Assert(result.Outputs.Count == 0, "partial outputs returned");
        Assert(result.Diagnostics.Count == 1, "unexpected diagnostic count");
        Assert(result.Diagnostics[0].Code == code, "unexpected diagnostic code");
    }

    private static int Count(string value, string needle)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception exception)
        {
            failed++;
            Console.Error.WriteLine("FAIL " + name + ": " + exception.Message);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private const string SharedEvidence = "Alpha license\nLine <two> & \"quoted\"\n";
    private static readonly string SharedDigest = Digest(SharedEvidence);
    private static readonly string[] ForwardRoots = ["src/alpha", "src/zeta"];
    private static readonly string[] ReverseRoots = ["src/zeta", "src/alpha"];
    private static readonly string[] ForwardProfiles = ["distribution", "runtime"];
    private static readonly string[] ReverseProfiles = ["runtime", "distribution"];
}
