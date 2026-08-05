using System;
using System.IO;
using System.Reflection;
using RunicAssets;

var source = new EmbeddedAssetSource(
    Assembly.GetExecutingAssembly(),
    [new("index.html", "PackageConsumer.index.html", IsEntryPoint: true)]);

await source.ValidateAsync();
await using Stream content = await source.OpenReadAsync(source.Manifest.EntryPoint.RelativePath);
using var reader = new StreamReader(content);
return (await reader.ReadToEndAsync()).Contains("package consumer", StringComparison.Ordinal) ? 0 : 1;
