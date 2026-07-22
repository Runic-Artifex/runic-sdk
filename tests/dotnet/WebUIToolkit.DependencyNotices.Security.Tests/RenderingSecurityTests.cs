using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using WebUIToolkit.DependencyNotices.Policy;
using WebUIToolkit.DependencyNotices.Rendering;

namespace WebUIToolkit.DependencyNotices.Security.Tests;

internal static class RenderingSecurityTests
{
    public static void Register(TestHarness tests)
    {
        tests.Add("HTML renderer escapes hostile metadata and evidence", EscapesHostileText);
        tests.Add("HTML renderer cannot create dangerous URL attributes", RejectsDangerousUrlContexts);
        tests.Add("HTML renderer emits a standalone script-free document", EmitsStandaloneScriptFreeDocument);
    }

    private static void EscapesHostileText()
    {
        using JsonDocument fixture = TestFiles.ReadFixture("html-url-injection.json");
        JsonElement root = fixture.RootElement;
        string marker = root.GetProperty("marker").GetString()!;
        DependencyNoticeDocument document = CreateDocument(
            root.GetProperty("name").GetString()!,
            root.GetProperty("packageUrl").GetString()!,
            root.GetProperty("evidenceText").GetString()!,
            root.GetProperty("digest").GetString()!);

        string html = Encoding.UTF8.GetString(StandaloneHtmlNoticeRenderer.Render(document));
        Assert.DoesNotContain(marker, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("onmouseover=\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&quot;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectsDangerousUrlContexts()
    {
        using JsonDocument fixture = TestFiles.ReadFixture("html-url-injection.json");
        JsonElement root = fixture.RootElement;
        string html = Encoding.UTF8.GetString(StandaloneHtmlNoticeRenderer.Render(CreateDocument(
            "dangerous URL",
            root.GetProperty("dangerousUrl").GetString()!,
            "evidence",
            new string('a', 64))));

        Assert.DoesNotContain("href=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"data:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"data:", html, StringComparison.OrdinalIgnoreCase);
    }

    private static void EmitsStandaloneScriptFreeDocument()
    {
        string html = Encoding.UTF8.GetString(StandaloneHtmlNoticeRenderer.Render(CreateDocument(
            "artifact",
            "pkg:generic/example@1.0.0",
            "license",
            new string('b', 64))));

        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("default-src 'none'", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
    }

    private static DependencyNoticeDocument CreateDocument(string name, string packageUrl, string evidenceText, string digest)
    {
        NoticeAsset asset = new(NoticeAssetKind.License, digest, "text/plain", evidenceText, "fixture://hostile?<secret>", false);
        DependencyNotice dependency = new(
            packageUrl,
            name,
            "1.0.0<script>",
            DependencyEcosystem.Generic,
            DependencyScope.Runtime,
            true,
            "MIT<script>",
            "MIT<script>",
            null,
            new[] { asset },
            new[] { new NoticePolicyDecision("MIT", LicensePolicyOutcome.Allow, "fixture") },
            null,
            true,
            "modified <img src=x onerror=alert(1)>");
        return new DependencyNoticeDocument(1, "artifact<script>", null, new[] { dependency }, null, Array.Empty<WebUIToolkit.DependencyNotices.Diagnostics.NoticeDiagnostic>());
    }
}
