# `@webuitoolkit/mvvm-conformance`

Runtime-neutral conformance support for TypeScript implementations of the
`webuitoolkit.mvvm/1` protocol. The package executes deterministic protocol and
session cases against `@webuitoolkit/mvvm`; it does not provide a UI adapter or
choose a transport.

> **Publication status:** this repository has not granted a source or package
> license. The package is marked `private` and `UNLICENSED` until the project
> publication review is complete.

## What the suite covers

The conformance cases exercise the observable SDK contract rather than a UI
framework:

- closed envelope and payload validation, lossless revisions, and hostile-input
  bounds;
- authoritative snapshots, atomic consecutive patches, validation state, and
  revision-gap recovery;
- command success, fault, cancellation, timeout, and exactly-one-terminal-state
  behavior; and
- disconnect, reconnect, fresh handshake, snapshot recovery, and rejection of
  speculative request replay.

The protocol specification remains authoritative. Passing this suite is
evidence of compatibility with its covered cases, not a substitute for a
security review.

## Node and browser runtimes

The exported runner uses only ES2022 and web-platform APIs. It can therefore be
hosted by a modern browser, a browser automation harness, or Node.js. It does
not import `node:*`, create DOM elements, install globals, or depend on a test
framework. Consumers supply cases and report results through the public API,
so a framework adapter can run exactly the same inputs in its supported
browser matrix.

Node.js 24.18 or newer is the repository's reference build and test runtime.
The package is emitted as ESM with TypeScript declarations:

```ts
import * as WebUIToolkitConformance from "@webuitoolkit/mvvm-conformance";
```

`WebUIToolkit` remains the parent product namespace. The lowercase
`webuitoolkit.mvvm/1` value is the fixed, case-sensitive wire protocol
identity, not a JavaScript namespace.

## Deterministic fixture corpus

The repository-owned corpus lives at `web/fixtures/conformance`:

- `protocol/v1/manifest.json` indexes valid, invalid, boundary, and semantic
  protocol documents;
- `protocol/v1/valid` and `protocol/v1/invalid` contain envelope cases;
- `protocol/v1/semantic` freezes cross-runtime behavioral expectations; and
- `vectors` contains executable state, command, reconnect, hostile-input, and
  reserved Flow projection traces.

Read fixture files as UTF-8 bytes and preserve manifest order. Do not parse and
re-serialize a frame before giving it to the SDK: duplicate keys, byte limits,
integer precision, and exact frame identity are part of the contract. A browser
harness should serve or bundle the fixture files without changing their bytes,
then pass their contents to the same exported runner used by Node tests.

Fixtures are versioned test data, not package exports. Consumers outside this
repository should vendor a pinned corpus revision or provide equivalent cases
to the runner rather than relying on a relative filesystem path inside the npm
package.

## Development

Install dependencies from the repository root so npm links the sibling
`@webuitoolkit/mvvm` workspace at the exact `0.1.0` package version. Then run:

```sh
npm run build --workspace @webuitoolkit/mvvm-conformance
npm test --workspace @webuitoolkit/mvvm-conformance
```

From this package directory, the equivalent commands are `npm run build` and
`npm test`. The build deletes `dist` and emits deterministic ESM and declaration
trees. The test command rebuilds first and then runs every
`test/**/*.test.ts` and `test/**/*.test.mjs` file with Node's test runner.

For a real-browser smoke, build both workspaces, serve the repository root over
HTTP, and open `web/packages/conformance/test/browser-smoke.html`. The harness
loads both ESM packages through an import map, fetches the committed corpus, and
marks `#result[data-status="passed"]` only when the SDK-backed report has 94
passes, zero failures, and zero skipped mandatory cases. It uses the same
in-memory framed SDK runtime as the Node suite; it is not an HTMX browser
harness.

`npm pack --dry-run` can inspect the package surface; only `dist`, this README,
and npm package metadata are included. The repository root owns workspace
registration and `package-lock.json`. Dependency changes require the root owner
to refresh that lockfile; this package does not maintain a second lockfile.

## Adding a case

Keep a case deterministic: use fixed identifiers and revisions, avoid clocks,
randomness, networking, locale-sensitive comparisons, and implementation-only
state. Add the data under the versioned fixture tree, register it in the
manifest when applicable, and assert the same public result in both the Node
suite and every browser harness. Invalid and hostile inputs must demonstrate
that no application dispatch occurred and must not require diagnostics to echo
untrusted payloads, capabilities, paths, or stack traces.
