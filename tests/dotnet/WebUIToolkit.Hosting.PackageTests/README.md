# Hosting package-consumer verification

This executable verifies the public Hosting surface from locally packed NuGet packages. It deliberately has no `ProjectReference`; `WebUIToolkit.Hosting`, `WebUIToolkit.Hosting.Abstractions`, and `WebUIToolkit.Hosting.Build` are package references pinned to `1.0.0`.

From the repository root, run:

```powershell
pwsh -NoProfile -File tests/WebUIToolkit.Hosting.PackageTests/Test-PackageConsumer.ps1
```

When the owned package contents intentionally change, refresh the committed TFM-only lock once with `-RefreshPortableLock`, review that lock, then rerun the command above without the switch to prove ordinary locked restore.

The runner packs the three owned shipping projects with stable revision metadata into the ignored `.packages/hosting-wave-b` feed, normalizes package-container metadata and timestamps, checks DLL/XML-documentation/README payloads and dependency direction, and verifies the feed packages' SHA-512 values against the portable lock. It clears and uses an isolated ignored consumer cache so a stale global package cannot satisfy the test. It then performs a portable locked restore/build/run and publishes and runs a Windows x64 Native-AOT executable. The test-local NuGet configuration maps `WebUIToolkit.Hosting*` exclusively to that feed. SDK and AOT tooling may resolve from nuget.org. The RID restore uses ignored `obj/aot.packages.lock.json`; it never adds a RID section to the committed portable `packages.lock.json`.

The executable uses only package APIs and BCL fakes. Its scenarios cover the composition builder, launch classification and mode routing, sanitized lifecycle events, frontend assets, deterministic manifest building/serialization, and neutral browser-host seams without referencing MVVM, command-line, or `cs-webui` implementations.
