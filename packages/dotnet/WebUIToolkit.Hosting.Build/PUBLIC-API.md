# WebUIToolkit.Hosting.Build public API

Wave B declared source surface. Types are in the `WebUIToolkit.Hosting.Build`
namespace and consume asset contracts from `WebUIToolkit.Hosting`. Compiler-synthesized
record members are omitted.

```csharp
public sealed class FrontendAssetBuildItem
{
    public FrontendAssetBuildItem(
        string relativePath,
        ReadOnlyMemory<byte> content,
        bool isEntryPoint = false,
        string? mediaType = null,
        string? brotliPath = null,
        string? gzipPath = null);

    public string RelativePath { get; }
    public ReadOnlyMemory<byte> Content { get; }
    public bool IsEntryPoint { get; }
    public string? MediaType { get; }
    public string? BrotliPath { get; }
    public string? GzipPath { get; }
}

public sealed class FrontendAssetManifest : IFrontendAssetManifest
{
    public const string CurrentVersion = "webuitoolkit.frontend-assets/1";

    public FrontendAssetManifest(IReadOnlyList<FrontendAsset> assets);
    public string ManifestVersion { get; }
    public IReadOnlyList<FrontendAsset> Assets { get; }
}

public interface IFrontendMediaTypeResolver
{
    string Resolve(string relativePath);
}

public sealed class DefaultFrontendMediaTypeResolver : IFrontendMediaTypeResolver
{
    public DefaultFrontendMediaTypeResolver();
    public string Resolve(string relativePath);
}

public sealed class FrontendAssetManifestBuilder
{
    public FrontendAssetManifestBuilder();
    public FrontendAssetManifestBuilder(IFrontendMediaTypeResolver mediaTypeResolver);

    public FrontendAssetManifest Build(IEnumerable<FrontendAssetBuildItem> items);
    public FrontendAssetManifest BuildFromDirectory(
        string outputRoot,
        string entryPointRelativePath,
        CancellationToken cancellationToken = default);
    public FrontendAssetManifest BuildFromDirectory(
        string outputRoot,
        string entryPointRelativePath,
        IEnumerable<string> excludedRelativePaths,
        CancellationToken cancellationToken = default);
}

public enum FrontendAssetManifestIssueKind
{
    UnsupportedVersion = 0,
    EmptyManifest = 1,
    NullAsset = 2,
    DuplicatePath = 3,
    NonDeterministicOrder = 4,
    EntryPointCardinality = 5,
    MissingCompressedVariant = 6,
}

public sealed record FrontendAssetManifestIssue(
    FrontendAssetManifestIssueKind Kind,
    int? AssetIndex,
    string Message);

public static class FrontendAssetManifestValidator
{
    public static IReadOnlyList<FrontendAssetManifestIssue> Validate(
        IFrontendAssetManifest manifest);
}

public static class FrontendAssetManifestJson
{
    public static byte[] SerializeToUtf8Bytes(IFrontendAssetManifest manifest);
    public static string Serialize(IFrontendAssetManifest manifest);
    public static FrontendAssetManifest Deserialize(ReadOnlySpan<byte> utf8Json);
}

public sealed class GenerateFrontendAssetManifestTask : Microsoft.Build.Utilities.Task
{
    public string OutputDirectory { get; set; }
    public string EntryPoint { get; set; }
    public string ManifestPath { get; set; }
    public bool VerifyOnly { get; set; }
    public Microsoft.Build.Framework.ITaskItem[] Assets { get; }
    public override bool Execute();
}
```
