# RunicToolkit.Hosting.Abstractions

Use the neutral contracts for lifecycle, browser hosting, frontend assets, and desktop-capability adapters.

```bash
dotnet add package RunicToolkit.Hosting.Abstractions --prerelease
```

Requires .NET 10. Most application authors should install `RunicToolkit.Hosting` or a higher-level adapter instead; reference this package when building a host adapter or contract-only library.

```csharp
using RunicToolkit.Hosting;

var window = new BrowserWindowOptions("main", "My app", 1000, 720);
```

Host and asset options validate identifiers, paths, and content metadata at the boundary. The interfaces intentionally expose no native handle or runtime-specific type. See the [public API](https://github.com/Runic-Artifex/runic-toolkit/blob/main/src/RunicToolkit.Hosting.Abstractions/PUBLIC-API.md), [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
