# WebUIToolkit.MVVM.Build executable contracts

This project is a dependency-free console test harness for the binding compiler
and its build integration. Run it from the repository root:

```console
dotnet restore tests/WebUIToolkit.MVVM.Build.Tests/WebUIToolkit.MVVM.Build.Tests.csproj --locked-mode -p:WebUIToolkitLocalPackageSource=<packed-feed>
dotnet run --project tests/WebUIToolkit.MVVM.Build.Tests/WebUIToolkit.MVVM.Build.Tests.csproj --configuration Release --no-restore
```

The packed feed must contain `WebUIToolkit.MVVM` version `1.0.0`; repository
orchestration creates it from the protocol/runtime owner before this project is
restored. The test project never uses a cross-owner project reference.

Every contract prints one `PASS` or `FAIL` line. The final line is stable and
machine readable: `TOTAL <total> PASSED <passed> FAILED <failed>`.

The committed NuGet lock is intentionally RID-free. Native-AOT smoke publishing,
when needed, must use an ignored intermediate lock rather than changing this file.
