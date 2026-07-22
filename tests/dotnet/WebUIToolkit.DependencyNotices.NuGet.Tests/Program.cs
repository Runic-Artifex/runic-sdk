using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.NuGet;

namespace WebUIToolkit.DependencyNotices.NuGet.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        TestHarness tests = new();
        Register(tests);
        return await tests.RunAsync().ConfigureAwait(false);
    }

    private static void Register(TestHarness tests)
    {
        tests.Add("valid graph is deterministic and complete", ValidGraph);
        tests.Add("canonical PURLs and exact versions are emitted", CanonicalIdentities);
        tests.Add("direct and transitive relationships are preserved", Relationships);
        tests.Add("development-only packages are classified", DevelopmentScope);
        tests.Add("package integrity and evidence digests are retained", IntegrityAndEvidence);
        tests.Add("missing explicit target fails closed", MissingTarget);
        tests.Add("case-ambiguous target fails closed", AmbiguousTarget);
        tests.Add("runtime target selection is explicit", RuntimeTarget);
        tests.Add("lock and assets version drift is diagnosed", Drift);
        tests.Add("lock and assets hash drift is diagnosed", HashDrift);
        tests.Add("unresolved graph edge is diagnosed", UnresolvedEdge);
        tests.Add("unsupported JSON shape is diagnosed", UnsupportedFormat);
        tests.Add("floating resolved versions are rejected", FloatingVersion);
        tests.Add("unsafe restored package path is rejected", UnsafePackagePath);
        tests.Add("license URL is not treated as local evidence", UrlOnlyEvidence);
        tests.Add("multiple local roots fail deterministic selection", MultiplePackageRoots);
        tests.Add("multiple local license candidates are diagnosed", MultipleLicenseCandidates);
        tests.Add("DTD package manifests are rejected", DtdIsRejected);
        tests.Add("invalid UTF-8 license evidence is rejected", InvalidLicenseEncoding);
        tests.Add("output ordering is ordinal and repeatable", StableOrdering);
        tests.Add("diagnostics do not disclose package-root host paths", DiagnosticsAreSanitized);
        tests.Add("duplicate JSON properties are rejected", DuplicateJsonProperty);
        tests.Add("malformed escaped Unicode is rejected", MalformedUnicode);
        tests.Add("JSON property budget is enforced", JsonPropertyBudget);
        tests.Add("JSON value budget is enforced", JsonValueBudget);
        tests.Add("oversized lock input is rejected before allocation", OversizedLock);
        tests.Add("oversized assets input is rejected before allocation", OversizedAssets);
        tests.Add("oversized license evidence is rejected", OversizedLicense);
        tests.Add("oversized package manifest is rejected", OversizedNuspec);
    }

    private static InventoryResult ScanValid() => NuGetInventoryAdapter.Scan(new NuGetInventoryOptions(
        Fixture("valid", "packages.lock.json"),
        Fixture("valid", "project.assets.json"),
        "net10.0",
        PackagesRoot: Fixture("valid", "packages")));

    private static void ValidGraph()
    {
        InventoryResult first = ScanValid();
        InventoryResult second = ScanValid();
        Assert.True(first.Succeeded, string.Join(Environment.NewLine, first.Diagnostics.Select(static value => value.Code)));
        Assert.Equal(3, first.Components.Count);
        Assert.Equal(0, first.Diagnostics.Count);
        Assert.SequenceEqual(
            first.Components.Select(static value => value.PackageUrl.CanonicalValue),
            second.Components.Select(static value => value.PackageUrl.CanonicalValue));
    }

    private static void CanonicalIdentities()
    {
        InventoryResult result = ScanValid();
        Assert.SequenceEqual(
            [
                "pkg:nuget/Example.Build@3.0.0",
                "pkg:nuget/Example.Direct@1.2.3",
                "pkg:nuget/Example.Transitive@2.0.0",
            ],
            result.Components.Select(static value => value.PackageUrl.CanonicalValue));
    }

    private static void Relationships()
    {
        InventoryResult result = ScanValid();
        Assert.True(result.Components.Single(static value => value.Name == "Example.Direct").IsDirect);
        Assert.True(!result.Components.Single(static value => value.Name == "Example.Transitive").IsDirect);
    }

    private static void DevelopmentScope()
    {
        InventoryComponent package = ScanValid().Components.Single(static value => value.Name == "Example.Build");
        Assert.Equal(DependencyScope.Development, package.Scope);
    }

    private static void IntegrityAndEvidence()
    {
        InventoryComponent package = ScanValid().Components.Single(static value => value.Name == "Example.Direct");
        Assert.Equal("sha512-ZGlyZWN0LWhhc2g=", package.Integrity);
        Assert.Equal("MIT", package.ObservedLicenseExpression);
        Assert.Equal(1, package.Evidence.Count);
        Assert.Equal(64, package.Evidence[0].Sha256.Length);
        Assert.True(!Path.IsPathRooted(package.Evidence[0].Path));
    }

    private static void MissingTarget()
    {
        InventoryResult result = NuGetInventoryAdapter.Scan(new NuGetInventoryOptions(
            Fixture("valid", "packages.lock.json"), Fixture("valid", "project.assets.json"), "net9.0",
            PackagesRoot: Fixture("valid", "packages")));
        Assert.HasCode(result, NoticeDiagnosticCodes.AmbiguousTarget);
    }

    private static void AmbiguousTarget()
    {
        InventoryResult result = NuGetInventoryAdapter.Scan(new NuGetInventoryOptions(
            Fixture("hostile", "ambiguous.lock.json"), Fixture("hostile", "ambiguous.assets.json"), "net10.0",
            PackagesRoot: Fixture("valid", "packages")));
        Assert.HasCode(result, NoticeDiagnosticCodes.AmbiguousTarget);
    }

    private static void RuntimeTarget()
    {
        using TemporaryInventory inventory = TemporaryInventory.Empty(
            "net10.0/win-x64", "net10.0/win-x64");
        InventoryResult selected = inventory.Scan("net10.0", "win-x64");
        InventoryResult missingPortable = inventory.Scan("net10.0");
        Assert.True(selected.Succeeded);
        Assert.HasCode(missingPortable, NoticeDiagnosticCodes.AmbiguousTarget);
    }

    private static void Drift()
    {
        InventoryResult result = NuGetInventoryAdapter.Scan(new NuGetInventoryOptions(
            Fixture("hostile", "drift.lock.json"), Fixture("hostile", "drift.assets.json"), "net10.0",
            PackagesRoot: Fixture("valid", "packages")));
        Assert.HasCode(result, NoticeDiagnosticCodes.LockFileDrift);
    }

    private static void HashDrift()
    {
        using TemporaryInventory inventory = TemporaryInventory.Single("Hash.Package", "1.0.0",
            contentHash: "bG9jaw==", assetsHash: "YXNzZXRz",
            nuspec: Nuspec("Hash.Package", "1.0.0", "<license type=\"file\">LICENSE</license>"),
            files: ["hash.package.nuspec", "LICENSE"]);
        inventory.WritePackageFile("hash.package/1.0.0/LICENSE", "license");
        InventoryResult result = inventory.Scan("net10.0");
        Assert.HasCode(result, NoticeDiagnosticCodes.LockFileDrift);
    }

    private static void UnresolvedEdge()
    {
        InventoryResult result = NuGetInventoryAdapter.Scan(new NuGetInventoryOptions(
            Fixture("hostile", "unresolved.lock.json"), Fixture("hostile", "unresolved.assets.json"), "net10.0",
            PackagesRoot: Fixture("valid", "packages")));
        Assert.HasCode(result, NoticeDiagnosticCodes.UnresolvedDependency);
    }

    private static void UnsupportedFormat()
    {
        InventoryResult result = NuGetInventoryAdapter.Scan(new NuGetInventoryOptions(
            Fixture("hostile", "malformed.lock.json"), Fixture("valid", "project.assets.json"), "net10.0",
            PackagesRoot: Fixture("valid", "packages")));
        Assert.HasCode(result, NoticeDiagnosticCodes.UnsupportedInventoryFormat);
    }

    private static void FloatingVersion()
    {
        using TemporaryInventory inventory = TemporaryInventory.Single("Floating.Package", "1.*",
            nuspec: Nuspec("Floating.Package", "1.0.0", string.Empty));
        Assert.HasCode(inventory.Scan("net10.0"), NoticeDiagnosticCodes.UnresolvedDependency);
    }

    private static void UnsafePackagePath()
    {
        InventoryResult result = NuGetInventoryAdapter.Scan(new NuGetInventoryOptions(
            Fixture("hostile", "unresolved.lock.json"), Fixture("hostile", "unresolved.assets.json"), "net10.0",
            PackagesRoot: Fixture("valid", "packages")));
        Assert.HasCode(result, NoticeDiagnosticCodes.InvalidDependencyGraph);
    }

    private static void UrlOnlyEvidence()
    {
        using TemporaryInventory inventory = TemporaryInventory.Single("Url.Package", "1.0.0",
            nuspec: Nuspec("Url.Package", "1.0.0", "<licenseUrl>https://example.invalid/license</licenseUrl>"));
        Assert.HasCode(inventory.Scan("net10.0"), NoticeDiagnosticCodes.UrlOnlyEvidence);
    }

    private static void MultiplePackageRoots()
    {
        using TemporaryInventory inventory = TemporaryInventory.Empty("net10.0", "net10.0", twoRoots: true);
        InventoryResult result = NuGetInventoryAdapter.Scan(new NuGetInventoryOptions(
            inventory.LockPath, inventory.AssetsPath, "net10.0"));
        Assert.HasCode(result, NoticeDiagnosticCodes.MultipleEvidenceCandidates);
    }

    private static void MultipleLicenseCandidates()
    {
        using TemporaryInventory inventory = TemporaryInventory.Single("Many.Package", "1.0.0",
            nuspec: Nuspec("Many.Package", "1.0.0", "<license type=\"expression\">MIT</license>"),
            files: ["many.package.nuspec", "LICENSE", "LICENSE.md"]);
        inventory.WritePackageFile("many.package/1.0.0/LICENSE", "one");
        inventory.WritePackageFile("many.package/1.0.0/LICENSE.md", "two");
        Assert.HasCode(inventory.Scan("net10.0"), NoticeDiagnosticCodes.MultipleEvidenceCandidates);
    }

    private static void DtdIsRejected()
    {
        const string dtd = "<!DOCTYPE package [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><package><metadata><id>Dtd.Package</id><version>1.0.0</version><license type=\"file\">LICENSE</license></metadata></package>";
        using TemporaryInventory inventory = TemporaryInventory.Single("Dtd.Package", "1.0.0", nuspec: dtd,
            files: ["dtd.package.nuspec", "LICENSE"]);
        inventory.WritePackageFile("dtd.package/1.0.0/LICENSE", "license");
        Assert.HasCode(inventory.Scan("net10.0"), NoticeDiagnosticCodes.InvalidEvidenceEncoding);
    }

    private static void InvalidLicenseEncoding()
    {
        using TemporaryInventory inventory = TemporaryInventory.Single("Binary.Package", "1.0.0",
            nuspec: Nuspec("Binary.Package", "1.0.0", "<license type=\"file\">LICENSE</license>"),
            files: ["binary.package.nuspec", "LICENSE"]);
        inventory.WritePackageBytes("binary.package/fixture/LICENSE", [0xc3, 0x28]);
        Assert.HasCode(inventory.Scan("net10.0"), NoticeDiagnosticCodes.InvalidEvidenceEncoding);
    }

    private static void StableOrdering()
    {
        string[] expected = ScanValid().Components.Select(static value => value.PackageUrl.CanonicalValue).ToArray();
        for (int index = 0; index < 5; index++)
        {
            Assert.SequenceEqual(expected, ScanValid().Components.Select(static value => value.PackageUrl.CanonicalValue));
        }
    }

    private static void DiagnosticsAreSanitized()
    {
        string root = Fixture("valid", "packages");
        InventoryResult result = NuGetInventoryAdapter.Scan(new NuGetInventoryOptions(
            Fixture("hostile", "unresolved.lock.json"), Fixture("hostile", "unresolved.assets.json"), "net10.0",
            PackagesRoot: root));
        Assert.True(result.Diagnostics.All(value => !value.Message.Contains(root, StringComparison.OrdinalIgnoreCase)));
        Assert.True(result.Diagnostics.All(value => value.Source is null || !value.Source.Contains(root, StringComparison.OrdinalIgnoreCase)));
    }

    private static void DuplicateJsonProperty()
    {
        InventoryResult result = NuGetInventoryAdapter.Scan(new NuGetInventoryOptions(
            Fixture("hostile", "duplicate-property.lock.json"), Fixture("valid", "project.assets.json"), "net10.0",
            PackagesRoot: Fixture("valid", "packages")));
        Assert.HasCode(result, NoticeDiagnosticCodes.UnsupportedInventoryFormat);
    }

    private static void MalformedUnicode()
    {
        InventoryResult result = NuGetInventoryAdapter.Scan(new NuGetInventoryOptions(
            Fixture("hostile", "malformed-unicode.lock.json"), Fixture("valid", "project.assets.json"), "net10.0",
            PackagesRoot: Fixture("valid", "packages")));
        Assert.HasCode(result, NoticeDiagnosticCodes.UnsupportedInventoryFormat);
    }

    private static void JsonPropertyBudget()
    {
        using TemporaryInventory inventory = TemporaryInventory.Empty("net10.0", "net10.0");
        StringBuilder json = new(3 * 1024 * 1024);
        json.Append("{\"version\":2,");
        for (int index = 0; index <= 200_000; index++)
        {
            json.Append('"').Append('p').Append(index).Append("\":null,");
        }

        json.Append("\"dependencies\":{\"net10.0\":{}}}");
        inventory.WriteLock(json.ToString());
        Assert.HasCode(inventory.Scan("net10.0"), NoticeDiagnosticCodes.UnsupportedInventoryFormat);
    }

    private static void JsonValueBudget()
    {
        using TemporaryInventory inventory = TemporaryInventory.Empty("net10.0", "net10.0");
        StringBuilder json = new(3 * 1024 * 1024);
        json.Append("{\"version\":2,\"padding\":[");
        for (int index = 0; index <= 500_000; index++)
        {
            if (index != 0)
            {
                json.Append(',');
            }

            json.Append("null");
        }

        json.Append("],\"dependencies\":{\"net10.0\":{}}}");
        inventory.WriteLock(json.ToString());
        Assert.HasCode(inventory.Scan("net10.0"), NoticeDiagnosticCodes.UnsupportedInventoryFormat);
    }

    private static void OversizedLock()
    {
        using TemporaryInventory inventory = TemporaryInventory.Empty("net10.0", "net10.0");
        TemporaryInventory.SetFileLength(inventory.LockPath, 32L * 1024 * 1024 + 1);
        Assert.HasCode(inventory.Scan("net10.0"), NoticeDiagnosticCodes.UnsupportedInventoryFormat);
    }

    private static void OversizedAssets()
    {
        using TemporaryInventory inventory = TemporaryInventory.Empty("net10.0", "net10.0");
        TemporaryInventory.SetFileLength(inventory.AssetsPath, 32L * 1024 * 1024 + 1);
        Assert.HasCode(inventory.Scan("net10.0"), NoticeDiagnosticCodes.UnsupportedInventoryFormat);
    }

    private static void OversizedLicense()
    {
        using TemporaryInventory inventory = TemporaryInventory.Single("Large.Package", "1.0.0",
            nuspec: Nuspec("Large.Package", "1.0.0", "<license type=\"file\">LICENSE</license>"),
            files: ["large.package.nuspec", "LICENSE"]);
        inventory.SetPackageFileLength("large.package/fixture/LICENSE", 4L * 1024 * 1024 + 1);
        Assert.HasCode(inventory.Scan("net10.0"), NoticeDiagnosticCodes.InvalidEvidenceEncoding);
    }

    private static void OversizedNuspec()
    {
        using TemporaryInventory inventory = TemporaryInventory.Single("Large.Manifest", "1.0.0");
        inventory.SetPackageFileLength("large.manifest/fixture/large.manifest.nuspec", 4L * 1024 * 1024 + 1);
        Assert.HasCode(inventory.Scan("net10.0"), NoticeDiagnosticCodes.InvalidEvidenceEncoding);
    }

    private static string Fixture(params string[] segments) =>
        Path.Combine([AppContext.BaseDirectory, "fixtures", .. segments]);

    private static string Nuspec(string id, string version, string license) =>
        string.Concat("<?xml version=\"1.0\" encoding=\"utf-8\"?><package><metadata><id>", id,
            "</id><version>", version, "</version>", license, "</metadata></package>");
}

internal sealed class TemporaryInventory : IDisposable
{
    private TemporaryInventory(string root)
    {
        Root = root;
        LockPath = Path.Combine(root, "packages.lock.json");
        AssetsPath = Path.Combine(root, "project.assets.json");
        PackagesRoot = Path.Combine(root, "packages");
        Directory.CreateDirectory(PackagesRoot);
    }

    public string Root { get; }
    public string LockPath { get; }
    public string AssetsPath { get; }
    public string PackagesRoot { get; }

    public static TemporaryInventory Empty(string lockTarget, string assetsTarget, bool twoRoots = false)
    {
        TemporaryInventory inventory = Create();
        File.WriteAllText(inventory.LockPath,
            "{\"version\":2,\"dependencies\":{\"" + lockTarget + "\":{}}}", Encoding.UTF8);
        string folders = twoRoots
            ? "{\"first\":{},\"second\":{}}"
            : "{\"unused\":{}}";
        File.WriteAllText(inventory.AssetsPath,
            "{\"version\":3,\"targets\":{\"" + assetsTarget + "\":{}},\"libraries\":{},\"packageFolders\":" + folders + "}",
            Encoding.UTF8);
        return inventory;
    }

    public static TemporaryInventory Single(
        string id,
        string version,
        string? contentHash = "aGFzaA==",
        string? assetsHash = "aGFzaA==",
        string? nuspec = null,
        IReadOnlyList<string>? files = null)
    {
        TemporaryInventory inventory = Create();
        string lower = id.ToLowerInvariant();
        string packagePath = lower + "/fixture";
        string fileJson = string.Join(',', (files ?? [lower + ".nuspec"])
            .Select(static value => "\"" + value + "\""));
        string lockHash = contentHash is null ? string.Empty : ",\"contentHash\":\"" + contentHash + "\"";
        string libraryHash = assetsHash is null ? string.Empty : "\"sha512\":\"" + assetsHash + "\",";
        string lockJson = "{\"version\":2,\"dependencies\":{\"net10.0\":{" +
            "\"" + id + "\":{\"type\":\"Direct\",\"resolved\":\"" + version + "\"" + lockHash + "}" +
            "}}}";
        string assetsJson = "{\"version\":3,\"targets\":{\"net10.0\":{" +
            "\"" + id + "/" + version + "\":{\"type\":\"package\",\"runtime\":{\"lib/a.dll\":{}}}" +
            "}},\"libraries\":{" +
            "\"" + id + "/" + version + "\":{" + libraryHash + "\"type\":\"package\",\"path\":\"" + packagePath + "\",\"files\":[" + fileJson + "]}" +
            "},\"packageFolders\":{\"unused\":{}}}";
        using (JsonDocument.Parse(lockJson)) { }
        using (JsonDocument.Parse(assetsJson)) { }
        File.WriteAllText(inventory.LockPath, lockJson, Encoding.UTF8);
        File.WriteAllText(inventory.AssetsPath, assetsJson, Encoding.UTF8);
        inventory.WritePackageFile(packagePath + "/" + lower + ".nuspec", nuspec ?? NuspecDocument(id, version));
        return inventory;
    }

    public InventoryResult Scan(string framework, string? runtime = null) =>
        NuGetInventoryAdapter.Scan(new NuGetInventoryOptions(LockPath, AssetsPath, framework, runtime, PackagesRoot));

    public void WritePackageFile(string relative, string text) =>
        WritePackageBytes(relative, Encoding.UTF8.GetBytes(text));

    public void WritePackageBytes(string relative, byte[] bytes)
    {
        string path = Path.Combine(PackagesRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void WriteLock(string text) => File.WriteAllText(LockPath, text, Encoding.UTF8);

    public void SetPackageFileLength(string relative, long length)
    {
        string path = Path.Combine(PackagesRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SetFileLength(path, length);
    }

    public static void SetFileLength(string path, long length)
    {
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }

    public void Dispose()
    {
        if (Root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static TemporaryInventory Create()
    {
        string root = Path.Combine(Path.GetTempPath(), "wut-notice-nuget-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new TemporaryInventory(root);
    }

    private static string NuspecDocument(string id, string version) =>
        "<package><metadata><id>" + id + "</id><version>" + version + "</version></metadata></package>";
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
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + name + ": " + exception.Message);
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

    public static void True(bool value, string? message = null)
    {
        if (!value)
        {
            throw new InvalidOperationException(message ?? "Expected true.");
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException("Sequences differ.");
        }
    }

    public static void HasCode(InventoryResult result, string code) =>
        True(result.Diagnostics.Any(value => string.Equals(value.Code, code, StringComparison.Ordinal)),
            "Expected diagnostic " + code + ". Actual: " + string.Join(", ", result.Diagnostics.Select(static value => value.Code)));
}
