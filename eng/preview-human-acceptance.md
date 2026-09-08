# Preview human acceptance handoff

Target: `0.2.0-preview.1`, September 8, 2026, under the demo-preview policy.
The user coordinates manual checks on this Linux system and the available Windows
VM, plus registry account configuration. Automated native JIT and NativeAOT CI on
Windows x64, Linux x64 and macOS arm64 remains required. The orchestrator integrates
results, fixes blockers and owns publication. Missing required gates delay release.

Real macOS interaction and signed sandbox checks, Wayland checks when unavailable
on this Linux system, broader accessibility certification and two independent pilot
developers are deferred follow-ups before v1. They do not block this demo preview.
Record them as deferred, without manufacturing pass receipts or implying native
certification from automated tests. The sections below retain their scenarios so the
follow-ups remain actionable.

## Candidate identity and receipts

Distribute one immutable candidate archive from a successful full CI run, together
with its artifact manifest/checksums, source commit, dependency graph and CI
provenance. Early fixture results help find defects; repeat affected acceptance
against the final candidate. Verify the downloaded archive and individual tested
binaries against the manifest before starting. Do not rebuild or replace files in a
candidate and reuse its name.

Every receipt records:

- Candidate version, candidate generation/identifier, source commit, full CI run URL
  and artifact manifest digest.
- Each tested archive/application/fixture path and SHA-256, including signed fixture
  hashes after signing; link the signing provenance and entitlements.
- UTC start/end, tester identity, scenario and exact steps; expected and actual
  outcomes, pass/fail/blocked status, logs or recording links and any issue reference.
- OS version, architecture, hardware, JIT/NativeAOT mode, webview/runtime version,
  package manager/toolchain versions and installation commands.
- For Linux, display server/compositor, desktop, GTK and portal/backend versions;
  for accessibility, screen reader/version, locale/IME, display scaling and contrast.
- Cleanup observations, permissions granted/denied and any scenario constraints.

The current authority is `eng/workspace.json`. The sealed candidate is normally
`artifacts/preview/candidate.json`; gate receipts are in
`artifacts/preview/receipts.json`. Under the default `demo-preview` policy this file
is an envelope with `schema: "runic.preview-evidence/1"`, `policy` equal to the sealed
`candidate.acceptancePolicy`, and `receipts` containing the required passing
receipts. A legacy receipt array is accepted only for the `full-v1` policy.
Each receipt uses `gate`, `outcome`,
`candidateDigest`, `source` (full SHA), `artifactHashes` (every candidate package's
`file` mapped to its SHA-256), `environment`, `scenario`, `validator`, `evidence`
(nonempty link/path), and `recordedAt` (ISO timestamp). Only observed successful
results use `outcome: "pass"`. Attach detailed human records and fixture binary
hashes through `evidence`; the package hash map does not replace fixture identity.

[preview/policy.mjs](preview/policy.mjs) defines the candidate-bound `demo-preview`
and `full-v1` profiles; [preview/gates.mjs](preview/gates.mjs) validates them.
The demo human gate IDs are `interactive-native-linux-local` and
`interactive-native-windows-vm`. The policy explicitly records deferred full-profile
gates with `outcome: "deferred"` and the user's scope-change reason. Do not put pass
receipts for deferred gates into the demo evidence envelope.
Interactive receipts require `actualNativeInteraction: true`; the soak requires
`durationSeconds >= 7200`. Performance requires `baselineCommit: "5afb8b8d"`,
`matchedMachineAndWorkload: true` and `unresolvedRegressionsAbove20Percent: 0`.
Accessibility receipts require observed `"pass"` outcomes for `keyboard`,
`screenReader`, `focusRestoration`, `ime`, `highContrast` and `displayScaling`.
Where recorded, accessibility IDs cover NVDA, VoiceOver, Orca X11 and Orca Wayland.
Deferred `pilot-1` and `pilot-2` results must identify different independent validators.
These deferred receipts are not prerequisites under the demo-preview policy. Keep one
current receipt per required gate in the submitted envelope; retain superseded records separately. A filename or checked box alone
is not evidence. Failed, blocked, missing and simulated native scenarios cannot be
promoted to passes. Each rerun identifies which earlier receipt it supersedes.

## Native and application sessions

Run automated JIT and NativeAOT fixtures on Windows x64, Linux x64 and macOS arm64.
Perform actual selected-file and clipboard operations on this Linux system and the
available Windows VM. Record the Linux display server and use its actual backend;
if Wayland is unavailable, defer that manual coverage before v1. Simulated selections
and headless test doubles cannot satisfy required manual checks. Real macOS and the
signed sandbox fixture remain explicit follow-ups before v1.

For each required local manual profile, open a real file, dismiss the dialog, cancel pending work and retry.
Save a new file and replace an existing one. Exercise conflict and permission-denied
paths. Verify atomic replacement or explicit unavailable status before any
modification when sibling staging is unsupported. On macOS verify scoped access
release and the signed sandbox entitlements. On Wayland record the actual portal
backend and test selected-file access; X11 results cannot cover this row.

Read clipboard text, no-text content and empty text distinctly. Test bounded reads,
busy/permission failures, copy/paste across applications and cancellation after a
write begins. On Linux check ownership behavior and resource cleanup after closing
the application. Record actual outcomes, including unsupported cases.

Exercise the customer import/export/copy/paste and document open/edit/save flows
through the real bridge. Edit while operations are pending; verify captured export
revisions, dirty-close protection, cancellation/retry, reconnect without replay and
persistence across process restart. Replace the owning window during pending work;
verify old work is invalidated. Reconnect the presentation and verify valid work is
retained. Close each host with forgotten leases and concurrent work and inspect
cleanup. Verify CS-WebUI unavailable native outcomes and scoped shutdown.

## Local desktop UX and deferred accessibility coverage

Record keyboard-only navigation and focus return after dialogs, dismissal, failures,
reconnect and canceled close on the available systems. Record any locally exercised
accessibility outcomes. Broader screen-reader and profile certification is deferred
before v1: use NVDA on Windows, VoiceOver on macOS and Orca on
Linux, recording announced labels, commands, operation status, errors and dirty-close
decisions. Test IME composition without lost or prematurely committed text, high
contrast, and supported display scaling. Name every tested profile and defect;
blocking application or host defects must be fixed and affected scenarios repeated.

## Packages, performance and deferred independent pilots

Install exact candidate packages outside the checkout with clean consumers. Exercise
both .NET tools, both template packages, React/Vue/Svelte/Angular, Bun/npm/pnpm,
both hosts and default/minimal Desktop. Inspect dependency graphs to verify omitted
providers bring no native dependencies or initialization, and CS-WebUI stays free of
Desktop and ASP.NET Core dependencies. Candidate-only feeds must not silently fall
back to public Runic packages or workspace links.

Measure equivalent existing configurations against green baseline `5afb8b8d` on the
same machine, with repeated startup samples and process-tree memory. Record raw
samples, workload, environment, aggregation and baseline/candidate hashes. Investigate
and fix reproducible regressions above 20%; report provider-enabled deltas separately.
Run a two-hour window/reconnect/cancellation soak. Fail on unreleased resources,
accumulating processes or sustained memory growth and retain observations over time.

As a follow-up before v1, two independent developers each build a small application using candidate packages
and documentation. Each records installation, migration, debugging and lifecycle
experience, commands, blockers and final application evidence. Resolve blocking
findings and repeat affected work against the final candidate. Maintainer-authored
examples and automated consumers do not replace these pilots.

## Registry handoff and final release

The user verifies NuGet ownership in organization `runic-artifex` and scopes for all
new identities and configures
GitHub OIDC trusted publishing for `Runic-Artifex/runic-sdk`. Configure npm trusted
publishers for existing identities with direct `npm publish` permission; new
publishers default to stage-only permission. After inspecting existing connections
with `npm trust list PACKAGE`, add a missing connection with:

```sh
npm trust github PACKAGE --file publish-preview.yml --repo Runic-Artifex/runic-sdk --env preview --allow-publish --yes
```

Use each exact package identity, authenticate interactively with 2FA, and preserve
unrelated existing publishers. Repeat for each new identity after bootstrap.
Follow the current registry instructions:
[NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
and [npm trusted publishers](https://docs.npmjs.com/trusted-publishers/).
Record account/scope verification without including credentials or recovery codes.

After all required demo-preview gates pass, bootstrap the four missing npm identities through the approved
one-time interactive login/2FA publication of the actual verified tarballs, then
configure their trusted publishers. Record the identities found by registry
inspection; do not infer missing identities or publish placeholders. Use npm under
Node for publication, public visibility and the `preview` tag, preserving `latest`.
Bun remains the other JavaScript tooling runner.

The orchestrator selects the successful full CI run for the frozen commit, verifies
its exact artifact set and required demo-preview receipts, and publishes in dependency order through the
dedicated preview workflow. After registry availability, run fresh public-registry
tool, template, installation and application smoke checks. Then create
`v0.2.0-preview.1` and the GitHub prerelease with evidence and checksum links.

If publication partially succeeds, resume only the same immutable set. Compare
registry bytes before skipping existing versions. Changed bytes require a new preview
number. If any required demo-preview gate is incomplete, retain the candidate and a blocker record naming
the owner, missing scenario/access/evidence and next action. Historical receipts
under `eng/release` never substitute for current candidate receipts.
