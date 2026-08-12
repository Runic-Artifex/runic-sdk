# RunicToolkit.Hosting.CsWebUi.ApplicationBridge

Connect a generated Application Bridge session to a native CS-WebUI frontend through one bounded binary binding.

```bash
dotnet add package RunicToolkit.Hosting.CsWebUi.ApplicationBridge --prerelease
```

Requires .NET 10, CS-WebUI, `RunicToolkit.ApplicationBridge`, and matching generated contracts. It is the adoption path for a template-based Application Bridge app.

```csharp
var options = new ApplicationBridgeFrontendApplicationOptions(
    assets, new CsWebUiAdapterOptions(webRoot),
    new BrowserHostOptions("my-app"),
    new BrowserWindowOptions("main", "My app", 1000, 720),
    static () => new ApplicationBridgeSession(
        new CounterBridgeDispatcher(new CounterBridgeHandler())));
await using var frontend = builder.UseApplicationBridge("MyFrontend", options);
```

The dispatcher and handler are generated/application types, as in the templates. Each native root lifetime receives a fresh logical session; reconnect and ordered event handling are transport-owned. See the [bridge guide](https://github.com/Runic-Artifex/runic-toolkit/blob/main/docs/guides/application-bridge.md), [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
