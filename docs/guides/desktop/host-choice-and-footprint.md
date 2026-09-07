# Host choice and footprint

Assessment: 2026-09-07. The intended product direction is equal access to Runic's
application development experience through CS-WebUI and Runic Desktop. Choose
Desktop for its additional hosting and presentation features. Choosing CS-WebUI
should not require replacing application members, generated frontend contracts,
or the development workflow. This document records the original assessment. The first implementation waves
are described in [host selection](host-selection.md); footprint experiments below
remain the work plan until accompanied by new measurements.

## What the current code provides

| Developer concern | Current state | Required outcome |
| --- | --- | --- |
| Application lifecycle and composition | `Runic.Application` is independent of Desktop; `UseDesktop` is the available desktop application integration | A CS-WebUI integration using the same `IApplicationHost` lifecycle |
| Typed members, actions, state and validation | Bridge runtime/generators do not reference Desktop; `DesktopApplicationBridge` connects the protocol to Desktop sessions | The same generated model and bridge conformance on both hosts |
| Window and callback APIs | CS-WebUI has `WebUiWindow`, synchronous/asynchronous binding and native WebUI transport | Preserve direct CS-WebUI access while offering Runic application composition |
| Templates and frontend development | Maintained application templates select `UseDesktop` | Explicit host selection with equivalent React, Vue, Svelte and Angular workflows |
| Testing and migration | Customer migration sample runs through Desktop | Run the same business rules, generated contract and frontend acceptance on both hosts |
| Host enhancements | Desktop owns Kestrel hosting, isolated surfaces, presentation preflight, fallback policy and native close coordination | Document capabilities and report unsupported requests before opening a window |
| Packaging | CS-WebUI remains independently packaged and maintained | SDK integration consumes CS-WebUI packages; selecting it must not pull in Desktop or ASP.NET Core |

Evidence: [Desktop composition](../../../packages/dotnet/Runic.Application.Desktop/DesktopApplicationHost.cs),
[Desktop bridge transport](../../../packages/dotnet/Runic.Application.Desktop/DesktopApplicationBridge.cs),
[Application dependencies](../../../packages/dotnet/Runic.Application/Runic.Application.csproj),
[bridge dependencies](../../../packages/dotnet/Runic.Application.Bridge/Runic.Application.Bridge.csproj),
and [CS-WebUI high-level sample](https://github.com/Runic-Artifex/cs-webui/blob/main/samples/CsWebUi.HighLevelSample/Program.cs).

## First implementation wave: prove interchangeable application hosts

Add an SDK integration package, provisionally `Runic.Application.CsWebUi`, with
`UseCsWebUi` composition. It should translate transport and lifecycle operations
into the existing Runic application protocol. It must preserve member generation,
action cancellation, validation, state subscriptions, reconnect epochs, disposal
and errors. CS-WebUI remains an independent lower-level library. No MVVM or
CommunityToolkit compatibility layer is involved.

Reuse common protocol/session policy where appropriate rather than duplicating
Desktop's security-sensitive admission and reconnect logic. Native WebUI callback
identities and disconnect notifications need an explicit mapping to Runic sessions.
Before exposing hosted content, establish origin/admission rules, bounded messages,
callback cancellation and deterministic teardown for the CS-WebUI transport.

Acceptance requires the counter and customer editor to switch host without changing
business rules, generated application contract, or UI components. A host-specific
startup/bootstrap file and package reference may differ. Verify validation,
async save/cancel, reconnect, and shutdown under a real browser. Where a native
window feature is unavailable, report a capability result and provide an explicit
application-level alternative. Do not silently claim native close veto support.

Then add host selection to templates and the development CLI, run both hosts in
the package/template matrix, and verify that the CS-WebUI publish dependency graph
contains neither `Runic.Desktop` nor `Microsoft.AspNetCore.App`. Desktop remains a
first-class choice; migration between hosts should be a composition change.

## What the 8 MiB measurement means

The [September 3 Linux measurement](../../../tests/fixtures/application/experiments/native-aot-size/results/linux-x64-2026-09-03.md)
reported runtime payloads of **1.79 MiB for CS-WebUI** and **8.25 MiB for Desktop**,
with the installed browser excluded from both. A separate slim ASP.NET Core
Minimal API diagnostic was **7.46 MiB**. These are historical measurements for one
RID and one source configuration, not fresh results or a guaranteed minimum size.
The diagnostic is a different program, so subtraction is only an attribution hint.

The comparison already enables NativeAOT, full trimming, size optimization,
invariant globalization and stripped symbols. Desktop's
[presentation host](../../../packages/dotnet/Runic.Desktop/Internal/PresentationHostCore.cs)
already creates a slim builder and clears logging providers. Suggesting those
same settings again cannot establish further savings.

The old result also found 1.23 MiB of XML documentation and other-RID WebView2
loaders in the complete Desktop publish directory. Those files were already
excluded from the 8.25 MiB runtime number. Cleaning the distribution can reduce
download/disk size without reducing that executable measurement.

## Tuning guidance and experiments

For application authors, enable `PublishAot` and evaluate
`OptimizationPreference=Size` against their workload. Keep build/linker warnings
visible and test the actual published artifact. Culture-sensitive applications
must assess invariant globalization explicitly; it should not become a blanket
SDK default, particularly for applications using translations.

Microsoft documents both slim and empty builders. The latter leaves configuration
to the application. Investigate a Desktop opt-in profile that registers only its
required Kestrel, WebSocket and lifetime services; preserve listener isolation,
admission, origin checks, request limits and shutdown. Compare it to today's
slim host before promising savings. Applications cannot substitute Desktop's
internally owned builder today. Merely deleting configuration providers after
registration does not demonstrate that their implementation was trimmed.
[ASP.NET Core NativeAOT guidance](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0).

Keep diagnostics and culture support as explicit tradeoffs. Disabling stack-trace
support reduces diagnostic information; invariant globalization changes culture
behavior. Inspect evaluated feature-switch values before recommending changes:
some size-related settings are already enabled by NativeAOT. Vary one switch at
a time and retain a useful diagnostic build. Do not suppress trimming warnings
to make a size profile pass.
[.NET trimming options](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trimming-options).

## Tooling proposal: explain and verify size

Extend the repaired [comparison harness](../../../tests/fixtures/application/experiments/native-aot-size/README.md)
into a package-consumer matrix before adding a user-facing `runic size` command.
The command name is illustrative; it does not exist yet.

The report should separate main executable, required native dependencies, frontend
assets, debug symbols, documentation, other-RID files, total distribution and
compressed distribution. Record SDK/runtime, RID, package versions, effective
publish settings, source revision and whether behavior verification passed.
For NativeAOT executable attribution, investigate compiler dependency/size reports;
an assembly inventory alone cannot explain linked native code size.

Run the same browser roundtrip and application bridge acceptance for the default
Desktop host, any minimal Desktop profile, and CS-WebUI. Keep the bare ASP.NET
Core host a separate diagnostic. Include warning output and compare artifacts
from clean package consumers. Measure startup and process-tree memory separately;
executable size is neither metric. Test Windows x64, Linux x64 and both macOS
architectures before making cross-platform claims.

The immediate deliverable is a measured tradeoff table and recommended profiles,
with functionality retained and removed stated for each. No smaller Desktop
binary or new ASP.NET Core reduction is claimed by this assessment.

## Assessment verification

Both relocated desktop comparator projects compile in Release with explicit
CS-WebUI source selection and no warnings (managed builds with AOT/trimming
switched off for this check). The shell script passes `bash -n`, and documentation
links resolve. This verifies build inputs and harness syntax, not new NativeAOT
size results. The previous local Nix NativeAOT worker issue remains recorded in
[reorganisation verification](../../../eng/migration/reorganisation-verification.md).
