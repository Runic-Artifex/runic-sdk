# CI runtime and native cleanup follow-up

The active CI matrix is Linux x64, Windows x64 and macOS Apple Silicon. Intel
macOS runners were removed at the maintainer's request. Existing release receipts
and source-hash records remain historical evidence, not newly certified targets.

## Failures diagnosed

- Windows footprint bootstrap called `spawnSync("bun.cmd")`. Bun is a native
  executable; the runner now launches `bun` directly.
- macOS footprint isolation compared a real `/private/var/...` package path with
  its unresolved `/var/...` consumer path. Both boundaries are now canonicalized,
  with a relative-path containment check that also rejects external symlinks and
  similarly prefixed sibling directories.
- macOS native close interception completed, but surface disposal stalled.
  The native Closed callback cleared the host reference before asynchronous
  release completed. Another close caller could finish and stop main-thread
  pumping while that release still needed the main thread. Presentation cleanup
  is now serialized; `WaitForClose` joins cleanup while servicing native work.
- A prior intermittent browser-profile cleanup failure hid the deletion error.
  Tests now retain that error and bounded generated-profile entry metadata. It
  did not recur in the diagnostic macOS run; do not label that intermittent issue
  definitively resolved solely because a later run passed.

Diagnostic evidence: [run 34116114771](https://github.com/Runic-Artifex/runic-sdk/actions/runs/34116114771).
The bounded native smoke deadline exits with failure instead of occupying a runner
until its job timeout or creating an unhandled managed-exception core dump.

## Runtime policy

Repository build scripts, unit tests, code generation, editor checks, documentation
and footprint consumers use Bun. Bun package-manager selection in the CLI and
in-repository MSBuild frontend targets also force Bun for Node-shebang executables.
`node:` imports remain valid Bun-compatible APIs.

Node is explicitly resolved for npm/pnpm compatibility consumers. Bun's forced
runtime mode can insert a Node-to-Bun shim on PATH, so these tests verify the
actual runtime and put real Node first for their child processes.

The optional pinned Vite DevTools dock uses a crossws Node transport that rejects
Bun. Core diagnostics/HMR work on Bun; auto mode omits the dock client and an
explicit requirement fails with `RUNICP007`. The Svelte template selects the
optional dock only under Node. A real installed npm consumer retains dock coverage.
Vue's pinned typechecker remains an explicit npm/Node compatibility check; the
Vue template build is separately checked with only Bun and .NET on PATH.

The obsolete Angular package test glob pointed at a nonexistent directory and was
removed. Angular package/template consumers and acceptance-receipt validation remain
in the root verification workflow; there is no claim of an Angular unit suite there.

## Local verification

Performed in the repository's actual Nix development environment:

- Build, managed tests, frontend tests, editor checks and generated bridge checks
  ran with Bun as the default JavaScript runtime.
- Desktop: 70 tests, including delayed native callback cleanup; Linux native smoke
  passed close veto, retry, responsiveness, approval and restart.
- Vite plugin: 15 tests, including Bun diagnostics and the installed npm/Node dock.
- Svelte: 16 tests; remaining package suites and editor checks passed.
- Documentation tests passed after preserving the exact historical release verifier
  sources required by the pinned release-data source hashes.
- Packed consumers: 16 NuGet libraries, two tools, two template packages and eight
  npm artifacts passed. npm runtime execution remains on actual Node.
- Path alias/external-link regression and real-Node compatibility checks passed.
- Workflow YAML parsed; both native and footprint matrices contain only the three
  active RIDs.

Full template acceptance, Bun-driven published footprint/browser lifecycle checks
and the updated remote native runs are the remaining verification gates at this
commit. No packages have been published.
