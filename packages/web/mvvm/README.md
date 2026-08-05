# `@runic-artifex/mvvm`

Framework-neutral TypeScript support for the `runic.toolkit.mvvm/1` wire
protocol. The package owns transport-independent message validation and the
client-side MVVM session model; UI framework bindings belong in separate
adapter packages.

## Runtime support

- Modern browsers with `TextEncoder`, `TextDecoder`, `AbortController`, and
  standards-compliant ES2022 support.
- Node.js 24.18 or newer for development, build, and conformance tests.
- Any transport that implements the SDK transport contract. The SDK does not
  depend on a browser framework, DOM renderer, WebSocket library, or JSON
  Schema runtime.

The package has no runtime dependencies. Its exact TypeScript compiler version
makes emitted JavaScript and declarations repeatable.

## Framework-neutral projection

`createMvvmProjection(client)` exposes immutable property, collection, command,
and validation reads keyed by protocol member ID. A projection emits one state
notification for each accepted client snapshot or atomic patch, delegates
property writes and command execution/cancellation to `MvvmClient`, and does
not introduce React, Vue, Svelte, Angular, or HTMX APIs.

## Package formats and namespace

The package exports browser- and Node-neutral ESM plus TypeScript declarations.
It does not install a browser global. Use a namespace import when a single
parent namespace is useful:

```ts
import * as RunicToolkit from "@runic-artifex/mvvm";
```

`RunicToolkit` remains the product's parent namespace. `MVVM` is the feature
area, while the lowercase string `runic.toolkit.mvvm/1` is the fixed, case-
sensitive wire protocol identity; it is not a JavaScript namespace.

CommonJS consumers can load the ESM entry point with dynamic `import()`. A
native `require()` entry point is deliberately not emitted: TypeScript 7 no
longer supports the legacy resolution mode needed for a second plain-`tsc`
CommonJS tree, and the SDK does not otherwise need a bundler.

## Public API

`ProtocolTransport` adapts an application-supplied `FrameChannel` and validates
every frame before dispatch. `MvvmClient` owns the handshake, snapshot and
patch state, commands, cancellation, validation state, and reconnect recovery.
The root entry point also exports the closed protocol unions, limits, codecs,
validators, and bounded error types used by adapters. `validateJsonFrame`
exposes the same bounded UTF-8/JSON reader for conformance cases that exercise
parser ceilings without requiring a client- or host-message envelope.

```ts
import * as RunicToolkit from "@runic-artifex/mvvm";

declare const channel: RunicToolkit.FrameChannel;
declare const viewId: RunicToolkit.Uuid;

const transport = new RunicToolkit.ProtocolTransport(channel);
const client = new RunicToolkit.MvvmClient(transport);

const unsubscribe = client.subscribe((event) => {
  if (event.type === "state") renderFrom(event.snapshot);
});

await client.start("Example.App", viewId);

declare function renderFrom(snapshot: RunicToolkit.ClientSnapshot): void;
```

The channel deals only in `Uint8Array` frames and supplies frame, close, and
error notifications. Applications choose the physical connection and can
rebind the transport before calling `client.reconnect()`. An in-flight mutation
at disconnect has an unknown outcome and must not be replayed automatically.

`startMvvmApplication` owns that sequence for framework applications. Once the
current channel has reported a close, pass a replacement channel to
`application.reconnect(channel)`. The promise resolves only after a new
handshake and authoritative replacement snapshot; reconnects are serialized
and are rejected before disconnection or after disposal.

`startNativeMvvmApplication` is the higher-level CsWebUi owner used by the
framework packages. It loads the private bridge, waits for binding readiness,
opens the generated contract, owns reconnect and page teardown, and lets the
framework register cleanup under that same lifetime. Its mutually exclusive
`channelFactory` option bypasses bridge loading for development-only
mock/replay hosts while retaining the production transport, client,
projection, generated contract, reconnect, and teardown code paths.

### Development inspector and mock host

`MvvmDevelopmentInspector` is an opt-in private-binding inspector.
`mountMvvmInspectorOverlay` renders its bounded event stream in the native
window. Events include direction, operation, member, revision, byte count,
duration, patch size, faults, and connection transitions. They deliberately
exclude capability tokens, arguments, property values, validation strings,
raw frames, and exception details. Pass a generated contract's
`memberMetadata` to the inspector to label correlated operations with both the
TypeScript member and its C# authoring member without exposing runtime values.
C#-first metadata also carries project-relative file, line, and column
coordinates. `createInjectedMvvmDevelopmentTools` activates the bounded native
overlay and `reportMvvmInspectorToEndpoint` terminal stream only when
`dotnet runic-toolkit dev` injects its random loopback endpoint; normal
production documents do not contain that endpoint.

`MvvmMockFrameChannel` is a development-only in-memory host that speaks the
real `runic.toolkit.mvvm/1` protocol. Supply an initial snapshot and mutation
handlers, then pass the channel to `startMvvmApplication`; generated contracts
and framework adapters run unchanged. The mock supports latency, validation,
command results and faults, pushed updates, stale revisions, cancellation
responses, and explicit disconnects, and exposes `mode === "mock"` so a
frontend can label the session unambiguously.
Use `createMvvmMockChannelFactory` when the high-level owner exposes reconnect:
replacement channels retain the fixture closure, accepted snapshot members,
and authoritative revision instead of silently resetting mock state.

## Protocol behavior

The SDK follows the normative specification and deterministic corpus under
`protocol/mvvm`. In particular, consumers should expect it to:

- reject malformed, oversized, too-deep, duplicate-key, or otherwise hostile
  envelopes before application dispatch;
- preserve revisions losslessly and apply only consecutive, atomic patches;
- replace projected members, command state, collections, and validation state
  when an authoritative snapshot arrives;
- expose one terminal outcome for each command and deterministic cancellation
  and timeout behavior;
- request an authoritative snapshot after a revision gap or reconnect instead
  of speculatively replaying patches; and
- treat capability tokens as bearer secrets that must not appear in host
  messages, faults, logs, or diagnostics.

Framework adapters should depend only on the public exports of this package.
They must not duplicate the wire validator or revision state machine.

## Development

From this package directory, after the workspace dependencies are installed:

```sh
npm run build
npm test
```

`npm run build` deletes `dist` and produces deterministic ES modules and
declarations in `dist/esm`. `npm test` rebuilds the package and
runs every `test/**/*.test.ts` and `test/**/*.test.mjs` file with the Node.js
test runner and Node's type-stripping support. `npm pack --dry-run` can inspect the package
surface; only `dist`, this README, and npm's package metadata are included.

The repository root owns workspace registration and its lockfile. Changes to
this package's exact development dependency require the root owner to refresh
and commit the root `package-lock.json`; this package does not maintain a
second lockfile.

## Security boundary

Treat received frames, payload values, error text, transport close reasons,
and reconnect state as untrusted. Do not log raw frames or capabilities. The
SDK validation limits are security requirements, not tuning suggestions. A UI
adapter must also render received strings as data and must not turn them into
HTML, code, member names, filesystem paths, or dynamic property access.

The protocol specification is authoritative if this overview and the protocol
ever differ.
