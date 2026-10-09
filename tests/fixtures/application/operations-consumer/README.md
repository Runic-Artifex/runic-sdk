# External operation consumer

This fixture is copied outside the SDK and built from the packed NuGet and npm
archives. It uses the shipped MSBuild bootstrap, generated composition/client,
`DesktopHost`, `DesktopBridgeTransport`, scoped ReactiveUI model and Svelte
operation binding. There are no source references or workspace symlinks.

Run after `bun run pack`, using the locked development environment:

```sh
bun tests/fixtures/application/operations-consumer/package-smoke.mjs
```

The existing Linux package-consumer CI job runs these focused journeys:

- Shared DTOs have no Runic dependency. Generation requires explicit
  `[assembly: RunicBridgeJsonIgnore]` for an unconditional computed `[JsonIgnore]`
  property; conditional JSON rules and root model members remain in the contract.
- An awaited long command and a public operation `wait()` admit concurrent draft
  and Cancel callbacks through the real desktop transport. The shipped Svelte
  `useOperation` renders pending and cancellation state.
- `createLatestOperationController` coalesces choices before a delayed real Start
  receipt, waits for actual accepted read completion before admitting the newest
  choice, and drops stale intents/cancellation across repository replacement.
- The generated recovery API reports unknown admission; the helper blocks new
  intent until the application explicitly replaces that uncertain boundary.
- Destroying the Svelte component detaches observation while accepted work remains
  active. The application requests cancellation separately.
- A cancelled generated invocation drains its content session while model recovery
  is still accepted. Async scope disposal waits on `AcceptedWorkScope` before
  recovery/resource release.

The delayed receipts wrap the generated Start promise; they do not fabricate
operation handles or implement a protocol. The consumer owns deterministic read
and recovery gates, like an application service waiting on an external operation.
Native owner/directory chooser and CLI result codecs have separate consumers.

For local desktop preview verification, add `--serve`. Open the printed URL with
the desktop preview tools and evaluate `await window.runJourneys()`. Send `close`
on the process stdin, observe `OPERATIONS_DISPOSE_PENDING`, then send `release`.
Successful shutdown prints `OPERATIONS_ACCEPTED_WORK_DRAINED`.

`--build-only` checks package isolation, generation and Svelte typing without
opening a browser. `--keep` retains the external application for investigation.
The runner removes its temporary source/build tree and candidate package cache
entries by default. Third-party NuGet packages and the .NET CLI home use the stable
external cache at `~/.cache/runic-package-consumers/operations`; override it with
`RUNIC_OPERATIONS_CONSUMER_CACHE` when needed. npm uses its existing user cache.
