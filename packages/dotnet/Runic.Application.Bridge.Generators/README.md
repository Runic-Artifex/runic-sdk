# Runic.Application.Bridge.Generators

Generate reflection-free C# Application Bridge contracts and dispatchers from committed schema artifacts.

```bash
dotnet add package Runic.Application.Bridge.Generators --prerelease
```

Requires the .NET 10 SDK and an Application Bridge manifest plus JSON Schema files. Use it with `Runic.Application.Bridge`; the [templates](https://www.nuget.org/packages/Runic.Application.Templates) demonstrate the complete layout.

```xml
<ItemGroup>
  <AdditionalFiles Include="bridge.manifest.json" />
  <AdditionalFiles Include="schema/**/*.json" />
</ItemGroup>
```

The generator never starts Node or npm. Invalid schema constructs and stale fingerprints produce stable `RTKAB` diagnostics, preserving trim and NativeAOT compatibility. See the [bridge guide](https://github.com/Runic-Artifex/runic-toolkit/blob/main/docs/guides/application-bridge.md), [examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
