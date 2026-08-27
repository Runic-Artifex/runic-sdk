# Managed WebUI port roadmap

Status: M2 content and server compatibility complete

Compatibility oracle: `webui-dev/webui@52f9e75`

## Objective

Build a managed-first behavioral port of WebUI for .NET. Preserve WebUI's
observable window, bridge, binding, content, and lifecycle behavior before
intentionally evolving toward APIs and implementation choices that fit managed
applications better.

This is a semantic port, not a line-by-line translation of `webui.c`. The
compatibility surface may retain WebUI-shaped behavior while the implementation
uses managed ownership, asynchronous operations, cancellation, Kestrel, and
small platform host adapters.

## Package boundary

The port begins as the independent `CsWebUi.Managed` package in the
`CsWebUi.Managed` namespace. It does not reference `CsWebUi.Native` and does not
load a WebUI native library.

The existing packages remain unchanged:

- `CsWebUi.Native` is the complete low-level C ABI binding.
- `CsWebUi` is the safe high-level wrapper over native WebUI.
- `CsWebUi.Managed` is the managed implementation under development.

No public backend abstraction is introduced until both implementations have
enough behavioral coverage to reveal which concepts are genuinely shared.

## Porting principles

1. Pin one official WebUI revision as the behavioral oracle during each parity
   cycle.
2. Preserve externally observable contracts rather than internal C structure.
3. Keep native WebUI available as a fallback and differential-test oracle.
4. Add compatibility behavior only when an upstream example, public API, or
   regression demonstrates it.
5. Keep HTTP, WebSocket, binding, and lifecycle code asynchronous internally.
6. Preserve trimming and NativeAOT compatibility from the first milestone.
7. Keep browser hosting separate from embedded WebView hosting.
8. Record intentional deviations rather than silently approximating behavior.
9. Begin managed-specific improvements only after the corresponding
   compatibility area has a useful regression suite.

## Milestones

### M0: managed vertical slice

Implemented in the initial `CsWebUi.Managed` project:

- loopback Kestrel server on an ephemeral port;
- embedded HTML at the window root;
- generated `/webui.js` bridge;
- token-gated WebSocket connection;
- synchronous and asynchronous JavaScript-to-.NET bindings;
- string, integer, floating-point, Boolean, and null results;
- browser launch through the operating-system URL handler;
- deterministic async close, disposal, and restart;
- real HTTP and WebSocket behavioral tests;
- successful Linux x64 NativeAOT publication.

The original M0 JSON bridge validated the managed hosting and lifetime model.
It was then removed rather than retained as a second protocol path.

### M1: bridge and binding compatibility

Implemented:

- the pinned WebUI 8-byte binary packet header and little-endian fields;
- the `/_webui_ws_connect` endpoint and `CHECK_TK` token handshake;
- registration-order CSV binding discovery and dynamic `ADD_ID` messages;
- `CALL_FUNC` identifiers, length-prefixed arguments, raw byte transport, and
  correlated response packets;
- WebUI-compatible string responses for empty, integer, floating-point,
  Boolean, and string results;
- protocol-level tests for valid and invalid handshakes, typed and binary
  arguments, dynamic bindings, and connection shutdown;
- click and all-event bindings with connected, disconnected, mouse-click,
  callback, and navigation event metadata;
- client-specific and broadcast JavaScript, navigation, close, and raw-data
  commands;
- correlated JavaScript results, browser error propagation, cancellation, and
  timeouts, including synchronous JavaScript execution from inside a callback;
- browser-to-server `MULTI` framing for large calls, serialized bridge sends,
  keepalive traffic, and automatic reconnect;
- the browser-facing `webui.call`, generated binding functions, raw handlers,
  connection callbacks, logging, encoding, navigation, and high-contrast APIs;
- a real headless Chromium test covering both upstream example directions,
  click dispatch, raw data, and a 150 KB chunked call;
- a native-versus-managed differential test using the same binary handshake,
  raw arguments, binding callback, and response assertion.

Exit gate met: the upstream callback and JavaScript example flows run through
the managed engine without changing their page-side WebUI calls or binding
behavior.

### M2: content and server compatibility

Implemented:

- embedded HTML (including the M0 fragment convenience), explicit files, root
  folders, and external-URL refresh pages for server-only mode;
- priority `/webui.js` routing, explicit-entry redirects, and WebUI's
  `index.html`, `index.htm`, `index.ts`, and `index.js` folder fallback order;
- extension-based MIME handling, non-cacheable responses, favicon behavior,
  streamed physical files, scoped-root traversal protection, and a virtual
  content handler with local-root fallback;
- explicit or ephemeral ports, idempotent server-only start, and public or
  loopback Kestrel binding;
- independent multi-window roots and listeners, cookie-backed stable client
  identifiers, per-connection identifiers, and configurable single- or
  multi-client WebSocket admission;
- deterministic close and restart, bridge reconnect, and application-wide
  running state, wait, and exit operations;
- focused HTTP and lifecycle tests plus native-versus-managed differential
  coverage for folder routing and response metadata.

Kestrel remains the transport. Compatibility handlers may expose WebUI-shaped
complete responses, but the internal server must retain native streaming and
cancellation capabilities.

### M3: browser host compatibility

- Browser discovery and explicit browser selection.
- Chromium application mode and isolated profiles.
- Browser arguments, proxy settings, kiosk mode, and process ownership.
- Window close detection and deterministic child-process cleanup.
- Windows, Linux, and macOS browser-hosted validation.

Exit gate: all non-WebView upstream examples and the stress-test stages have a
managed execution path.

### M4: embedded WebView hosts

- WebView2 host on Windows.
- WKWebView host on macOS.
- WebKitGTK host on Linux.
- Common navigation, size, position, visibility, focus, and close contracts.
- Keep platform code in small host-specific assemblies or native shims.

The server, bridge, bindings, and application lifecycle remain managed and
shared across hosts.

### M5: managed-first evolution

After useful parity is established, add improvements deliberately:

- first-class `Stream`, pipelines, and asynchronous response bodies;
- `CancellationToken` propagation from browser disconnects;
- ASP.NET Core routing and middleware integration;
- dependency-injection scopes per application, window, or request;
- structured serialization with source-generated metadata;
- one-listener hosting where applications prefer it;
- clearer application and window lifetime objects instead of process globals;
- explicit security and origin policy;
- opt-in compatibility adapters for behavior that should not remain the
  managed default.

## Validation strategy

- Keep focused protocol and lifecycle tests in `CsWebUi.Managed.Tests`.
- Port reusable scenarios from `samples/UpstreamExamples` to an engine-neutral
  harness only after both engines can express them faithfully.
- Compare event order, argument values, responses, navigation, reconnect, and
  shutdown behavior rather than implementation details.
- Run browser-hosted tests on every supported operating system.
- Validate trimming and NativeAOT on representative milestones.
- Preserve the existing native wrapper tests throughout the port.

## Current intentional gaps

- Short non-path strings remain embedded bodies for M0 source compatibility;
  native WebUI treats such values as filenames unless they contain a full HTML
  marker.
- `ShowAsync` opens the default browser rather than selecting application mode.
- Port and public-binding changes take effect after close/restart rather than
  live-reloading a running listener.
- Window dragging and resize packets are recognized but have no platform host
  to act on until M3/M4.
- The bridge core high-contrast query currently returns `false` until browser
  and platform preference integration is added.
- There are no embedded WebViews yet.
- Package and API compatibility are not promised while the managed engine is a
  source preview.
