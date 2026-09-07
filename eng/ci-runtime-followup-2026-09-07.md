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

Follow-up verification completed:

- Full template matrix: 18 generated projects, including both hosts and npm/pnpm/Bun
  cases; 17 command/authoritative-snapshot smoke checks passed.
- All three published Linux consumers passed with Bun as the recorded verifier:
  Desktop default, Desktop minimal and CS-WebUI. Each ran the real browser and
  five lifecycle scenarios. Local reports are retained under
  `artifacts/host-footprint/linux-x64/run-1788781636317/`. These are verification
  results, not replacements for the earlier committed clean-source size baseline.
- Both CS-WebUI and Desktop CLI development passed live bridge/HMR acceptance with
  the unsaved draft preserved. An initial startup timed out during concurrent local
  verification; direct startup and the isolated full rerun passed. No timeout was
  widened and no test was skipped. Continue observing the CI startup check.
- No core dumps were reported locally since 13:00 CEST on 2026-09-07.
- [CI run 34118452783](https://github.com/Runic-Artifex/runic-sdk/actions/runs/34118452783)
  passed all three native jobs on code commit `4c37b8c9`: Linux x64, Windows x64 and
  macOS arm64. This includes the real Apple Silicon close/disposal/restart smoke.
  The broader verification and footprint jobs were still pending or running when
  this record was written.

No packages have been published. This evidence-only update does not restart CI.

## HMR readiness follow-up

Run 34118452783 subsequently completed with the aggregate `bun run verify` and
CS-WebUI HMR checks passing, followed by a Desktop Vite startup timeout. Templates
and footprint jobs were not reached. The startup stall remains undiagnosed; two
successful complete local HMR runs and 130 isolated Bun/Vite starts (including
100 launched through .NET) did not reproduce it.

Two diagnostic defects are fixed: the readiness fault no longer includes a URL
that the command sanitizer mistakes for a drive path, and acceptance cleanup
preserves the primary error alongside any shutdown failure. Readiness reports
which module probe failed and its last response, with the origin in local progress
output. CI retains per-host development logs, including successful startup logs.
The existing 30-second startup deadline and required shutdown checks remain.

Validation: all 25 development-tool tests pass, including controlled client/entry
timeouts, successful readiness, caller cancellation and early process exit. The
changed acceptance driver passed both real host/HMR journeys under Bun in the Nix
shell with `CI=true`; logs are under `artifacts/host-dev`. This is diagnostic
coverage, not proof that the intermittent startup stall is fixed.
