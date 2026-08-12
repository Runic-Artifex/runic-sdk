# RunicToolkit.ApplicationBridge

Define a typed, revisioned command-and-event boundary between a .NET application and its browser frontend.

```bash
dotnet add package RunicToolkit.ApplicationBridge --prerelease
```

Requires .NET 10. Pair it with `RunicToolkit.ApplicationBridge.Generators` and `@runic-artifex/application-bridge`; use `RunicToolkit.Hosting.CsWebUi.ApplicationBridge` for the ready-made CS-WebUI transport.

```csharp
await using var session = new ApplicationBridgeSession(
    new CounterBridgeDispatcher(new CounterBridgeHandler()));
```

`CounterBridgeDispatcher` and `CounterBridgeHandler` above are generated/application types from the template. Sessions own revisioning, duplicate-command handling, cancellation, bounded admission, and teardown. Read the [Application Bridge guide](https://github.com/Runic-Artifex/runic-toolkit/blob/main/docs/guides/application-bridge.md), start from [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), or file [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
