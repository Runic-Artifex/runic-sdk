using System;
using System.IO;
using System.Linq;
using System.Text;

namespace WebUIToolkit.DependencyNotices.Runtime.Tests;

internal static class Program
{
    public static int Main()
    {
        TestHarness tests = new();
        RegisterLoadingTests(tests);
        RegisterValidationTests(tests);
        RegisterCatalogTests(tests);
        return tests.Run();
    }

    private static void RegisterLoadingTests(TestHarness tests)
    {
        tests.Add("loads schema v2 from bytes", () =>
        {
            NoticeDocument document = Load(TestDocuments.V2);
            Assert.Equal(2, document.SchemaVersion);
            Assert.Equal("app", document.ArtifactName);
            Assert.Equal("cycloneDx", document.Sbom!.Format);
            Assert.Equal("Alpha", document.Dependencies[0].Name);
            Assert.Equal("license bytes", document.Dependencies[0].Assets[0].Text);
        });

        tests.Add("loads compatible schema v1", () =>
        {
            NoticeDocument document = Load(TestDocuments.V1);
            Assert.Equal(1, document.SchemaVersion);
            Assert.Equal("legacy", document.ArtifactName);
            Assert.Equal(null, document.Sbom);
        });

        tests.Add("loads from stream without closing it", () =>
        {
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(TestDocuments.V2));
            NoticeDocument document = NoticeDocumentLoader.Load(stream);
            Assert.Equal("app", document.ArtifactName);
            Assert.True(stream.CanRead);
        });

        tests.Add("loads from path", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"wut-notices-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path, TestDocuments.V2, new UTF8Encoding(false));
                Assert.Equal("app", NoticeDocumentLoader.Load(path).ArtifactName);
            }
            finally
            {
                File.Delete(path);
            }
        });

        tests.Add("runtime references no toolkit engine or adapter", () =>
        {
            string[] references = typeof(NoticeDocumentLoader).Assembly.GetReferencedAssemblies().Select(static value => value.Name ?? string.Empty).ToArray();
            Assert.True(!references.Any(static name => name.StartsWith("WebUIToolkit.DependencyNotices.", StringComparison.Ordinal)));
        });
    }

    private static void RegisterValidationTests(TestHarness tests)
    {
        tests.Add("rejects future schema", () => Reject(TestDocuments.V2.Replace("\"schemaVersion\":2", "\"schemaVersion\":3", StringComparison.Ordinal)));
        tests.Add("rejects obsolete schema", () => Reject(TestDocuments.V1.Replace("\"schemaVersion\":1", "\"schemaVersion\":0", StringComparison.Ordinal)));
        tests.Add("rejects duplicate root property", () => Reject(TestDocuments.V2.Replace("\"artifactName\":\"app\"", "\"artifactName\":\"first\",\"artifactName\":\"app\"", StringComparison.Ordinal)));
        tests.Add("rejects duplicate nested property", () => Reject(TestDocuments.V2.Replace("\"name\":\"Alpha\"", "\"name\":\"first\",\"name\":\"Alpha\"", StringComparison.Ordinal)));
        tests.Add("rejects unmapped property", () => Reject(TestDocuments.V2.Replace("\"artifactVersion\":\"1.0\"", "\"artifactVersion\":\"1.0\",\"unexpected\":true", StringComparison.Ordinal)));
        tests.Add("rejects lone high surrogate in property name with domain exception", () =>
        {
            string json = TestDocuments.V2.Replace("\"artifactName\"", "\"artifact\\uD800Name\"", StringComparison.Ordinal);
            AssertSurrogateRejection(json);
        });
        tests.Add("rejects lone high surrogate in string value with domain exception", () =>
        {
            string json = TestDocuments.V2.Replace("\"artifactName\":\"app\"", "\"artifactName\":\"\\uD800\"", StringComparison.Ordinal);
            AssertSurrogateRejection(json);
        });
        tests.Add("rejects lone low surrogate in string value with domain exception", () =>
        {
            string json = TestDocuments.V2.Replace("\"artifactName\":\"app\"", "\"artifactName\":\"\\uDC00\"", StringComparison.Ordinal);
            AssertSurrogateRejection(json);
        });
        tests.Add("rejects missing v2 nullable property", () => Reject(TestDocuments.V2.Replace(",\"sbomComponentReference\":\"npm-zeta\"", string.Empty, StringComparison.Ordinal)));
        tests.Add("rejects invalid ecosystem", () => Reject(TestDocuments.V2.Replace("\"ecosystem\":\"npm\"", "\"ecosystem\":\"cargo\"", StringComparison.Ordinal)));
        tests.Add("rejects invalid digest", () => Reject(TestDocuments.V2.Replace(TestDocuments.Hash, TestDocuments.Hash.ToUpperInvariant(), StringComparison.Ordinal)));
        tests.Add("rejects invalid diagnostic code", () => Reject(TestDocuments.V2.Replace("WUTNOTICE6001", "NOTICE6001", StringComparison.Ordinal)));

        tests.Add("enforces document byte limit", () =>
        {
            NoticeLoadOptions options = new() { MaxDocumentBytes = 10 };
            Assert.Throws<NoticeDocumentException>(() => Load(TestDocuments.V2, options));
        });

        tests.Add("enforces stream byte limit", () =>
        {
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(TestDocuments.V2));
            NoticeLoadOptions options = new() { MaxDocumentBytes = 10 };
            Assert.Throws<NoticeDocumentException>(() => NoticeDocumentLoader.Load(stream, options));
        });

        tests.Add("enforces string byte limit", () =>
        {
            NoticeLoadOptions options = new() { MaxStringBytes = 5 };
            Assert.Throws<NoticeDocumentException>(() => Load(TestDocuments.V2, options));
        });

        tests.Add("enforces dependency limit", () =>
        {
            NoticeLoadOptions options = new() { MaxDependencies = 1 };
            Assert.Throws<NoticeDocumentException>(() => Load(TestDocuments.V2, options));
        });

        tests.Add("enforces asset limit", () =>
        {
            string dependency = TestDocuments.NuGetDependency.Replace("] ,", "],", StringComparison.Ordinal);
            dependency = dependency.Replace($"\"assets\":[{TestDocuments.Asset}]", $"\"assets\":[{TestDocuments.Asset},{TestDocuments.Asset}]", StringComparison.Ordinal);
            NoticeLoadOptions options = new() { MaxAssetsPerDependency = 1 };
            Assert.Throws<NoticeDocumentException>(() => Load(TestDocuments.V2With(dependency), options));
        });

        tests.Add("enforces diagnostic limit", () =>
        {
            NoticeLoadOptions options = new() { MaxDiagnostics = 1 };
            string json = TestDocuments.V2With(TestDocuments.NpmDependency, $"[{TestDocuments.Diagnostic},{TestDocuments.Diagnostic}]");
            Assert.Throws<NoticeDocumentException>(() => Load(json, options));
        });

        tests.Add("rejects non-positive limits", () =>
        {
            NoticeLoadOptions options = new() { MaxDepth = 0 };
            Assert.Throws<ArgumentOutOfRangeException>(() => Load(TestDocuments.V2, options));
        });
    }

    private static void RegisterCatalogTests(TestHarness tests)
    {
        tests.Add("orders dependencies deterministically", () =>
        {
            NoticeDocument document = Load(TestDocuments.V2);
            Assert.Equal("Alpha", document.Dependencies[0].Name);
            Assert.Equal("zeta", document.Dependencies[1].Name);
        });

        tests.Add("search is ordinal case-insensitive and ordered", () =>
        {
            NoticeCatalog catalog = new(Load(TestDocuments.V2));
            Assert.Equal("Alpha", catalog.Search("ALPHA")[0].Name);
            Assert.Equal(1, catalog.Search("MIT").Count);
        });

        tests.Add("filter combines predicates", () =>
        {
            NoticeCatalog catalog = new(Load(TestDocuments.V2));
            var results = catalog.Filter(new NoticeFilter(NoticeEcosystem.NuGet, NoticeDependencyScope.Runtime, true, true));
            Assert.Equal(1, results.Count);
            Assert.Equal("Alpha", results[0].Name);
        });

        tests.Add("groups use stable ordinal keys and members", () =>
        {
            NoticeCatalog catalog = new(Load(TestDocuments.V2));
            var groups = catalog.Group(NoticeGroupBy.Ecosystem);
            Assert.Equal("npm", groups[0].Key);
            Assert.Equal("nuget", groups[1].Key);
            Assert.Equal("zeta", groups[0].Dependencies[0].Name);
        });

        tests.Add("returned collections are read-only", () =>
        {
            NoticeDocument document = Load(TestDocuments.V2);
            Assert.Throws<NotSupportedException>(() => ((System.Collections.Generic.IList<NoticeDependency>)document.Dependencies).Add(document.Dependencies[0]));
        });
    }

    private static NoticeDocument Load(string json, NoticeLoadOptions? options = null) =>
        NoticeDocumentLoader.Load(Encoding.UTF8.GetBytes(json).AsSpan(), options);

    private static void Reject(string json) => Assert.Throws<NoticeDocumentException>(() => Load(json));

    private static void AssertSurrogateRejection(string json)
    {
        NoticeDocumentException exception = Assert.Throws<NoticeDocumentException>(() => Load(json));
        Assert.Equal("JSON string contains an unpaired Unicode surrogate escape.", exception.Message);
    }
}
