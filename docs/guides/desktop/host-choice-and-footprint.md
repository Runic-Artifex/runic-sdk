# Host choice and historical footprint assessment

The September 2026 assessment on this page is historical context, not an active
implementation plan. Current host capabilities and platform limits are documented
in [host selection](host-selection.md) and the package READMEs.

Runic Application Views declares Window and View contracts independently from a
presentation host. The current template integration targets CS-WebUI. Runic
Desktop is a separate library for installed-browser and embedded-WebView
presentations; it does not make the CS-WebUI integration switch hosts implicitly.

## Historical Linux footprint measurement

The [September 3 Linux measurement](../../../tests/fixtures/application/experiments/native-aot-size/results/linux-x64-2026-09-03.md)
reported runtime payloads of **1.79 MiB for CS-WebUI** and **8.25 MiB for Desktop**,
with the installed browser excluded from both. A separate slim ASP.NET Core
Minimal API diagnostic was **7.46 MiB**. These are historical measurements for
one RID and one source configuration, not fresh results or a guaranteed minimum
size. The diagnostic is a different program, so subtraction is only an
attribution hint.

The comparison enabled NativeAOT, full trimming, size optimization, invariant
globalization and stripped symbols. The old result also found 1.23 MiB of XML
documentation and other-RID WebView2 loaders in the complete Desktop publish
directory. Those files were already excluded from the 8.25 MiB runtime number;
cleaning the distribution can reduce download/disk size without reducing that
executable measurement.

For current size methodology and results, use
[size reporting and tuning](size-and-tuning.md) and the linked historical
measurement fixture.
