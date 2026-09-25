# Public Desktop API coverage

The public `Runic.Desktop` .NET API and the retained internal WebUI compatibility
implementation are separate surfaces. A passing compatibility test does not
establish that an application can use a corresponding public host operation.

Keep public-path regression coverage for the managed Desktop API:

| Contract | Regression coverage |
| --- | --- |
| Host, surface, window, listener and request lifetimes | `Runic.Desktop.Tests` managed API suite |
| Streaming responses, backpressure and disconnect cancellation | `Runic.Desktop.Tests` request and surface scenarios |
| Origin and credential admission, content roots and typed failures | `Runic.Desktop.Tests` security and policy scenarios |
| Embedded-window policy, navigation and close confirmation | `Runic.Desktop.Tests` plus `Runic.Desktop.WebViewSmoke` on supported native runners |
| Native dispatch and host thread ownership | Desktop native smoke and independent Platform runtime conformance |
| NativeAOT behavior | `Runic.Desktop.WebViewSmoke` NativeAOT lane and release artifact checks |

The internal `webui-compat/52f9e75` implementation remains protocol and
differential evidence; it is not a public application transport contract. Native
CI and recorded human interaction are separate evidence: fake hosts and headless
X11 do not certify every compositor, WebView runtime, or native permission scenario.

## Public API boundary

The public .NET API models windows, surfaces, requests, and native capabilities.
It does not publish a separate TypeScript Desktop transport package in this
preview. Runic Application Views selects explicit Window and View contracts and
generates TypeScript application clients; its current host adapter targets
CS-WebUI. A future Views-to-Desktop adapter requires its own explicit design and
public-path tests.
