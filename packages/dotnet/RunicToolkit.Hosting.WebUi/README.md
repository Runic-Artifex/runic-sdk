# RunicToolkit.Hosting.WebUi

Serve a validated static frontend and scope its root session through framework-neutral browser contracts.

```bash
dotnet add package RunicToolkit.Hosting.WebUi --prerelease
dotnet add package RunicToolkit.Hosting.Build --prerelease
```

Requires .NET 10. Use it to author a browser-host adapter; choose `RunicToolkit.Hosting.CsWebUi.App` when you want the supplied native app composition.

```csharp
using RunicToolkit.Hosting.Build;
using RunicToolkit.Hosting.WebUi;

var manifest = new FrontendAssetManifestBuilder().BuildFromDirectory("wwwroot", "index.html");
var assets = new DirectoryFrontendAssetProvider("wwwroot", manifest);
```

`FrontendAssetManifestBuilder` comes from the companion `RunicToolkit.Hosting.Build` package; keep both packages on the same preview version. This is not an ASP.NET Core host and does not discover a native runtime. Pair the assets with a concrete browser adapter and root-session factory. See the [package source](https://github.com/Runic-Artifex/runic-toolkit/tree/main/src/RunicToolkit.Hosting.WebUi), [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
