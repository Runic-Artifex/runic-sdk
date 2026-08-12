# RunicToolkit.Desktop

Keep view-models independent of a windowing framework with typed desktop capability contracts.

```bash
dotnet add package RunicToolkit.Desktop --prerelease
```

Requires .NET 10. Reference this package from application code; select `RunicToolkit.Hosting.CsWebUi.App` when a CS-WebUI host should provide the implementations.

```csharp
using RunicToolkit.Desktop;

sealed class Editor(IDesktopCapabilities capabilities)
{
    public bool CanUseClipboard =>
        capabilities.Report[DesktopCapability.Clipboard].IsSupported;
}
```

Check the capability report before optional or permission-gated operations. Desktop contracts use file contents and semantic identifiers, not native paths or DOM handles. See the [API source](https://github.com/Runic-Artifex/runic-toolkit/tree/main/src/RunicToolkit.Desktop), [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
