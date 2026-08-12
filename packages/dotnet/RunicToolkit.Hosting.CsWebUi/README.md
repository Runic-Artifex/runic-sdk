# RunicToolkit.Hosting.CsWebUi

Host a local static frontend in a native CS-WebUI window without ASP.NET Core.

```bash
dotnet add package RunicToolkit.Hosting.CsWebUi --prerelease
```

Requires .NET 10 and the matching prerelease [CS-WebUI](https://github.com/Runic-Artifex/cs-webui) native dependency. Choose this low-level adapter for a custom host; choose `RunicToolkit.Hosting.CsWebUi.App` for the high-level app builder.

```csharp
var factory = new CsWebUiBrowserHostFactory(
    new CsWebUiAdapterOptions("wwwroot", CsWebUiPresentationMode.Auto));
```

Pass the factory to `WebUiModeRunner`. The adapter serves only the current local application, validates entry routes, and serializes process-wide CS-WebUI native work. Do not show or dispose a window from `configureWindow`. See the [adapter reference](https://github.com/Runic-Artifex/runic-toolkit/tree/main/src/RunicToolkit.Hosting.CsWebUi), [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
