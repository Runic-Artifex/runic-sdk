# Runic.Desktop.NativeServer.Static.<rid>

The static library of the native Runic Desktop server for one runtime identifier, which a NativeAOT publish links
into the executable. Do not reference it: `Runic.Desktop.NativeServer` brings it through its `runtime.json` when
NuGet restores for that runtime identifier, and its build targets link it and keep it out of every output. See the
[Runic.Desktop.NativeServer README](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop.NativeServer/README.md).
