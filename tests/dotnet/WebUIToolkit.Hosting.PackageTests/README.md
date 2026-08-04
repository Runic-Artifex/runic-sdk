# Hosting package-consumer verification

This executable verifies the public Hosting surface from locally packed NuGet packages. It deliberately has no `ProjectReference`; the core, Generic Host adapter, abstractions, build, generator, and WebUi packages are pinned package references.

From the repository root, run:

```powershell
pwsh -NoProfile -File tests/WebUIToolkit.Hosting.PackageTests/Test-PackageConsumer.ps1
```

Use `-SkipNativeAot` only for the G3 managed package boundary when the exact SDK
runtime and ILCompiler packs are unavailable. The default remains the full G4
package-consumer check and still requires Native AOT.

The runner packs the owned shipping projects into the ignored `.packages/hosting-wave-c` feed and checks DLL/XML-documentation/README payloads and dependency direction. It also proves that the core and its Generic Host and WebUI adapters remain independent of `Microsoft.AspNetCore.App`, while each adapter declares only the Microsoft.Extensions packages it uses. It clears and uses an isolated ignored consumer cache so a stale global package cannot satisfy the test. It then restores, builds, and runs the managed consumer and publishes and runs a NativeAOT executable. The test-local NuGet configuration maps `WebUIToolkit.Hosting*` exclusively to that feed. SDK and AOT tooling may resolve from nuget.org.

The executable uses only package APIs and BCL fakes. Its scenarios cover the composition builder, launch classification and mode routing, sanitized lifecycle events, frontend assets, deterministic manifest building/serialization, and neutral browser-host seams without referencing MVVM, command-line, or `cs-webui` implementations.
