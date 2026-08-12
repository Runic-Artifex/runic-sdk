# RunicToolkit.Hosting.CsWebUi.App

Build a native CS-WebUI application from a frontend asset provider, a root session, and the shared lifecycle.

```bash
dotnet add package RunicToolkit.Hosting.CsWebUi.App --prerelease
```

Requires .NET 10 and CS-WebUI. This is the high-level host for applications without the Application Bridge; use `RunicToolkit.Hosting.CsWebUi.ApplicationBridge` when your frontend uses generated bridge contracts.

```csharp
var builder = WebUiApp.CreateBuilder(args);
builder.UseCsWebUi("MyFrontend", new CsWebUiAppOptions(
    assets, root, new CsWebUiAdapterOptions(webRoot),
    new BrowserHostOptions("my-app"),
    new BrowserWindowOptions("main", "My app", 1000, 720)));
return await builder.RunAsync();
```

The builder registers frontend-neutral desktop services for the exact native-window lifetime. Use one frontend per builder. See the [public API](https://github.com/Runic-Artifex/runic-toolkit/blob/main/src/RunicToolkit.Hosting.CsWebUi.App/PUBLIC-API.md), [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
