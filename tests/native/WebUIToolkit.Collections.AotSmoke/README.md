# Native-AOT smoke executable

This executable uses no reflection, dynamic code, serializer discovery,
suppression, or trim descriptors. It exercises range add/insert/remove/replace/
move, clear, snapshot and event-payload isolation, exact notification ordering,
Range and Reset policy, comparer/FIFO `UpdateTo`, keyed `UpdateTo`, no-op/result
contracts, reentrancy rejection, and pre-mutation/subscriber exception behavior.

The promotion matrix covers the desktop hosts currently supported by this smoke:

| Host/toolchain | RID |
| --- | --- |
| Windows x64 with Visual Studio C++ build tools | `win-x64` |
| Linux x64 with the Native-AOT compiler/linker prerequisites | `linux-x64` |
| Apple Silicon macOS with Xcode command-line tools | `osx-arm64` |

Run the host-aware harness from PowerShell 7. It auto-detects the RID, rejects
cross-compilation, runs the managed smoke, and publishes and runs the native binary:

```powershell
./tests/WebUIToolkit.Collections.AotSmoke/run-native-smoke.ps1
```

Release jobs may state their matching RID explicitly:

```powershell
./tests/WebUIToolkit.Collections.AotSmoke/run-native-smoke.ps1 -RuntimeIdentifier win-x64
./tests/WebUIToolkit.Collections.AotSmoke/run-native-smoke.ps1 -RuntimeIdentifier linux-x64
./tests/WebUIToolkit.Collections.AotSmoke/run-native-smoke.ps1 -RuntimeIdentifier osx-arm64
```

For environments without PowerShell, use the equivalent parameterized commands
below, replacing `<rid>` with the RID matching the current host:

```console
dotnet build -c Release src/WebUIToolkit.Collections
dotnet restore tests/WebUIToolkit.Collections.AotSmoke
dotnet run -c Release --no-restore --project tests/WebUIToolkit.Collections.AotSmoke
dotnet restore -r <rid> --disable-parallel -p:PublishAot=true -p:PublishTrimmed=true tests/WebUIToolkit.Collections.AotSmoke
dotnet publish -c Release -r <rid> --no-restore -p:PublishAot=true -p:PublishTrimmed=true tests/WebUIToolkit.Collections.AotSmoke
```

The RID-specific smoke consumes the built shipping assembly through the explicit
Release-bin `HintPath`. A non-reference-producing project edge orders the shipping
build deterministically without changing the assembly under test. RID properties
therefore participate in the restore graph.

Run the native executable from the publish directory. Success prints one stable
`PASS` line and exits zero; validation failures print one `FAIL` line and exit one.
Promotion evidence is host-specific: a RID is accepted only after the native
binary has been built and executed on a matching host with its required toolchain.
