# MVVM package and Native-AOT smoke

The executable deliberately uses public `WebUIToolkit.MVVM` APIs only. It does not use reflection, runtime code generation, or reflection-based JSON serialization.

The normal repository build uses a project reference:

```powershell
dotnet run --project tests/WebUIToolkit.MVVM.AotSmoke -c Release
```

A direct project-reference Native-AOT publish is:

```powershell
dotnet publish tests/WebUIToolkit.MVVM.AotSmoke -c Release -r win-x64 --self-contained true -p:MvvmNativeAot=true
```

To prove that the packed NuGet package is consumable and Native-AOT compatible, run the script from any directory and supply a .NET runtime identifier for the current machine:

```powershell
./tests/WebUIToolkit.MVVM.AotSmoke/Test-PackageAot.ps1 -RuntimeIdentifier win-x64
```

The script packs `WebUIToolkit.MVVM`, restores this executable through a `PackageReference`, publishes a native executable, runs it, and removes its temporary package and publish directories.
