using System;
using System.IO;
using System.Linq;
using System.Reflection;
using RunicAssets;
using RunicAssets.RunicToolkit;

var source = new EmbeddedAssetSource(
    Assembly.GetExecutingAssembly(),
    [new("index.html", "PackageConsumer.index.html", IsEntryPoint: true)]);

await source.ValidateAsync();
var boundary = new RunicToolkitAssetBoundary(source, new Uri("app://package-consumer/application"));
await boundary.ValidateAsync();
await using Stream content = await boundary.OpenReadAsync(
    boundary.Manifest.Assets.Single(static asset => asset.IsEntryPoint).RelativePath,
    default);
using var reader = new StreamReader(content);
return boundary.Manifest.ManifestVersion == "runic-toolkit.frontend-assets/1"
    && boundary.EntryPoint.AbsoluteUri == "app://package-consumer/application/index.html"
    && (await reader.ReadToEndAsync()).Contains("package consumer", StringComparison.Ordinal)
    ? 0
    : 1;
