![CS-WebUI banner](https://raw.githubusercontent.com/Runic-Artifex/cs-webui/main/.github/assets/brand/banner.png)

<div align="center">

![CS-WebUI logo](https://raw.githubusercontent.com/Runic-Artifex/cs-webui/main/assets/webui_csharp.png)

# CS-WebUI

[last-commit]: https://img.shields.io/github/last-commit/Runic-Artifex/cs-webui?style=for-the-badge&logo=github&logoColor=C0CAF5&labelColor=414868
[release-version]: https://img.shields.io/github/v/tag/Runic-Artifex/cs-webui?style=for-the-badge&logo=webtrees&logoColor=C0CAF5&labelColor=414868&color=7664C6
[nuget-version]: https://img.shields.io/nuget/vpre/CsWebUi?style=for-the-badge&logo=nuget&logoColor=C0CAF5&labelColor=414868&color=7664C6
[license]: https://img.shields.io/github/license/Runic-Artifex/cs-webui?style=for-the-badge&logo=opensourcehardware&label=License&logoColor=C0CAF5&labelColor=414868&color=8c73cc

[![][last-commit]](https://github.com/Runic-Artifex/cs-webui/pulse)
[![][release-version]](https://github.com/Runic-Artifex/cs-webui/releases/latest)
[![][nuget-version]](https://www.nuget.org/packages/CsWebUi)
[![][license]](https://github.com/Runic-Artifex/cs-webui/blob/main/LICENSE)

> Use any web browser or supported WebView as a GUI, with .NET in the backend
> and HTML5 in the frontend, all in lightweight cross-platform packages.

![WebUI screenshot](https://raw.githubusercontent.com/webui-dev/webui-logo/main/screenshot.png)

</div>

Build lightweight, cross-platform .NET desktop interfaces with ordinary HTML,
CSS, and JavaScript. CS-WebUI opens your UI in an installed browser or a
supported WebView, while your application stays in .NET.

CS-WebUI provides .NET 10 bindings for
[WebUI](https://github.com/webui-dev/webui). Choose `CsWebUi` for deterministic
window ownership, UTF-8 conversion, error handling, raw-data helpers, and safe
synchronous or `ValueTask`-based callbacks. Choose `CsWebUi.Native` only when
you need the complete unsafe WebUI 2.5 C ABI directly.

An independent managed engine is also being developed in `CsWebUi.Managed`.
The current source preview uses Kestrel for HTTP and WebSocket hosting and does
not load the WebUI native library. Its M1 bridge and binding layer now speaks
WebUI's binary protocol in real browsers, including events, JavaScript calls,
raw data, chunking, and reconnect behavior. See the
[managed port roadmap](docs/design/managed-webui-port-roadmap.md) for its
implemented vertical slice, compatibility milestones, and intentional gaps.

> CS-WebUI follows WebUI's 2.5 beta ABI and is currently released as a
> prerelease package.

## Why CS-WebUI

- Portable: use an installed browser or a supported embedded WebView.
- Lightweight native runtime with WebUI's fast binary communication protocol.
- Windows, Linux, and macOS packages for x64 and Arm64 where available.
- Complete low-level C ABI plus an idiomatic, ownership-safe managed API.
- Synchronous and asynchronous JavaScript-to-.NET bindings.
- Trimming and NativeAOT-oriented, including optional static linking on
  Windows x64.
- Policy-free custom HTTP responses for asset and framework integrations.
- Official WebUI native binaries, pinned and verified during packaging.

## Get started

```bash
dotnet add package CsWebUi --prerelease
```

Use `CsWebUi.Native` instead when an application needs the direct C ABI:

```bash
dotnet add package CsWebUi.Native --prerelease
```

`CsWebUi` references the native package for you. Both packages target .NET 10
and currently ship prerelease builds that follow the WebUI 2.5 beta ABI.

## Minimal example

```csharp
using CsWebUi;

using var window = new WebUiWindow();

window.Bind("multiply", static e =>
    WebUiResult.FromInt64(e.GetInt64() * e.GetInt64(1)));

window.Show("""
    <!doctype html>
    <script src="webui.js"></script>
    <button onclick="multiply(6, 7).then(alert)">Multiply</button>
    """);

WebUiApplication.Wait();
```

`WebUiWindow.Dispose()` destroys the native window and safely defers final
destruction until active managed callbacks finish. Async bindings
automatically opt WebUI into its asynchronous-response mode; return a
`WebUiResult` to resolve the JavaScript promise.

## Documentation and examples

- [`CsWebUi.BasicSample`](samples/CsWebUi.BasicSample) is the smallest complete
  callback example.
- [`CsWebUi.HighLevelSample`](samples/CsWebUi.HighLevelSample) demonstrates the
  safe window, event, async callback, binary-message, and JavaScript APIs.
- [`UpstreamExamples`](samples/UpstreamExamples) contains C# ports of every
  distinct C and C++ example in the pinned WebUI source tree, including a
  stage-timed stress test and a C-versus-NativeAOT comparison harness.
- [WebUI documentation](https://webui.me/docs/) covers the native concepts,
  browsers, WebViews, and JavaScript bridge shared by all language wrappers.
- [Runic Assets](https://github.com/Runic-Artifex/runic-assets) provides Vite
  packing, embedded assets, development refresh, cache policy, and direct
  `CsWebUi` package delivery.
- [Report an issue](https://github.com/Runic-Artifex/cs-webui/issues) or start
  with the [repository documentation](https://github.com/Runic-Artifex/cs-webui#readme)
  when you need help choosing a browser, WebView, or hosting model.

## Supported platforms

| Runtime | Browser mode | Embedded WebView mode | Bundled native library |
| --- | --- | --- | --- |
| Windows x64 | Yes | WebView2 | `webui-2.dll` |
| Linux x64 | Yes | WebKitGTK | `libwebui-2.so` |
| Linux Arm64 | Yes | WebKitGTK | `libwebui-2.so` |
| macOS x64 | Yes | WebKit | `libwebui-2.dylib` |
| macOS Arm64 | Yes | WebKit | `libwebui-2.dylib` |

Browser discovery and support are provided by WebUI. Embedded mode requires
the platform WebView runtime; browser mode needs a supported installed browser.

## Custom file responses

`WebUiWindow.SetFileHandler` is the policy-free managed wrapper over WebUI's
native per-window custom file handler. It receives WebUI's cleaned, URL-decoded
path and returns either a complete raw HTTP response or `NotHandled`:

```csharp
window.SetFileHandler(path =>
{
    if (path != "/health")
    {
        return WebUiFileHandlerResult.NotHandled;
    }

    return WebUiFileHandlerResult.FromResponse(
        "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
});
```

Responses are copied into WebUI-owned memory, including when WebUI's global
asynchronous-response mode is enabled. The delegate remains retained until it
is replaced or the window is disposed. An in-flight request safely finishes
with the registration it started with, and disposal defers native destruction
while managed callbacks are active. Exceptions are contained and become a
minimal 500 response. `WebUiFileHandlerOptions.MaxResponseBytes` can impose a
lower response limit than the native signed 32-bit length maximum.

`NotHandled` deliberately falls through to WebUI's configured local root. A
closed virtual boundary should return its own complete 404 response instead.
The callback cannot inspect request headers, WebUI serializes HTTP handling
behind a process-wide mutex, and async-response mode is also process-wide. This
is a contiguous-buffer API, not streaming.

## Packages

| Package | Purpose |
| --- | --- |
| `CsWebUi.Native` | Full low-level C ABI, `LibraryImport`, pointers, native enums, callbacks, and library override support. |
| `CsWebUi` | Friendly window, event, callback, JavaScript, browser/server, and lifecycle APIs. |
| `CsWebUi.Managed` | Source-preview managed implementation using Kestrel; not yet a compatibility-complete or published package. |

Release packages bundle the standard, non-TLS WebUI shared library for `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. The raw TLS API remains available when an application supplies a secure custom WebUI build.

The bundled Windows shared library statically links the MSVC runtime, avoiding a separate Visual C++ Redistributable prerequisite.

### Optional Windows NativeAOT static linking

Windows `win-x64` NativeAOT applications can opt into linking WebUI and the
WebView2 loader directly into the application executable:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <CsWebUiStaticLink>true</CsWebUiStaticLink>
</PropertyGroup>
```

Publish normally with `dotnet publish`. The resulting publish directory does
not need `webui-2.dll` or `WebView2Loader.dll`. The Microsoft Edge WebView2
Runtime itself remains a system prerequisite when embedded WebView mode is
used.

Static linking is opt-in and currently supports only `win-x64`. Without
`CsWebUiStaticLink`, the package retains its normal dynamic-library behavior.
`WebUiNativeLibrary.SetLibraryPath` and `CSWEBUI_NATIVE_LIBRARY` are bypassed
in static mode because NativeAOT resolves the WebUI entry points at link time.
The WebView2 loader redistribution terms are included in the package under
`licenses/WebView2`.

For a custom or locally built native library, configure it before the first WebUI call:

```csharp
CsWebUi.Native.WebUiNativeLibrary.SetLibraryPath("/path/to/libwebui-2.so");
```

Alternatively set `CSWEBUI_NATIVE_LIBRARY` to a library file or its containing directory.

## Upstream conformance and provenance

CS-WebUI covers the exported WebUI v2.5 C ABI. CI compares every `WEBUI_EXPORT`
in the pinned official compatibility header and the reviewed source-build header
with `CsWebUi.Native`, and fails if either surface drifts. The higher-level
`CsWebUi` package builds on that complete low-level layer with managed ownership
and callback APIs.

No WebUI native binaries are committed to this repository. Release workflows
build [`Runic-Artifex/webui`](https://github.com/Runic-Artifex/webui) at the
exact revision and header digest in
[`eng/webui-source-build-provenance.json`](eng/webui-source-build-provenance.json)
for every supported RID, run native lifecycle regressions, and package those
same artifacts. The package records SHA-256 values for every bundled binary
under `provenance/WebUI/native-assets.sha256`; CI verifies the packed values.

## NixOS development

The flake pins both Nixpkgs and the WebUI source revision used for development
and source-build testing. It builds `webui-2`, exposes it through
`CSWEBUI_NATIVE_LIBRARY`, and includes .NET 10, CMake, Chromium, Xvfb, and Linux
WebView dependencies.

```bash
nix develop
dotnet test
dotnet run --project samples/CsWebUi.BasicSample
```

Useful flake outputs:

```bash
nix build .#webui-native
nix build .#webui-stress-c
nix flake check
```

To compare the pinned native C stress test with the NativeAOT C# port through
the same failing or completed stage:

```bash
./eng/compare-upstream-examples.sh
```

## Design notes

- The raw package uses explicit Cdecl `LibraryImport` declarations, `nuint` for `size_t`, one-byte C booleans, and unmanaged function pointers.
- Both packages are trimming- and NativeAOT-oriented. The high-level callback dispatcher has no reflection-based registration.
- The high-level API parses JavaScript numeric arguments and serializes double results with the invariant culture, avoiding process-locale differences in WebUI's raw float helpers.
- Empty high-level callback results complete correctly when async bindings are present; WebUI's direct empty-string helper otherwise leaves that response pending.
- Strings passed to WebUI reject embedded null characters instead of silently truncating at the native C-string boundary.
- WebUI owns pointers returned from event accessors and other borrowed APIs. The safe `WebUiEvent` wrapper invalidates access after the callback completes.
- Browser mode needs an installed browser. Embedded WebView mode has platform dependencies, including the WebView2 runtime/loader on Windows and GTK/WebKit on Linux.

## License

CS-WebUI is MIT licensed. WebUI and the official WebUI C# logo are also MIT
licensed; their attributions are retained in [NOTICE](NOTICE).
