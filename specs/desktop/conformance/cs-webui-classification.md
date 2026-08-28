# CS-WebUI comparison classification

Oracle revision: `webui-dev/webui@52f9e75`

CS-WebUI is an independent upstream-compatible product and a differential
oracle. It is not a Runic Desktop production dependency. Each comparison used
by W90 has one classification:

| Area | Classification | Runic Desktop treatment |
| --- | --- | --- |
| 8-byte packet header, token command, binding discovery, calls, results, raw bytes, and `MULTI` reassembly | Retained compatibility behavior | The internal `webui-compat/52f9e75` profile and codec tests preserve page interoperability. |
| Embedded HTML, explicit files/folders, MIME selection, redirects, and bridge route priority | Retained compatibility behavior | M0-M4 behavioral tests remain differential evidence behind the M6 API. |
| Installed-browser discovery and platform WebView hosting | Retained compatibility behavior | Observable presentation behavior is retained through Runic-owned host adapters. |
| One server and mutable configuration per `WebUiWindow` | Intentional divergence | `DesktopHost` owns listeners; isolated `DesktopSurface` instances share by default and immutable options define boundaries. |
| Process-global wait, client, cookie, root, browser-folder, and embedded-factory configuration | Intentional divergence | The public path has no mutable process globals; host and surface options own configuration and disposal. |
| 32-bit WebUI packet token as the only browser admission check | Intentional divergence | M6 adds canonical origin validation and a 256-bit surface-scoped credential before the compatibility token exchange. |
| Wildcard CORS and implicit trust in loopback location | Intentional divergence | The safe default is same-origin, credentialed, single-client, and loopback-only. |
| Per-window listener as the normal topology | Intentional divergence | Shared-listener hosting is the default; isolated listeners are explicit. |
| Synchronous setters and show/close calls as the primary public API | Intentional divergence | Async creation, presentation, cancellation, close, and disposal are primary. |
| WebUI C ABI, allocation rules, CivetWeb patches, and native callback threading | No longer applicable | These remain CS-WebUI/upstream responsibilities and do not constrain the managed implementation. |
| Exact WebUI public type names and numeric C enum identity | No longer applicable | No public M6 type contains `WebUi`; the internal profile carries only required wire behavior. |

An upstream change is adopted only when it remains useful under a retained
classification or receives an explicit new Runic Desktop contract decision.
