using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Evidence;

namespace WebUIToolkit.DependencyNotices.Tests;

internal static class CoreTests
{
    public static void Register(TestHarness tests)
    {
        tests.Add("evidence digest hashes exact bytes as lowercase SHA-256", EvidenceDigestIsExact);
        tests.Add("evidence digest validation rejects noncanonical hex", EvidenceDigestValidation);
        tests.Add("component ordering is name version then canonical PURL", ComponentOrdering);
        tests.Add("diagnostic catalog uses unique reserved identities", DiagnosticCatalog);
        tests.Add("contract versions preserve Wave A and expose additive document v2", ContractVersions);
    }

    private static void EvidenceDigestIsExact()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("abc\r\n");
        string digest = EvidenceDigest.ComputeSha256(bytes);
        Assert.Equal("552bab6864c7a7b69a502ed1854b9245c0e1a30f008aaa0b281da62585fdb025", digest);

        using MemoryStream stream = new(bytes, writable: false);
        Assert.Equal(digest, EvidenceDigest.ComputeSha256(stream));
    }

    private static void EvidenceDigestValidation()
    {
        Assert.True(EvidenceDigest.IsCanonicalSha256(new string('a', 64)));
        Assert.True(!EvidenceDigest.IsCanonicalSha256(new string('A', 64)));
        Assert.True(!EvidenceDigest.IsCanonicalSha256("abc"));
    }

    private static void ComponentOrdering()
    {
        List<ManualDependencyComponent> components =
        [
            Component("Zulu", "2.0.0", "pkg:generic/zulu@2.0.0"),
            Component("Alpha", "2.0.0", "pkg:generic/alpha@2.0.0?variant=b"),
            Component("Alpha", "1.0.0", "pkg:generic/alpha@1.0.0"),
            Component("Alpha", "2.0.0", "pkg:generic/alpha@2.0.0?variant=a"),
        ];
        components.Sort(DependencyComponentComparer.Instance);
        Assert.Equal("pkg:generic/alpha@1.0.0", components[0].PackageUrl.CanonicalValue);
        Assert.Equal("pkg:generic/alpha@2.0.0?variant=a", components[1].PackageUrl.CanonicalValue);
        Assert.Equal("pkg:generic/alpha@2.0.0?variant=b", components[2].PackageUrl.CanonicalValue);
        Assert.Equal("pkg:generic/zulu@2.0.0", components[3].PackageUrl.CanonicalValue);
    }

    private static void DiagnosticCatalog()
    {
        HashSet<string> unique = new(StringComparer.Ordinal);
        foreach (string code in NoticeDiagnosticCodes.All)
        {
            Assert.True(code.StartsWith("WUTNOTICE", StringComparison.Ordinal));
            Assert.Equal(13, code.Length);
            Assert.True(unique.Add(code), $"Duplicate diagnostic code {code}.");
        }
    }

    private static void ContractVersions()
    {
        Assert.Equal(1, NoticeContractVersions.Configuration);
        Assert.Equal(1, NoticeContractVersions.Policy);
        Assert.Equal(1, NoticeContractVersions.EvidenceLock);
        Assert.Equal(1, NoticeContractVersions.DiagnosticDocument);
        Assert.Equal(1, NoticeContractVersions.NoticeDocumentV1);
        Assert.Equal(2, NoticeContractVersions.NoticeDocument);
    }

    private static ManualDependencyComponent Component(string name, string version, string purl) =>
        new(PackageUrl.Parse(purl), name, version, null, "MIT", Array.Empty<NoticeEvidence>(), false, null);
}
