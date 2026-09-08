# Public Desktop transport coverage

The public `@runic-artifex/desktop` transport and the compatibility `webui.js`
client are different implementations. A passing compatibility test does not
establish that a public application can use the corresponding host operation.

The September 8 preview audit found this gap when the customer example's native
close callback waited for a JavaScript command that the public transport ignored.
Keep these regressions on the public path:

| Contract | Regression coverage |
| --- | --- |
| Host JavaScript results, errors, quick scripts and asynchronous confirmation | Desktop transport tests and `NativeHostTests` using the bundled public transport |
| Traffic during pending confirmation; veto and approved retry | Public native fixture, including native picker cancellation and scoped cleanup |
| Host navigation | Public native fixture verifies a new document, DOM marker, URL and authenticated connection |
| Terminal session closure | Public transport tests and server `SessionRevocationTests`, including an uncooperative peer and a self-closing callback |
| Reconnection | Late script replies cannot reach a replacement socket |
| Native window replacement | `WindowGenerationTests` reject stale mutations, serialize admitted work, and reset presentation options |
| Surface disposal | Admitted mutations drain; queued operations and opens are rejected after disposal |
| Bounded Effect frame delivery | Overflow must fail the stream and close the channel, never silently drop while remaining connected |
| WebSocket teardown | Unicode close reasons remain within the protocol's UTF-8 byte limit |

The native fixture is built from the public TypeScript sources and embedded in
the test executable for both JIT and NativeAOT. Compatibility tests remain useful
for their own contract. Native CI and recorded human interaction remain separate
evidence: fake hosts and headless X11 do not certify every compositor or native
permission scenario.

## Remaining API-design follow-ups

The public frame channel negotiates the Application Bridge capability. It is not
a replacement for the complete legacy `webui` JavaScript global. Before promising
generic compatibility, design and test public APIs for arbitrary named raw
receivers, generic capability calls/results, and DOM click/navigation/drag hooks.
Clarify which low-level .NET presentation event kinds are available through the
public registration API. Do not count a legacy-client test as completion of these
follow-ups.
