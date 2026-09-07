# NativeAOT desktop size comparison

This experiment compares two matched minimal desktop applications:

- `CsWebUiBaseline` uses cs-webui and its native WebUI library.
- `RunicDesktopBaseline` uses Runic Desktop and its ASP.NET Core/Kestrel host.

Both compile the same HTML, open it in an installed browser, perform the same
JavaScript-to-.NET `ping` call, verify the returned value in JavaScript, notify
the backend of completion, and close automatically.

`AspNetCoreDiagnostic` is not a third comparison candidate. It is a diagnostic
Minimal API host used only to estimate how much of the Runic executable is the
ASP.NET Core NativeAOT floor.

## Run on Linux x64

From the `runic-sdk` repository, with the independent CS-WebUI source checkout
and its Linux native library already available:

~~~sh
CS_WEBUI_REPOSITORY="/absolute/path/to/cs-webui" nix develop --command \
  ./tests/fixtures/application/experiments/native-aot-size/compare-linux-x64.sh
~~~

The script uses .NET SDK 10.0.302 and identical Release, NativeAOT, full
trimming, invariant-globalization, size-optimization, and symbol-stripping
settings. It writes each run beneath:

~~~text
artifacts/native-aot-size/linux-x64/run.*
~~~

Each run contains the three publish directories, full and normalized compressed
artifacts, publish logs, file inventories, measurements, and environment/source
revisions. The normalized runtime payload includes the cs-webui executable and
`libwebui-2.so`, and the Runic Desktop executable. It excludes XML documentation
and native binaries for other RIDs that cannot participate in a Linux run.

The script smoke-runs both primary binaries under Xvfb after publishing. Set
`RUNIC_NATIVE_AOT_SKIP_SMOKE=1` only when the environment cannot launch an
installed browser.

Smoke the two primary binaries under a virtual display with:

~~~sh
nix develop --command \
  xvfb-run -a artifacts/native-aot-size/linux-x64/<run-directory>/cs-webui/DesktopSizeBaseline

nix develop --command \
  xvfb-run -a artifacts/native-aot-size/linux-x64/<run-directory>/runic-desktop/DesktopSizeBaseline
~~~

Select one concrete `run.*` directory when more than one result exists.

The comparator requires the external checkout explicitly; it never discovers a
retired sibling repository. Results belong under the SDK root even when invoked
from another directory. Each result includes both source revisions, working-tree
status, and whether browser verification was skipped.

The September 3 result is historical evidence, not a new measurement of the
reorganised SDK. See [host choice and footprint](../../../../../docs/guides/desktop/host-choice-and-footprint.md)
for the parity assessment and proposed tuning experiments.
