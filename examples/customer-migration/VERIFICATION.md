# Verification evidence

Verified on 2026-09-05 in the Linux development environment using .NET 10.0.302,
Node 24.19.0, Bun 1.4.0 and Chromium 152.

- Full `bun run build` and `bun run test`: passed, including the editor, docs,
  SDK contracts, package tests, and the newly integrated migration checks.
- Migration comparison: 26 assertions covering shared business outcomes,
  validation, overlapping saves, cancellation, version conflicts, uniqueness,
  reconnect, session isolation and disk persistence.
- Frontend state tests: 6 passed, including stale receipts, dirty reconnect,
  normalized saves, cancellation, discard and bounded contact import.
- Application Bridge suite: 34 passed, including five new cancellation cases.
- Live browser acceptance: passed against the actual C# host with an isolated
  temporary data file; search, validation, save, cancel, navigation prompts,
  reconnect, import, uniqueness errors and narrow layout were exercised.
- Final host build and live browser acceptance passed again after the last
  frontend error-handling change. The rendered desktop layout was inspected.
- WPF project: cross-compiled successfully with zero warnings/errors. Native WPF
  execution was not performed; CI now compiles this host on Windows as well.
- Frozen workspace install, workspace invariants, generated-contract checks,
  migration documentation links and whitespace checks: passed.
- The built Runic host dependency graph contains neither CommunityToolkit nor
  the original viewmodel assembly.

This evidence does not certify native close interception, OS dialog parity,
accessibility on all native hosts, or MAUI/mobile support. Those limits and the
next SDK work are recorded in the README and migration RFC.
