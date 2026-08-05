# CommunityToolkit trimming smoke fixture

The fixture consumes only packed packages, enables full trimming and trim
analyzers, and exercises generated observable-property validation, async command
cancellation, and adapter disposal. It is Wave C evidence, not the Wave D G4
Native-AOT release gate.

```console
dotnet restore tests/RunicToolkit.MVVM.CommunityToolkit.Tests/AotSmoke
dotnet pack -c Release -p:PackageVersion=0.0.0-local -o tests/RunicToolkit.MVVM.CommunityToolkit.Tests/PackageConsumer/obj/packages src/RunicToolkit.MVVM
dotnet pack -c Release -p:PackageVersion=0.0.0-local -o tests/RunicToolkit.MVVM.CommunityToolkit.Tests/PackageConsumer/obj/packages src/RunicToolkit.MVVM.CommunityToolkit
dotnet restore -p:CommunityToolkitAdapterPackageVersion=0.0.0-local -p:RestoreAdditionalProjectSources=../PackageConsumer/obj/packages tests/RunicToolkit.MVVM.CommunityToolkit.Tests/AotSmoke
dotnet publish -c Release --no-restore -p:CommunityToolkitAdapterPackageVersion=0.0.0-local -p:PublishTrimmed=true tests/RunicToolkit.MVVM.CommunityToolkit.Tests/AotSmoke
```
