# CommunityToolkit trimming smoke fixture

The fixture consumes only packed packages, enables full trimming and trim
analyzers, and exercises generated observable-property validation, async command
cancellation, and adapter disposal. It is Wave C evidence, not the Wave D G4
Native-AOT release gate.

```console
dotnet restore --locked-mode tests/WebUIToolkit.MVVM.CommunityToolkit.Tests/AotSmoke
dotnet pack -c Release -p:PackageVersion=0.0.0-local -o tests/WebUIToolkit.MVVM.CommunityToolkit.Tests/PackageConsumer/obj/packages src/WebUIToolkit.MVVM
dotnet pack -c Release -p:PackageVersion=0.0.0-local -o tests/WebUIToolkit.MVVM.CommunityToolkit.Tests/PackageConsumer/obj/packages src/WebUIToolkit.MVVM.CommunityToolkit
dotnet restore -p:CommunityToolkitAdapterPackageVersion=0.0.0-local -p:RestoreAdditionalProjectSources=../PackageConsumer/obj/packages -p:NuGetLockFilePath=obj/aot.packages.lock.json -p:RestoreLockedMode=false tests/WebUIToolkit.MVVM.CommunityToolkit.Tests/AotSmoke
dotnet publish -c Release --no-restore -p:CommunityToolkitAdapterPackageVersion=0.0.0-local -p:NuGetLockFilePath=obj/aot.packages.lock.json -p:PublishTrimmed=true tests/WebUIToolkit.MVVM.CommunityToolkit.Tests/AotSmoke
```
