# WebUIToolkit.Collections package-consumer smoke

This executable packs `WebUIToolkit.Collections` into a temporary local feed and
consumes version `1.0.0` from a new, isolated project. It validates package
metadata and assets before restoring exclusively from that feed, building, and
running a program that compiles against the complete Wave A public API.

Run the managed compatibility check from the repository root:

```powershell
dotnet run --project tests/WebUIToolkit.Collections.PackageConsumer -c Release
```

Also publish and run the temporary consumer as a Native-AOT executable for the
current runtime identifier:

```powershell
dotnet run --project tests/WebUIToolkit.Collections.PackageConsumer -c Release -- --aot
```

The AOT restore writes `obj/aot.packages.lock.json` inside the disposable
consumer directory. The committed harness lock therefore remains portable and
RID-free. Its source mapping still requires `WebUIToolkit.Collections` to come
from the temporary feed; when the installed SDK does not include Native-AOT
packs, only Microsoft/runtime toolchain packages may come from NuGet.org. Use
`--keep-temp` to retain the generated consumer for diagnosis.
