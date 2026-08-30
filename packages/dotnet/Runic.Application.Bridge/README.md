# Runic.Application.Bridge

Define a typed, revisioned command-and-event boundary between a .NET application and its browser frontend.

```bash
dotnet add package Runic.Application.Bridge --prerelease
```

Requires .NET 10. Pair it with `Runic.Application.Bridge.Generators` and `@runic-artifex/application-bridge`; use `Runic.Application.Desktop` for the native desktop transport or `Runic.Application.Hosting` for a local WebSocket transport.

```csharp
await using var session = new ApplicationBridgeSession(
    new CounterBridgeDispatcher(new CounterBridgeHandler()));
```

`CounterBridgeDispatcher` and `CounterBridgeHandler` above are generated/application types from the template. Sessions own revisioning, duplicate-command handling, cancellation, bounded admission, and teardown. Read the [Application Bridge guide](https://github.com/Runic-Artifex/runic-toolkit/blob/main/docs/guides/application-bridge.md), start from [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), or file [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
