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

This original evidence did not certify native close interception. The follow-up
below adds native GTK evidence; OS dialog parity, accessibility across hosts and
MAUI/mobile support remain outside these checks.

## Native close follow-up — 2026-09-06

- Managed Desktop suite: 69 tests passed. New cases cover shared close decisions,
  veto/retry, callback failure/cancellation, caller cancellation, forced shutdown,
  late approvals, surface/window reuse, custom hosts and unsupported browser policy.
- A regression test holds an authenticated socket open after native destruction:
  `IsOpen` and `WaitForClose` now follow native window lifetime, not socket teardown.
- Native Linux smoke passed in a disposable Ubuntu 24.04 container with .NET SDK
  10.0.400, GTK 3.24.41, WebKitGTK 2.52.6 and Xvfb. It exercised actual GTK
  `delete-event` requests, an asynchronous pending decision with a responsive live
  JavaScript bridge, veto, retry, approval, destruction and subsequent window startup.
  Source compilation used the development SDK 10.0.302. The virtual display emitted
  a DRI3 acceleration warning; this was a lifecycle check, not a rendering benchmark.
- Live customer browser acceptance passed against the real C# host, including clean
  close, dirty close, Keep editing, Escape, discard and denial during an active save,
  alongside the existing business flows. This tests presentation policy; it does not
  claim a complete native customer UI automation run.
- Application/Desktop integration checks passed for bridge reconnect semantics,
  cancellation and deterministic teardown. Frontend state and workspace checks:
  10 tests passed (6 state tests and 4 workspace invariants).
- Customer host and native smoke builds passed with zero warnings/errors.
- Root CI now runs native close smoke on Linux, Windows and both macOS architectures.
  Windows/macOS native execution was not performed in this local Linux session.
  The current asynchronous Application host still needs a macOS main-thread runner;
  the lower-level Desktop smoke test supplies its own runner.

The next structural task is the [repository reorganisation](../../eng/repository-reorganisation.md).

## Native contact candidate checks

The managed migration harness exercises the real generated bridge for unavailable
file services, clipboard absence versus empty JSON, bounded UTF-8 input, reviewable
candidate correlation, stale export confirmation, saved-revision copy, selected-file
lease cleanup, dismissal, uncertain commit, cleanup after acknowledged commit,
concurrent-save export revision capture, and reconnect without replay. Its clipboard is deterministic and is not native OS
acceptance evidence. Existing persistence, cancellation and domain equivalence
checks remain in that harness.

For each final native binary, record its source commit, SHA-256, OS/backend,
scenario and outcome. Required manual checks: real selected-file import and
atomic export; actual clipboard copy/paste; cancel and retry; edit while import
is pending and review the late candidate; save a new revision while an export
picker is open and verify captured bytes; reconnect without repeated side effects;
restart and confirm saved data; keyboard focus recovery and dirty-close protection.
For this demo preview, manual release gates are the current local Linux environment
and the available Windows VM. Run the automated JIT and NativeAOT matrix on all
three supported OS targets. Actual macOS selected-file/signed-sandbox checks,
Wayland/portal if it is not the current local environment, broader accessibility
(NVDA, Orca, VoiceOver, IME, high contrast and scaling) and independent pilots are
explicit follow-ups before v1. Record each deferred scenario as deferred, never
as a passing receipt. Deterministic tests do not replace the required local Linux
and Windows VM native checks. Historical verification above remains historical.
