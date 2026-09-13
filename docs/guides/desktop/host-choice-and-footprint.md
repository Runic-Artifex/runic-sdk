# Host choice and historical footprint assessment

The September 2026 assessment on this page is historical context, not an active
implementation plan. Current host selection and platform limits are documented in
[host selection](host-selection.md) and the package READMEs.

`Runic.Application` and the Application Bridge are host-neutral. The maintained
templates and migration examples select a host at composition time while retaining
their domain services, generated bridge contract and frontend components.

| Host | Composition | Boundary |
| --- | --- | --- |
| Runic Desktop | `UseDesktop` | Embedded or browser presentation, Desktop window lifecycle and optional native integration |
| CS-WebUI | `UseCsWebUi` | Independently packaged runtime with no Desktop or ASP.NET Core dependency; native close veto is unavailable |
| Generic/local web host | `Runic.Application.Hosting` | Explicit application-owned Generic Host lifetime or local Application Bridge WebSocket endpoint |

CS-WebUI remains independently usable for direct WebUI APIs. The Runic host adapter
owns its application lifecycle, bridge session, loopback binding and per-host
callback admission; it is not a Desktop compatibility layer. A selected host owns
the presentation transport while the bridge retains revisions, cancellation,
validation and recovery semantics.

## Historical Linux footprint measurement

The [September 3 Linux measurement](../../../tests/fixtures/application/experiments/native-aot-size/results/linux-x64-2026-09-03.md)
reported runtime payloads of **1.79 MiB for CS-WebUI** and **8.25 MiB for Desktop**,
with the installed browser excluded from both. A separate slim ASP.NET Core
Minimal API diagnostic was **7.46 MiB**. These are historical measurements for one
RID and one source configuration, not fresh results or a guaranteed minimum size.
The diagnostic is a different program, so subtraction is only an attribution hint.

The comparison enabled NativeAOT, full trimming, size optimization, invariant
globalization and stripped symbols. The old result also found 1.23 MiB of XML
documentation and other-RID WebView2 loaders in the complete Desktop publish
directory. Those files were already excluded from the 8.25 MiB runtime number;
cleaning the distribution can reduce download/disk size without reducing that
executable measurement.

For current size methodology and results, use
[size reporting and tuning](size-and-tuning.md) and the linked measurement fixtures.
