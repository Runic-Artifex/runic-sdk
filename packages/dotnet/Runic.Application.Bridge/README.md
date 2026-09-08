# Runic.Application.Bridge

Define a typed, revisioned command-and-event boundary between a .NET application and its browser frontend.

```bash
dotnet add package Runic.Application.Bridge --prerelease
```

Requires .NET 10. Pair it with `Runic.Application.Bridge.Generators` and `@runic-artifex/application-bridge`; use `Runic.Application.Desktop` for the native desktop transport or `Runic.Application.Hosting` for a local WebSocket transport.

New applications annotate partial C# classes with `BridgeSnapshot` and
`BridgeCommand`; immutable request and receipt DTOs define the wire model.
`BridgeEvent` and `BridgeError` provide typed outbound payloads. The package
includes the analyzer, reflection-free codecs, project-module discovery and
session-scoped DI activation. Explicit whole-contract Effect authority remains
available for frontend-owned contracts.

Initialization automatically invokes the snapshot provider. Normal application
startup uses generated composition; each logical session owns an async DI scope.
For custom hosts, use `ApplicationBridgeSessionFactory.Create(services)` after
configuring the generated contract services. Sessions own revisioning, duplicate
command handling, cancellation, bounded admission and teardown.

See the [Application Bridge guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/guides/application/guides/application-bridge.md).
