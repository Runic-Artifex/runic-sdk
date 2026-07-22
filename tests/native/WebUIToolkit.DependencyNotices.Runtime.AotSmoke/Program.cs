using System;
using System.Text;
using WebUIToolkit.DependencyNotices.Runtime;

const string Json = """
    {"schemaVersion":2,"artifactName":"aot-smoke","artifactVersion":null,"dependencies":[{"packageUrl":"pkg:nuget/Example@1.0.0","name":"Example","version":"1.0.0","ecosystem":"nuget","scope":"runtime","isDirect":true,"observedLicenseExpression":"MIT","effectiveLicenseExpression":"MIT","selectedLicenseExpression":null,"assets":[{"kind":"license","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","mediaType":"text/plain","text":"MIT","origin":"embedded","isOverride":false}],"decisions":[],"sbomComponentReference":null,"isModified":false,"modificationNotice":null}],"sbom":null,"diagnostics":[]}
    """;

NoticeDocument document = NoticeDocumentLoader.Load(Encoding.UTF8.GetBytes(Json).AsSpan());
NoticeCatalog catalog = new(document);
if (document.SchemaVersion != 2
    || document.ArtifactName != "aot-smoke"
    || catalog.Search("example").Count != 1
    || catalog.Filter(new NoticeFilter(NoticeEcosystem.NuGet, NoticeDependencyScope.Runtime, true)).Count != 1
    || catalog.Group(NoticeGroupBy.EffectiveLicenseExpression)[0].Key != "MIT")
{
    Console.Error.WriteLine("Native AOT runtime smoke failed.");
    return 1;
}

Console.WriteLine("PASS dependency-notices-runtime-native-aot");
return 0;
