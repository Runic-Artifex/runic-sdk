# Upstream cs-webui Native-AOT feasibility record

Status: **feasible for the Wave A contract kernel on Windows x64, with upstream integration risks listed below**.

Recorded on 2026-07-22 from an isolated temporary tree. Nothing in the upstream checkouts or probe output is a repository dependency. This record distinguishes source facts, executed evidence, failed attempts, and work that is still required for a release gate.

## Pins

| Component | Immutable revision or version | How it was selected |
| --- | --- | --- |
| External `cs-webui` wrapper | `f706387ae8ac62fe1ad2cfc011dc088751c8f556` | Remote `HEAD` of `https://github.com/ViktorJannicke/cs-webui.git` on 2026-07-22, then used by full SHA |
| External WebUI native source | `6c561ed739ce415e1e48ed17cb67c880aff1dc9d` | `cs-webui`'s committed `flake.lock`/`flake.nix` pin |
| `cs-webui` managed package version in source | `2.5.0-beta.4.2` | `Directory.Build.props` |
| WebUI C ABI claimed by the binding | `2.5.0-beta.4` | `README.md` and `WebUiConstants.Version` |
| .NET SDK used | `10.0.302` | `dotnet --version`; upstream requests `10.0.100` with `latestFeature` roll-forward |
| Browser used for the round trip | Microsoft Edge `150.0.4078.83` | Installed Windows x64 browser, launched headlessly |

The wrapper commit is the pin for this record. A later remote `HEAD` is not implicitly accepted.

## Conclusion

The actual upstream sources satisfy the narrow feasibility question exercised here:

1. Both upstream libraries build for `net10.0` with their AOT and trimming declarations.
2. A source-published `win-x64` Native-AOT executable loads a WebUI DLL built from the wrapper's exact native-source pin and executes native ABI calls.
3. A separate high-level probe publishes with full trimming/AOT analyzers and warnings as errors.
4. That published native executable starts a WebUI server; headless Edge loads its HTML and `webui.js`; JavaScript calls the managed `roundTrip` binding; managed code reads `native-aot`, returns `ack:native-aot`, and exits zero after deterministic cleanup.

This is stronger than compile-only evidence. It does not make upstream `cs-webui` part of `RunicToolkit.MVVM`: the neutral protocol/session library remains BCL-only, and a future host adapter owns the external dependency.

## Verified source facts

- The external native and high-level `cs-webui` libraries target `net10.0`; the repository enables unsafe code, warnings as errors, .NET analyzers, and deterministic builds.
- Both libraries declare `IsAotCompatible=true` and `IsTrimmable=true`. The native layer also declares `DisableRuntimeMarshalling=true`.
- The high-level library has no external managed runtime package dependency; it project-references the native binding. Test packages are not runtime dependencies.
- Interop uses source-generated `LibraryImport` methods, explicit Cdecl calling conventions, and unmanaged function pointers. The high-level callback trampoline is `UnmanagedCallersOnly`; registration is not reflection-discovered.
- The external native-library resolver uses an explicit `NativeLibrary.SetDllImportResolver`, accepts its documented environment variable or explicit path setter, and otherwise permits normal runtime-native-asset resolution.
- The high-level API exposes sync/async bindings, `StartServer`, browser/WebView show methods, JavaScript execution, raw data, close, disposal, and application cleanup.
- Window disposal cancels its shutdown token and defers native destruction until active managed callbacks drain.
- The wrapper's native workflow builds five 64-bit assets: `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. Its standard build disables TLS and Windows requests the static MSVC runtime.
- The wrapper, pinned WebUI source, and Runic Toolkit are MIT licensed. The wrapper
  `NOTICE` attributes WebUI; dependency and notice review remains independently required.

## Executed evidence

The evidence index is [evidence.md](evidence.md), and the exact command transcript and selected output are in [commands-and-results.txt](commands-and-results.txt). The probe source is preserved under [probe](probe/README.md), parameterized by an external checkout path so the evidence directory does not vendor or depend on upstream sources.

| Check | Result |
| --- | --- |
| Remote wrapper pin and clean clone | `f706387...`, clean checkout |
| Upstream restore | exit `0`, six projects restored |
| Upstream Release build | exit `0`, 0 warnings, 0 errors |
| Upstream managed tests without native library | exit `0`, 19 passed, 3 explicitly skipped native tests |
| Pinned native WebUI configure/build/install | exit `0`/`0`/`0`; `webui-2.dll` produced |
| Wrapper ABI export validation | exit `0`, 111 WebUI exports validated |
| Native DLL dependency check | only Windows system DLLs; no `VCRUNTIME`, `MSVCP`, or dynamic UCRT dependency reported |
| Upstream tests with the built DLL | exit `0`, 22 passed, 0 skipped |
| Upstream low-level Native-AOT publish | exit `0`, `Generating native code`, no warning |
| Upstream low-level published binary | exit `0`, `WebUI allocated port 16058.` |
| High-level full-trim Native-AOT publish | exit `0`, `Generating native code`, no warning |
| High-level browser/managed callback | exit `0`, callback received `native-aot`, success marker emitted |

The locally built `webui-2.dll` was 543,744 bytes with SHA-256 `5954D96895ED3F2614DDD17DA9CE5A34B8FC22F70EB854CCBF7B833EBFB33AAA`. The final high-level native executable built from the preserved probe was 1,410,560 bytes with SHA-256 `C19BB8EE4FC30BE1D323E839C52DBC1652FFB4EA17DBF86B21A91AF4140E9D68`. These hashes identify this run's evidence; they are not asserted as cross-machine deterministic build contracts.

## Failed attempts and environment limitations

- `cmake`, `ninja`, `cl`, and `link` were not on the ordinary PowerShell `PATH`. Visual Studio Enterprise 2026 contained the required tools, so the probe invoked its bundled CMake explicitly. CMake selected MSVC 19.51 and Windows SDK 10.0.28000.0.
- The native CMake build emitted MSB8029 warnings because its intermediate directory was below `%TEMP%`. This is a temporary-directory incremental-build warning, not a source/AOT warning; configure, compile, and install succeeded.
- Before the external native-library path environment variable was set, upstream's three native smoke tests were explicitly skipped. That run verified managed tests only and is not counted as native-runtime proof.
- Two Edge invocations using `--dump-dom` exited before WebUI's WebSocket connection completed. The probe timed out and returned `3`; no managed callback occurred. Keeping headless Edge alive with a remote-debugging endpoint allowed the connection and round trip to complete. The observed result suggests `--dump-dom` lifetime was too short, but that cause was not independently proven.
- The environment has no interactive user validation in this record. The successful browser was headless Edge; visible-window and embedded WebView behavior were not exercised.

## Remaining blockers and risks

1. Upstream source checkouts contain no tracked native binaries. A local pack can omit native assets unless `RequireNativeAssets=true`; complete packages require the native build matrix or a supplied `NativeAssetsRoot`.
2. The ABI follows a beta WebUI revision. Every update needs a new immutable pin, export/signature/layout validation, and runtime matrix. The current export script verifies 111 names, not every C type/layout semantic.
3. This local run proves Windows x64 only. Upstream CI's checked-in AOT smoke runs low-level interop on Linux x64, but high-level browser callbacks and other RIDs are not equivalent evidence.
4. Standard packaged native assets are non-TLS. TLS requires a custom WebUI/OpenSSL build and separate tests.
5. No assets are declared for Windows ARM64/x86, musl, or other unlisted RIDs.
6. Browser mode requires an installed supported browser. Embedded WebView mode adds platform prerequisites such as WebView2 on Windows and GTK/WebKit on Linux.
7. The out-of-tree probe proves the integration seam but is not an upstream regression test. A release gate should preserve a package-consumer version of the round trip and run the published binary for every supported target.
8. This probe exercises a framework-neutral string callback, not the RunicToolkit MVVM protocol corpus, session revisions, cancellation, reconnect, or frontend adapters. Those remain downstream gates.
9. Dependency attribution and redistribution terms still require the repository's
   formal notices and SBOM review even though ADR 0014 licenses Runic Toolkit under MIT.

## Adoption decision for Wave A

Treat upstream Native-AOT compatibility as **verified enough to proceed with the neutral protocol/session kernel**, not as G4 release acceptance. Keep `cs-webui` behind an explicit host adapter, pin the wrapper and native ABI independently, require native assets during pack, and retain the published-binary callback probe as a future package-consumer gate.
