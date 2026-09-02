# Runic.Application.Bridge.Generators

Generate reflection-free C# Application Bridge contracts and dispatchers from committed Runic Bridge IR.

```bash
dotnet add package Runic.Application.Bridge.Generators --prerelease
```

Requires the .NET 10 SDK and `Contract/bridge.ir.json` generated from the handwritten Effect Schema contract. Use it with `Runic.Application.Bridge`; the [templates](https://www.nuget.org/packages/Runic.Application.Templates) demonstrate the complete layout.

```xml
<ItemGroup>
  <AdditionalFiles Include="Contract/bridge.ir.json" />
</ItemGroup>
```

The generator never starts Node or npm. It verifies the IR format and canonical wire fingerprint before emitting strict codecs, typed domain-error helpers, and exhaustive dispatch. Invalid IR produces stable `RTKAB` diagnostics, preserving trim and NativeAOT compatibility. See the [bridge guide](https://github.com/Runic-Artifex/runic-toolkit/blob/main/docs/guides/application-bridge.md), [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
