# RunicToolkit.Hosting.Build

Create a deterministic manifest for a built frontend and optionally add its files to your .NET output.

```bash
dotnet add package RunicToolkit.Hosting.Build --prerelease
```

Requires .NET 10 and a local, already-configured frontend build command. Choose `Directory` mode to serve output in place, `Copy` to publish it as content, or `Embed` for assembly resources.

```xml
<PropertyGroup>
  <RunicToolkitGenerateFrontendAssets>true</RunicToolkitGenerateFrontendAssets>
  <RunicToolkitFrontendOutputDirectory>Frontend/dist</RunicToolkitFrontendOutputDirectory>
  <RunicToolkitFrontendEntryPoint>index.html</RunicToolkitFrontendEntryPoint>
  <RunicToolkitFrontendAssetMode>Copy</RunicToolkitFrontendAssetMode>
</PropertyGroup>
```

The task never downloads tools or network content. It rejects unsafe paths and reparse points; do not allow untrusted writers to modify the output tree during a build. See the [build reference](https://github.com/Runic-Artifex/runic-toolkit/tree/main/src/RunicToolkit.Hosting.Build), [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
