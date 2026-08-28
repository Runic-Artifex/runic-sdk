# .NET platform evidence matrix

W90 defines one process-isolated lane per supported .NET desktop profile. CI is
split deliberately: Linux is the routine push/pull-request gate, an explicit
manual dispatch runs only the three hosted Windows/macOS lanes, and a `v*`
release tag runs both. This avoids spending hosted macOS capacity on ordinary
commits or rerunning Linux during one-off native certification.
Routine Linux jobs are capped at ten minutes and native jobs at five minutes so
a wedged platform process cannot consume the hosted runner's six-hour default.

The Linux gate runs the complete sequence:

1. restore and Release-build the solution;
2. run the complete managed behavioral and conformance-receipt suite;
3. pack `Runic.Desktop` and restore a clean consumer with no project reference;
4. build and run that consumer, publish it with NativeAOT for the lane RID, and
   run the native executable; and
5. launch the embedded WebView smoke as a separate process.

Native Windows/macOS certification restores, builds, and launches the platform
WebView smoke. It does not repeat the managed suite or the platform-neutral
package/NativeAOT journey already proved by the Linux gate.

| Profile | Runner | Browser/WebView process evidence | Exact package / NativeAOT |
| --- | --- | --- | --- |
| Linux x64 | `ubuntu-24.04`, Xvfb, WebKitGTK 4.1 | `Runic.Desktop.WebViewSmoke` under `xvfb-run` | `verify-local-package.sh linux-x64` |
| Windows x64 | `windows-2022`, WebView2 | separate `dotnet run` process | not repeated |
| macOS Intel | `macos-15-intel`, system WKWebView | separate `dotnet run` process | not repeated |
| macOS Arm64 | `macos-14`, system WKWebView | separate `dotnet run` process | not repeated |

The Linux gate uploads its TRX output even on failure. The smoke executable uses
only the public M6 host/surface/window API and checks native handle creation,
bridge JavaScript execution, focus, geometry, minimize/maximize, close, and a
second surface/window lifetime.

## Local W90 execution

On 2026-08-28, the Linux x64 managed and receipt suite passed 46/46. The
focused API/framing/reconnect set also passed 8/8 while the receipt was being
bound. The exact `Runic.Desktop.0.1.0-w90.nupkg` clean consumer restored,
built, executed through Kestrel, published with NativeAOT, and the produced
native Linux x64 executable executed the same HTTP journey successfully. The
process-isolated WebKitGTK smoke passed under Xvfb, including bridge
authentication, JavaScript, native-window operations, close, and restart.

Windows and both macOS runtime results are intentionally produced only on their
native hosted runners; cross-compilation on Linux is not presented as WebView
runtime evidence. A candidate is incomplete until all four required CI lanes
are green for the same commit.
