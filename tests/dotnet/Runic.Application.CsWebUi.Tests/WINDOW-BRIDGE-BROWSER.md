# Internal Window Bridge browser fixture

This fixed Notes fixture checks the new internal `WindowBridgeSession` and
`CsWebUiWindowBridgeHost` together through actual CS-WebUI callbacks. It is a
manual protocol witness, not a generated client or public SDK example. The
ordinary frontend in this check is a small headless Chromium script.

Its `WindowBridgeCheckedTitleField` and
`CsWebUiWindowBridgeNotesDescriptor` are compiled only into test assemblies.
They deliberately do not ship in `Runic.Application.Bridge` or
`Runic.Application.CsWebUi`.

From this SDK worktree, use the locked shell:

```sh
direnv exec . dotnet build tests/dotnet/Runic.Application.CsWebUi.Tests/Runic.Application.CsWebUi.Tests.csproj -c Release
direnv exec . node tests/dotnet/Runic.Application.CsWebUi.Tests/window-bridge-browser-smoke.mjs
```

The script launches the test executable in `--window-bridge-browser-host` mode,
loads its private loopback URL, and calls typed fixture methods through the one
credential-checked fixed dispatcher. The bootstrap carries the fixture's
internal endpoint descriptors; it does not expose a generic author API. The
host issues an ordered document ticket and owns a fixed logical begin route.
Its reply returns the current endpoint revision and map to recover an update
that happened after the HTML response was captured. Handoff broadcasts are
best effort: a failed WebSocket send does not remove a valid server endpoint,
and the next begin reply reconciles the map. The smoke
verifies a typed title snapshot, direct setter acknowledgement, a stale checked
write conflict, explicit rebase, and duplicate receipt. It opens a
ViewModel-backed dialog after the HTML page has loaded, waits for its internal
descriptor handoff, retires it, and verifies the stale descriptor is
disconnected before creating a replacement. The fixed native registration count
remains one. It uses CDP `Page.reload` and waits for a newly minted document
epoch before asserting that the fresh document received the latest descriptor
map. It admits that epoch, remounts Editor and the dialog, and dispatches the
dialog descriptor after reload. It also dispatches a `__proto__` route from a
null-prototype endpoint map. The script then starts a held Save, navigates the window
away from Editor, releases the Save, and observes a truthful `succeeded`
terminal status with `state: null`. The marker is emitted only after the host
reports that its owned scope drained:

```text
SDK_WINDOW_BRIDGE_BROWSER_OK|fixed-dispatch|bootstrap|fresh-document-reload|dynamic-dialog-after-load|retired-descriptor-disconnected|revisioned-handoff|reload-latest-map|safe-endpoint-keys|one-native-bind|...|two-accepted-saves|document-owned-terminal-waits|terminal-state-null|scope-drained
```

This check observes one adapter dispatcher registration plus one host disconnect
registration. It does not measure native memory, unbinding, or capacity. It
uses one browser connection. A second Chromium
process with its own profile could load the same HTML and bootstrap, but the
CS-WebUI host did not supply `globalThis.webui` to it. This adapter therefore
does not currently provide a real two-client browser test. The host-neutral
core and fake transport tests exercise connection-bound presentation leases
and reject an authenticated but unmounted connection; the native test suite
covers callback rejection and close ordering. The fixed Notes fixture above
remains manually authored. The separate post-MVVM browser probe now exercises
a fixture-only consumer that imports emitted metadata/types, infers four
fixture routes, and calls Toolkit Title/Save plus ReactiveUI Refresh. It does
not make that inferred convention a generated frontend runtime or public API.

## Direct-route drain boundary

The fixed dispatcher can drain callbacks that entered through an attachment's
endpoint lease. It has no transport-wide drain contract for arbitrary direct
native callbacks. A generated route that owns a View, scoped service, or other
retireable resource must therefore remain attachment-owned until the host has
that broader drain proof. This fixture does not make direct native callbacks
safe to outlive an attachment.

## Actual browser document-epoch acceptance

`window-bridge-reload-smoke.mjs` reloads that same headless Chromium page with
the Chrome DevTools Protocol `Page.reload` command, then waits for a fresh
bootstrap epoch. It is intentionally separate from the primary smoke so
the primary lifecycle witness remains strict about its one presentation.

```sh
direnv exec . dotnet build tests/dotnet/Runic.Application.CsWebUi.Tests/Runic.Application.CsWebUi.Tests.csproj -c Release
direnv exec . node tests/dotnet/Runic.Application.CsWebUi.Tests/window-bridge-reload-smoke.mjs
```

On the locked Linux CS-WebUI host, the completed navigation emits a native
disconnect event, then subsequent callbacks reuse the same client and
connection IDs. The host issues a fresh 32-character ordered document
ticket for each entry response. `__runicBridgeDocumentBegin` verifies the
ticket and retires every old-document
presentation before the replacement mounts, and fixed Notes calls include that
epoch. A saved old descriptor therefore receives a normal rejected result and
cannot reach the replacement. The smoke also mounts two same-reference Editor
presentations in the replacement, removes one, and verifies the other still
serves its typed state:

```text
SDK_WINDOW_BRIDGE_DOCUMENT_EPOCH_OK|native-connection-preserved|old-document-drained|replacement-mounted|stale-document-rejected|same-reference-independent|stale-generation-disconnected
```

It also sends a deliberately wrong endpoint generation after the reload and
observes the fixed dispatcher return `disconnected`. The host-neutral core
suite separately covers retired epoch rejection, a queued old presentation
publication, same-reference lease independence, and delayed mount denial after
a native disconnect. The manual routes remain test fixture protocol only; no
generated client or public author API is implied.

The earlier `SDK_WINDOW_BRIDGE_RELOAD_OBSERVED|...|old-lease-retained` probe
called `location.reload()` but could read the already-true connection flag
before navigation finished. It was not a valid completed-reload observation
and is superseded by the fresh-epoch probe above.

The host-neutral test also delivers an unseen older begin after a newer
document has mounted; the older ticket cannot retire the newer mount. A host
test captures entry HTML, adds a route, then checks that the begin reply
recovers that route from the current manifest. Ordered tickets also reject
older documents without retaining a per-reload tombstone set; the core suite
admits 140 successive tickets on one reused connection. These are internal protocol
checks, not generated-client or reconnect-loop evidence.

## Same-document socket reconnect limit

`window-bridge-reconnect-limit-smoke.mjs` forces the WebUI socket to close while
the Chromium document stays loaded. It uses CDP `Runtime.getProperties` to
find WebUI v2.5.0-beta.4's private `#ws` and invokes `close()` on that socket;
WebUI's own reconnect loop opens the replacement. This is test-only access to
a library implementation detail. In this Chromium run, CDP
`Network.emulateNetworkConditions({ offline: true })` did not close an
already-established WebSocket, so it was not an adequate disconnect probe.

```sh
direnv exec . dotnet build tests/dotnet/Runic.Application.CsWebUi.Tests/Runic.Application.CsWebUi.Tests.csproj -c Release
direnv exec . node tests/dotnet/Runic.Application.CsWebUi.Tests/window-bridge-reconnect-limit-smoke.mjs
```

The actual native callback sequence was `Connected:0:0`, `Disconnected:0:0`,
`Connected:0:0`, with each pair recording `ClientId:ConnectionId`. The newly
connected browser has the **same** issued document ticket and the same
callback identity as before. The disconnect releases Editor's presentation.
`__runicBridgeDocumentBegin` rejects that same ticket with `stale-document`
and “The native connection is no longer active”; a subsequent Editor mount is
rejected. The smoke checks those precise outcomes and prints:

```text
SDK_WINDOW_BRIDGE_RECONNECT_LIMIT_OBSERVED|same-document-ticket|native-callback-identity-reused|old-presentation-drained|same-ticket-begin-rejected|editor-remount-rejected
```

The host-neutral core can distinguish a newer entry ticket on the reused
identity, as the completed page-reload check above demonstrates. It cannot
distinguish this socket incarnation from delayed callbacks of the old one
using the current CS-WebUI callback fields. Re-admitting the same ticket by
simply clearing the disconnected flag would let an old callback regain access.
An ordinary frontend needs an explicit reconnect recovery policy, such as a
fresh entry reload, until the host and client have an incarnation handshake
with a demonstrable stale-callback rule. This probe does not implement such a
policy or promise seamless same-document recovery.

## Explicit reload after socket reconnect

`window-bridge-reconnect-reload-smoke.mjs` keeps the limit witness above intact
and runs a separate fallback path in actual Chromium. It closes WebUI's current
private `#ws` through CDP, waits for WebUI's own socket reconnect, verifies
that the retained ticket still cannot begin or remount, and then invokes CDP
`Page.reload`. The script waits for a **different host-issued entry ticket**
before beginning the replacement document. It remounts Editor and reads the
window's retained title. This is an explicit full navigation in a test fixture;
there is no automatic production frontend policy or transparent retention of
component state.

```sh
direnv exec . node tests/dotnet/Runic.Application.CsWebUi.Tests/window-bridge-reconnect-reload-smoke.mjs
```

The fixture accepts a held Save before the disconnect. The ordinary Save
status route remains tied to its initiating document, so the replacement
document receives `rejected` there. A separate **test-only**,
presentation-gated window observer sees that the accepted work is still
`running`, releases it, and sees `succeeded` with the title captured before the
disconnect. This demonstrates operation lifetime across document replacement;
it does not define a public cross-document operation lookup API. The marker is
printed only after the fixture host reports its scope drained:

```text
SDK_WINDOW_BRIDGE_RECONNECT_RELOAD_OK|same-document-limit-observed|fresh-host-ticket|fallback-navigation-completed|replacement-admitted|editor-remounted|title-restored|old-document-rejected|accepted-window-work-survived|scope-drained
```

The CS-WebUI event callback provides `ClientId` and `ConnectionId`, and this
host observes `Disconnected:0:0` followed by `Connected:0:0` for the socket
replacement. Those fields identify the logical connection but expose no new
socket incarnation. The host-issued ticket belongs to an **entry response**;
WebUI's same-document reconnect does not fetch another entry response and
therefore retains the old ticket. Clearing the disconnected state on the old
ticket would also admit a delayed old-socket callback with the reused IDs.
Full page reload obtains a new ticket and lets the existing ordered document
admission retire old presentations. A seamless same-document recovery would
require host and browser protocol evidence for a fresh socket incarnation and
rejection of delayed callbacks from its predecessor.

## Ordinary TypeScript consumers of one logical Editor

`window-bridge-notes-client.ts` is a fixture-only ordinary TypeScript authoring
sketch. It admits the current document, resolves the current fixed-dispatch
descriptor on each call, and gives each component an `EditorLease` with its own
presentation ID. `title()` returns a `TitleSnapshot`; `setTitle()` and
`writeTitle()` return discriminated receipts; `startSave()` returns an explicit
admission result. Each Editor title request and Save start carries that specific
presentation ID. The host rejects a callback from an unmounted component even
when another component still presents the same logical Editor in the document.
An already accepted Save remains window-owned and observable by request ID after
the initiating component leaves. The client waits for an in-flight mount before
teardown, so a late mount callback cannot leave a presentation behind. A compile-only
consumer in `window-bridge-notes-client.typecheck.ts` checks the intended method
signatures and rejects invalid title, baseline, status, and lease-ID usage.
The fixture now checks each host reply at runtime before returning its typed
result. It validates document admission and the full descriptor map, mount and
unmount acknowledgments, title snapshots and receipts, and Save admission and
status. Unknown variants, unexpected fields, and malformed nested values fail
at the wire boundary. These are handwritten fixture decoders that establish
the desired ordinary TypeScript behavior; production route-specific decoders
remain a code-generation task.

With the workspace TypeScript dependency installed, typecheck it using:

```sh
direnv exec . ./node_modules/.bin/tsc -p tests/dotnet/Runic.Application.CsWebUi.Tests/tsconfig.notes-client.json
```

The mocked-wire check sends malformed manifest, title, and Save replies that
TypeScript declarations alone cannot detect. It also loses admission and
completion replies, then checks that `SaveUncertainError` retains the request
ID and Start is never replayed automatically. A separate mocked missed-handoff
case refreshes the host manifest only when a descriptor is absent, before
sending the Title request:

```sh
direnv exec . node tests/dotnet/Runic.Application.CsWebUi.Tests/window-bridge-notes-client-wire-test.mjs
```

It prints `SDK_WINDOW_BRIDGE_NOTES_WIRE_DECODE_OK|...|lost-admission-id-retained|lost-completion-id-retained|no-start-replay|missing-descriptor-reconciled-before-call`.

The browser smoke remains a runnable `.mjs` script. Node 24's built-in type
stripping loads this test-only TypeScript module into the actual Chromium
document, so the executable client and checked authoring source stay together:

```sh
direnv exec . node tests/dotnet/Runic.Application.CsWebUi.Tests/window-bridge-shared-client-smoke.mjs
```

The script imports that module inside one actual Chromium document. It proves
the title snapshot, direct setter, checked-write conflict and rebase receipts,
and an ordinary `await editor.save()` that waits for a held .NET operation.
It also installs a fixture listener for the adapter's data-free refresh hint,
triggers that hint from a mounted Editor, and uses the mounted peer's ordinary
typed `title()` route to pull the current snapshot. The released owner cannot
trigger another hint or that typed pull. This is a fixture proof of the
refresh-hint path, not a generated subscription API.
The separate `startSave()` method still exposes admission; a duplicate
request ID reports the existing terminal outcome without replay. Releasing one of
two mounted consumers leaves the peer able to read the title. It sends raw
title read, direct write, checked write, and Save start callbacks using the
released owner's ID; all are rejected while the peer stays mounted and its
title stays unchanged. It starts and releases a mount before its callback
settles and observes no remaining presentation. Finally, it mounts a replacement before
releasing its predecessor, sends a duplicate stale cleanup for the predecessor
ID, and finds the replacement active; releasing that final owner removes title
access after Preview navigation has already retired the Editor endpoint. The
ordinary client treats the ensuing rejected unmount as completed component
teardown. It waits for the host scope to drain before printing:

```text
SDK_WINDOW_BRIDGE_ORDINARY_CLIENT_OK|one-document|two-consumers|typed-snapshot|setter-receipts|refresh-hint-authorized-pull|stale-refresh-rejected|awaited-save|duplicate-admission-decoded|independent-release|exact-presentation-callback-gate|late-mount-drained|overlap-replacement|stale-cleanup-rejected|navigation-before-unmount|scope-drained
```

The overlap models an HMR component replacement; it does not run Vite or a
framework HMR cycle. Fixture title and Save-start callbacks now require the
caller's exact active Editor presentation ID, so a released owner cannot use
its peer's mount. `save()` reports an uncertain admission or completion with
its request ID if the reply is lost or malformed; the fixture never replays
Start automatically. The missing-descriptor refresh has a mocked wire proof;
it does not establish eventual delivery of every one-way dynamic handoff or
safe retry after a dispatched mutation. The result types and runtime decoders
are hand-written; they reject malformed replies but have no generated
correspondence to the .NET fixture. This is an internal consumer witness, not
a generated TypeScript API or a public SDK promise.
