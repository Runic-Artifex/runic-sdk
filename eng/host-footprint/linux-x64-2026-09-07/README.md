# Linux x64 package-consumer footprint — 2026-09-07

Source: `c274ca5c7f8bf4efd99d45340d727e68233a1a1b`; clean working tree at measurement start.
NixOS 26.11 (Zokor), .NET SDK 10.0.302 / runtime and AOT compiler 10.0.10.
The repository Nix shell supplied the native toolchain and Chromium.

All three NativeAOT applications passed the same customer browser acceptance:
validation, save, controlled cancellation before persistence, unsaved-navigation
and close policy, reconnect, import, uniqueness and responsive layout. Each also
passed five fresh-process lifecycle cases, including active browser shutdown.
The cancellation test uses the sample's explicit headless save gate, so it cannot
accidentally measure a save that finished before cancellation was requested.

| Host / profile | Executable | Executable + required native libraries | Complete distribution | ZIP |
| --- | ---: | ---: | ---: | ---: |
| desktop / default | 10.42 MiB | 10.42 MiB | 11.16 MiB | 5.03 MiB |
| desktop / minimal | 9.42 MiB | 9.42 MiB | 10.16 MiB | 4.57 MiB |
| cswebui / default | 5.16 MiB | 5.47 MiB | 5.47 MiB | 2.64 MiB |

Minimal Desktop reduces this application's executable by **9.6%**
(1,054,016 bytes). CS-WebUI includes its 328,520-byte native WebUI
library in the runtime payload. Desktop's full distribution additionally contains
WebView2 documentation and a Windows loader that cannot participate in this Linux
run; neither is subtracted from the complete distribution or ZIP numbers.

All candidates use Release, self-contained NativeAOT, size optimization,
invariant globalization and stripped symbols, with AOT warnings treated as
errors. The invariant-culture choice is fixture-specific, not an SDK default.
Both Desktop profiles passed 69 conformance tests. The measured app embeds the
same frontend built in isolation from candidate npm archives; its own domain and
application projects use candidate NuGet SDK packages. The CS-WebUI dependency
graph contains neither Runic Desktop nor ASP.NET Core.

## Evidence and reproduction

The JSON reports retain every byte count, file hash, evaluated setting, library
version, build environment and verification result. `matrix.json` records exact
NuGet/npm candidate hashes and the generated frontend lockfile hash. SDK candidates
were built from commit `63602b17`; Desktop was repacked from `c274ca5c`
after its bounded profile-cleanup fix. Customer application sources are from the
clean revision above. No package was published.

Only absolute checkout and consumer paths were replaced with `$SDK_ROOT` and
`$CONSUMER_ROOT` in these receipts. No measurements, hashes or verification results
were changed. Complete archives, the frontend lockfile and original reports remain
under `artifacts/host-footprint/linux-x64/run-1788778087971`. Large binaries are not committed.

Reproduce with `CONFIGURATION=Release bun run pack`, then
`CONFIGURATION=Release bun run verify:footprint` inside `nix develop`. The matrix
creates new directories and retains prior runs. Publish and verification logs
are included here. Missing-favicon 404 messages are browser diagnostics; the
application assertions and graceful host exit checks passed.

These are NixOS Linux x64 measurements, not cross-platform certification or the
older minimal sample's size. Browser/OS libraries are excluded. Nix native loader
paths are not a portable deployment promise for arbitrary Linux hosts. File size
does not measure startup time or process-tree memory.

The four-RID CI matrix is configured but was still queued for this revision when
the receipts were prepared. Earlier native jobs passed on Linux and Windows;
macOS jobs were superseded before completion. No macOS result is claimed.
