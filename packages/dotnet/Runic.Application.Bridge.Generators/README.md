# Runic.Application.Bridge.Generators

The analyzer bundled in `Runic.Application.Bridge` generates reflection-free adapters, strict codecs, dispatchers, and dependency injection registration. It requires the .NET 10 SDK.

C# is the default authority. Annotate commands and a snapshot provider on partial classes, declare immutable request and receipt DTOs, and put one `ApplicationBridgeContract` attribute on the entry assembly. Referenced projects contribute generated module metadata and registrars, including adapters for private members. The application uses its own DTO types directly.

For Effect authority, set `RunicApplicationBridgeAuthority` to `effect` and include the generated `Contract/bridge.ir.json` as an `AdditionalFiles` item. The generator emits C# DTOs, a typed snapshot provider and command-handler interface, codecs, and composition from that IR.

Source generators never write committed artifacts or start JavaScript tooling. The Node compiler orchestrates the IR and frontend facade, using the managed Roslyn inspector for C# authority. The build targets run generation before compilation. Invalid contracts produce source-located `RTKAB` diagnostics; generated runtime code supports trimming and NativeAOT.

See the [bridge guide](../../../docs/guides/application/guides/application-bridge.md) for authoring, DI scopes, project modules, and frontend schema enrichment.
