namespace WebUIToolkit.DependencyNotices.Runtime.Tests;

internal static class TestDocuments
{
    public const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public const string Asset = """
        {"kind":"license","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","mediaType":"text/plain","text":"license bytes","origin":"cache/asset","isOverride":false}
        """;

    public const string NpmDependency = """
        {"packageUrl":"pkg:npm/zeta@2.0.0","name":"zeta","version":"2.0.0","ecosystem":"npm","scope":"development","isDirect":false,"observedLicenseExpression":"MIT","effectiveLicenseExpression":"MIT","selectedLicenseExpression":null,"assets":[],"decisions":[],"sbomComponentReference":"npm-zeta","isModified":false,"modificationNotice":null}
        """;

    public const string NuGetDependency = """
        {"packageUrl":"pkg:nuget/Alpha@1.0.0","name":"Alpha","version":"1.0.0","ecosystem":"nuget","scope":"runtime","isDirect":true,"observedLicenseExpression":"Apache-2.0","effectiveLicenseExpression":"Apache-2.0","selectedLicenseExpression":null,"assets":[{"kind":"license","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","mediaType":"text/plain","text":"license bytes","origin":"cache/asset","isOverride":false}],"decisions":[{"subject":"Apache-2.0","outcome":"allow","rule":"approved"}],"sbomComponentReference":"nuget-alpha","isModified":true,"modificationNotice":"patched"}
        """;

    public const string Diagnostic = """
        {"code":"WUTNOTICE6001","severity":"warning","message":"review","packageUrl":null,"source":null,"offset":0,"remediation":null}
        """;

    public const string V2 = """
        {"schemaVersion":2,"artifactName":"app","artifactVersion":"1.0","dependencies":[{"packageUrl":"pkg:npm/zeta@2.0.0","name":"zeta","version":"2.0.0","ecosystem":"npm","scope":"development","isDirect":false,"observedLicenseExpression":"MIT","effectiveLicenseExpression":"MIT","selectedLicenseExpression":null,"assets":[],"decisions":[],"sbomComponentReference":"npm-zeta","isModified":false,"modificationNotice":null},{"packageUrl":"pkg:nuget/Alpha@1.0.0","name":"Alpha","version":"1.0.0","ecosystem":"nuget","scope":"runtime","isDirect":true,"observedLicenseExpression":"Apache-2.0","effectiveLicenseExpression":"Apache-2.0","selectedLicenseExpression":null,"assets":[{"kind":"license","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","mediaType":"text/plain","text":"license bytes","origin":"cache/asset","isOverride":false}],"decisions":[{"subject":"Apache-2.0","outcome":"allow","rule":"approved"}],"sbomComponentReference":"nuget-alpha","isModified":true,"modificationNotice":"patched"}],"sbom":{"format":"cycloneDx","documentReference":"bom.json","serialNumber":null},"diagnostics":[{"code":"WUTNOTICE6001","severity":"warning","message":"review","packageUrl":null,"source":null,"offset":0,"remediation":null}]}
        """;

    public const string V1 = """
        {"schemaVersion":1,"artifactName":"legacy","dependencies":[{"packageUrl":"pkg:generic/legacy@1","name":"legacy","version":"1","ecosystem":"generic","scope":"unknown","isDirect":true,"observedLicenseExpression":"MIT","effectiveLicenseExpression":"MIT","assets":[],"decisions":[],"isModified":false}],"diagnostics":[]}
        """;

    public static string V2With(string dependencies, string diagnostics = "[]") =>
        $$"""{"schemaVersion":2,"artifactName":"app","artifactVersion":null,"dependencies":[{{dependencies}}],"sbom":null,"diagnostics":{{diagnostics}}}""";
}
