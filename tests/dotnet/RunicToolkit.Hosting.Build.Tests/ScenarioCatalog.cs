using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RunicToolkit.Hosting.Build;

namespace RunicToolkit.Hosting.Build.Tests;

internal static class ScenarioCatalog
{
    private static readonly byte[] EmptyContent = [];

    public static IReadOnlyList<TestScenario> All { get; } =
    [
        new("manifest.bytes_are_stable_across_order_and_culture", StableBytesAcrossOrderAndCulture),
        new("manifest.uses_ordinal_path_order", OrdinalPathOrder),
        new("manifest.computes_lowercase_sha256", ComputesSha256),
        new("manifest.requires_exactly_one_entry_point", EntryPointCardinality),
        new("paths.reject_empty_traversal_and_rooted_values", RejectsUnsafePathShapes),
        new("paths.reject_hostile_url_like_values", RejectsHostilePaths),
        new("paths.reject_case_insensitive_duplicates", RejectsCaseInsensitiveDuplicates),
        new("metadata.resolves_stable_media_types", ResolvesMediaTypes),
        new("metadata.preserves_normalized_compression_paths", PreservesCompressionMetadata),
        new("metadata.rejects_missing_compressed_variants", RejectsMissingCompressedVariants),
        new("output.snapshots_content_and_collections", OutputIsImmutable),
        new("directory.build_is_scoped_and_deterministic", DirectoryBuildIsScoped),
        new("directory.rejects_invalid_roots_and_entry_paths", DirectoryRejectsInvalidInputs),
        new("directory.observes_precancelled_token", DirectoryObservesCancellation),
        new("directory.rejects_reparse_points_when_supported", DirectoryRejectsReparsePoints),
        new("directory.rejects_unix_ambiguous_file_names", DirectoryRejectsUnixAmbiguousFileNames),
        new("validation.reports_stable_immutable_issues", ValidationIssuesAreStableAndImmutable),
        new("architecture.has_only_explicit_msbuild_tooling", HasOnlyExplicitBuildTooling),
    ];

    private static ValueTask StableBytesAcrossOrderAndCulture()
    {
        FrontendAssetBuildItem[] forward =
        [
            Item("index.html", "<main></main>", isEntryPoint: true),
            Item("scripts/I.js", "export {};"),
            Item("assets/icon.svg", "<svg/>")
        ];
        FrontendAssetBuildItem[] reverse = forward.Reverse().ToArray();
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            byte[] first = FrontendAssetManifestJson.SerializeToUtf8Bytes(new FrontendAssetManifestBuilder().Build(forward));

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            byte[] second = FrontendAssetManifestJson.SerializeToUtf8Bytes(new FrontendAssetManifestBuilder().Build(reverse));

            TestAssert.EqualSequence(first, second, "manifest bytes must not depend on enumeration order or ambient culture");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask OrdinalPathOrder()
    {
        FrontendAssetManifest manifest = new FrontendAssetManifestBuilder().Build(
        [
            Item("index.html", "entry", isEntryPoint: true),
            Item("a.js", "lower"),
            Item("Z.js", "upper")
        ]);

        TestAssert.EqualSequence(
            new[] { "Z.js", "a.js", "index.html" },
            manifest.Assets.Select(asset => asset.RelativePath).ToArray());
        return ValueTask.CompletedTask;
    }

    private static ValueTask ComputesSha256()
    {
        FrontendAssetManifest manifest = new FrontendAssetManifestBuilder().Build(
            [Item("index.html", "abc", isEntryPoint: true)]);
        FrontendAsset asset = manifest.Assets[0];

        TestAssert.Equal(3L, asset.Length);
        TestAssert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            asset.Sha256);
        return ValueTask.CompletedTask;
    }

    private static ValueTask EntryPointCardinality()
    {
        var builder = new FrontendAssetManifestBuilder();
        TestAssert.Throws<InvalidOperationException>(() => builder.Build([]));
        TestAssert.Throws<InvalidOperationException>(() => builder.Build([Item("index.html", "none")]));
        TestAssert.Throws<InvalidOperationException>(() => builder.Build(
        [
            Item("index.html", "one", isEntryPoint: true),
            Item("other.html", "two", isEntryPoint: true)
        ]));
        return ValueTask.CompletedTask;
    }

    private static ValueTask RejectsUnsafePathShapes()
    {
        string[] invalidPaths =
        [
            "", "   ", "/index.html", "C:/index.html", "C:\\index.html",
            "../index.html", "assets/../index.html", "./index.html", "assets//index.html"
        ];

        foreach (string invalidPath in invalidPaths)
        {
            TestAssert.Throws<ArgumentException>(
                () => new FrontendAssetManifestBuilder().Build([new(invalidPath, EmptyContent, true)]),
                $"the path <{Escape(invalidPath)}> is unsafe");
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask RejectsHostilePaths()
    {
        string[] hostilePaths =
        [
            "https://example.test/index.html", "//server/share/index.html", "\\\\server\\share\\index.html",
            "index.html?debug=true", "index.html#fragment", "assets/na:me.js", "assets/bad\0name.js",
            "assets/bad\tname.js", "assets/%2e%2e/index.html", "assets%2findex.html",
            "assets%5cindex.html", "assets/%252e%252e/index.html", "assets/%00.bin",
            "assets/%0d.txt", "assets/%3f.txt", "assets/%23.txt", "assets/%3a.txt", "assets/%20.txt"
        ];

        foreach (string hostilePath in hostilePaths)
        {
            TestAssert.Throws<ArgumentException>(
                () => new FrontendAssetManifestBuilder().Build([new(hostilePath, EmptyContent, true)]),
                $"the path <{Escape(hostilePath)}> must not enter the manifest");
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask RejectsCaseInsensitiveDuplicates()
    {
        TestAssert.Throws<InvalidOperationException>(() => new FrontendAssetManifestBuilder().Build(
        [
            Item("index.html", "first", isEntryPoint: true),
            Item("INDEX.HTML", "second")
        ]));
        return ValueTask.CompletedTask;
    }

    private static ValueTask ResolvesMediaTypes()
    {
        var resolver = new DefaultFrontendMediaTypeResolver();
        (string Path, string Expected)[] cases =
        [
            ("index.HTML", "text/html; charset=utf-8"),
            ("styles/site.css", "text/css; charset=utf-8"),
            ("scripts/app.mjs", "text/javascript; charset=utf-8"),
            ("images/logo.svg", "image/svg+xml"),
            ("runtime/module.wasm", "application/wasm"),
            ("assets/blob.unknown", "application/octet-stream")
        ];

        foreach ((string path, string expected) in cases)
        {
            TestAssert.Equal(expected, resolver.Resolve(path));
        }

        FrontendAssetManifest manifest = new FrontendAssetManifestBuilder().Build(
            [new("index.custom", EmptyContent, true, "  application/vnd.example+test  ")]);
        TestAssert.Equal("application/vnd.example+test", manifest.Assets[0].MediaType);
        TestAssert.Throws<ArgumentException>(() => new FrontendAssetManifestBuilder().Build(
            [new("index.html", EmptyContent, true, "text/\r\nhtml")]));
        return ValueTask.CompletedTask;
    }

    private static ValueTask PreservesCompressionMetadata()
    {
        FrontendAssetManifest manifest = new FrontendAssetManifestBuilder().Build(
        [
            new(
                "scripts\\app.js",
                Encoding.UTF8.GetBytes("export {};"),
                isEntryPoint: true,
                brotliPath: "scripts\\app.js.br",
                gzipPath: "scripts\\app.js.gz"),
            new("scripts/app.js.br", new byte[] { 0x01 }),
            new("scripts/app.js.gz", new byte[] { 0x02 })
        ]);
        FrontendAsset asset = manifest.Assets.Single(candidate => candidate.IsEntryPoint);

        TestAssert.Equal("scripts/app.js", asset.RelativePath);
        TestAssert.Equal("scripts/app.js.br", asset.BrotliPath);
        TestAssert.Equal("scripts/app.js.gz", asset.GzipPath);
        return ValueTask.CompletedTask;
    }

    private static ValueTask RejectsMissingCompressedVariants()
    {
        TestAssert.Throws<InvalidOperationException>(() => new FrontendAssetManifestBuilder().Build(
        [
            new("index.html", EmptyContent, true, brotliPath: "index.html.br")
        ]));

        var invalid = new FakeManifest(
            FrontendAssetManifest.CurrentVersion,
            [new FrontendAsset("index.html", "text/html", 0, new string('0', 64), true, "index.html.br", "index.html.gz")]);
        IReadOnlyList<FrontendAssetManifestIssue> issues = FrontendAssetManifestValidator.Validate(invalid);
        TestAssert.EqualSequence(
            new[]
            {
                FrontendAssetManifestIssueKind.MissingCompressedVariant,
                FrontendAssetManifestIssueKind.MissingCompressedVariant
            },
            issues.Select(issue => issue.Kind).ToArray());
        TestAssert.True(issues.All(issue => issue.AssetIndex == 0));
        TestAssert.Equal(issues[0].Message, issues[1].Message, "Brotli and gzip failures use one stable message");
        return ValueTask.CompletedTask;
    }

    private static ValueTask OutputIsImmutable()
    {
        byte[] source = Encoding.UTF8.GetBytes("abc");
        var item = new FrontendAssetBuildItem("index.html", source, isEntryPoint: true);
        source[0] = (byte)'z';
        ReadOnlyMemory<byte> exposedContent = item.Content;
        TestAssert.True(MemoryMarshal.TryGetArray(exposedContent, out ArraySegment<byte> exposedArray));
        byte[] exposedBacking = exposedArray.Array
            ?? throw new TestAssertionException("Expected the copied content to expose array-backed memory.");
        exposedBacking[exposedArray.Offset] = (byte)'y';
        FrontendAssetManifest manifest = new FrontendAssetManifestBuilder().Build([item]);

        TestAssert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            manifest.Assets[0].Sha256,
            "build items must snapshot caller-owned content");

        IList<FrontendAsset> mutableView = TestAssertAssignableList(manifest.Assets);
        TestAssert.Throws<NotSupportedException>(() => mutableView.Clear());
        TestAssert.Equal(1, manifest.Assets.Count);
        return ValueTask.CompletedTask;
    }

    private static ValueTask DirectoryRejectsUnixAmbiguousFileNames()
    {
        if (OperatingSystem.IsWindows())
        {
            return ValueTask.CompletedTask;
        }

        AssertDirectoryFileNameRejected(" leading.js");
        AssertDirectoryFileNameRejected("trailing.js ");
        AssertDirectoryFileNameRejected("literal\\backslash.js");
        return ValueTask.CompletedTask;
    }

    private static ValueTask DirectoryBuildIsScoped()
    {
        using var temp = new ScopedTemporaryDirectory();
        temp.Write("index.html", "entry");
        temp.Write(Path.Combine("scripts", "app.js"), "script");
        temp.Write(Path.Combine("assets", "data.bin"), "data");

        FrontendAssetManifest manifest = new FrontendAssetManifestBuilder()
            .BuildFromDirectory(temp.Path, "index.html");

        TestAssert.EqualSequence(
            new[] { "assets/data.bin", "index.html", "scripts/app.js" },
            manifest.Assets.Select(asset => asset.RelativePath).ToArray());
        TestAssert.Equal(1, manifest.Assets.Count(asset => asset.IsEntryPoint));
        TestAssert.Equal("index.html", manifest.Assets.Single(asset => asset.IsEntryPoint).RelativePath);
        return ValueTask.CompletedTask;
    }

    private static ValueTask DirectoryRejectsInvalidInputs()
    {
        var builder = new FrontendAssetManifestBuilder();
        string missingRoot = Path.Combine(Path.GetTempPath(), "RunicToolkit.Hosting.Build.Tests", Guid.NewGuid().ToString("N"));
        TestAssert.Throws<DirectoryNotFoundException>(() => builder.BuildFromDirectory(missingRoot, "index.html"));

        using var temp = new ScopedTemporaryDirectory();
        temp.Write("index.html", "entry");
        TestAssert.Throws<ArgumentException>(() => builder.BuildFromDirectory(temp.Path, "../index.html"));
        TestAssert.Throws<ArgumentException>(() => builder.BuildFromDirectory(temp.Path, "https://example.test/index.html"));

        using var empty = new ScopedTemporaryDirectory();
        TestAssert.Throws<InvalidOperationException>(() => builder.BuildFromDirectory(empty.Path, "index.html"));
        return ValueTask.CompletedTask;
    }

    private static ValueTask DirectoryObservesCancellation()
    {
        using var temp = new ScopedTemporaryDirectory();
        temp.Write("index.html", "entry");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        TestAssert.Throws<OperationCanceledException>(() =>
            new FrontendAssetManifestBuilder().BuildFromDirectory(temp.Path, "index.html", cancellation.Token));
        return ValueTask.CompletedTask;
    }

    private static ValueTask DirectoryRejectsReparsePoints()
    {
        using var external = new ScopedTemporaryDirectory();
        external.Write("external.txt", "must not be read");
        using var temp = new ScopedTemporaryDirectory();
        temp.Write("index.html", "entry");
        string linkPath = Path.Combine(temp.Path, "linked.txt");
        string targetPath = Path.Combine(external.Path, "external.txt");

        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            TestAssert.Throws<IOException>(() =>
                new FrontendAssetManifestBuilder().BuildFromDirectory(temp.Path, "index.html"));
        }
        finally
        {
            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask ValidationIssuesAreStableAndImmutable()
    {
        var invalid = new FakeManifest("unsupported/9", []);
        IReadOnlyList<FrontendAssetManifestIssue> issues = FrontendAssetManifestValidator.Validate(invalid);

        TestAssert.EqualSequence(
            new[]
            {
                FrontendAssetManifestIssueKind.UnsupportedVersion,
                FrontendAssetManifestIssueKind.EmptyManifest,
                FrontendAssetManifestIssueKind.EntryPointCardinality
            },
            issues.Select(issue => issue.Kind).ToArray());
        TestAssert.True(issues.All(issue => !issue.Message.Contains("unsupported/9", StringComparison.Ordinal)),
            "validation messages must not disclose attacker-controlled values");

        IList<FrontendAssetManifestIssue> mutableView = TestAssertAssignableList(issues);
        TestAssert.Throws<NotSupportedException>(() => mutableView.Clear());
        return ValueTask.CompletedTask;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "This contract intentionally inspects the references retained in both managed and Native AOT outputs.")]
    private static ValueTask HasOnlyExplicitBuildTooling()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return ValueTask.CompletedTask;
        }

        Assembly buildAssembly = typeof(FrontendAssetManifestBuilder).Assembly;
        string[] forbiddenFragments =
        [
            "Microsoft.Extensions", "Microsoft.CodeAnalysis",
            "RunicToolkit.ApplicationBridge", "RunicCommandLine", "cs-webui"
        ];
        string[] references = buildAssembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        foreach (string forbidden in forbiddenFragments)
        {
            TestAssert.True(
                references.All(reference => !reference.Contains(forbidden, StringComparison.OrdinalIgnoreCase)),
                $"Hosting.Build must not acquire the hidden service dependency <{forbidden}>");
        }

        TestAssert.True(references.Contains("RunicToolkit.Hosting.Abstractions", StringComparer.Ordinal));
        TestAssert.True(references.Contains("Microsoft.Build.Framework", StringComparer.Ordinal));
        TestAssert.True(references.Contains("Microsoft.Build.Utilities.Core", StringComparer.Ordinal));
        return ValueTask.CompletedTask;
    }

    private static FrontendAssetBuildItem Item(string relativePath, string content, bool isEntryPoint = false)
        => new(relativePath, Encoding.UTF8.GetBytes(content), isEntryPoint);

    private static void AssertDirectoryFileNameRejected(string hostileFileName)
    {
        using var temp = new ScopedTemporaryDirectory();
        temp.Write("index.html", "entry");
        temp.Write(hostileFileName, "hostile");
        TestAssert.Throws<IOException>(() =>
            new FrontendAssetManifestBuilder().BuildFromDirectory(temp.Path, "index.html"));
    }

    private static string Escape(string value) => value.Replace("\0", "\\0", StringComparison.Ordinal);

    private static IList<T> TestAssertAssignableList<T>(IReadOnlyList<T> values)
    {
        if (values is IList<T> list)
        {
            return list;
        }

        throw new TestAssertionException($"Expected immutable collection to implement {typeof(IList<T>).FullName}.");
    }

    private sealed record FakeManifest(
        string ManifestVersion,
        IReadOnlyList<FrontendAsset> Assets) : IFrontendAssetManifest;

    private sealed class ScopedTemporaryDirectory : IDisposable
    {
        public ScopedTemporaryDirectory()
        {
            string parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "RunicToolkit.Hosting.Build.Tests");
            Directory.CreateDirectory(parent);
            Path = System.IO.Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Write(string relativePath, string content)
        {
            string fullPath = System.IO.Path.Combine(Path, relativePath);
            string? directory = System.IO.Path.GetDirectoryName(fullPath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
