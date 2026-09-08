# Preview reliability acceptance

Run these commands in the locked project development shell. The runner never
builds or republishes artifacts. Build the agreed green baseline and the candidate
separately, record the full source revision and SHA-256 of **every** published
file at build time, then retain those immutable directories. Do not assign the
baseline revision to a stale output directory just because the checkout once
used that revision. Build provenance has this form:

```json
{"sourceRevision":"<full 40-character commit>","artifacts":{"CustomerDesktop":"<sha256>","other-file":"<sha256>"}}
```

`artifactHashes(directory)` exported from `run.mjs` computes the file map. The
runner checks it before and after measurement; the build/CI must establish its
source association. Put receipts and provenance **outside** the artifact tree.
All files in the artifact tree must be regular files/directories, with no links.

A measurement config:

```json
{
  "directory": "/absolute/immutable/published-customer",
  "executable": "/absolute/immutable/published-customer/CustomerDesktop",
  "provenance": "/absolute/build-provenance.json",
  "profile": "desktop-default",
  "samples": 15,
  "environment": {"toolchain": "flake.lock sha256", "powerProfile": "performance"}
}
```

```sh
bun eng/reliability/run.mjs measure baseline-config.json baseline.json
bun eng/reliability/run.mjs measure candidate-config.json candidate.json
bun eng/reliability/run.mjs compare baseline.json candidate.json
bun eng/reliability/run.mjs compare candidate.json provider-enabled.json --provider
bun test eng/reliability/metrics.test.mjs
```

This workload starts a package-built customer host in `--serve` mode, waits for
an HTTP response, issues ten requests, samples process-tree memory, and checks
graceful shutdown with no surviving children. It is supplementary server-startup
evidence, not embedded-window startup or accessibility evidence. At least ten
samples are required. Readiness timing is event-driven (workload `customer-serve-http-10-requests-v2`); the earlier v1 polling observations are not comparable. Run matched configurations on the same machine, display
session, toolchain and power profile. Both median and upper-quartile increases
greater than 20% flag a reproducible regression for investigation. A single
outlier does not. Provider deltas are reported separately and do not silently
pass the equivalent-configuration gate.

Linux uses `/proc` RSS, macOS `ps` RSS, and Windows CIM working-set sizes, summed
for the process tree. These include shared resident pages in multiple processes;
they are not unique/private memory. Cross-OS comparisons are rejected. Persistent
native helper processes are included in the native soak. No claim about physical
memory equivalence across different OS metrics is made.

## Native soak duration

Publish `tests/native/Runic.Desktop.WebViewSmoke` using the same verified candidate
source and record its immutable artifact provenance. Use that directory and
executable in a config with these additional fields:

```json
{
  "adapter": "/absolute/runic-sdk/eng/reliability/native-soak-adapter.mjs",
  "profile": "desktop-embedded-default",
  "durationMs": 1800000
}
```

```sh
bun eng/reliability/run.mjs soak native-config.json native-soak.json
```

`durationMs` defaults to 1,800,000 milliseconds (30 minutes). Explicit finite
durations of at least 1,800,000 milliseconds are supported and must finish in
full; the receipt records `requestedDurationMs` and actual elapsed time. Use
`7200000` for the original `full-v1` two-hour gate or an explicitly authorized
longer native lifecycle/regression investigation. The current leak-fix
investigation is limited by the maintainer to 30-minute soaks until a working
fix is established.

The adapter runs the real embedded WebView fixture with `--soak`, retaining one
DesktopHost for the entire run. Every cycle creates native windows, executes the
bridge, reloads and verifies a new document identity, unchanged URL, complete loading and retained session storage, exercises
close veto/retry, cancels queued runtime dispatch, and forgets real runtime
read/save leases and an open staged transaction before scope shutdown. Internal
resource counters, disposed streams, unchanged target bytes, and absence of
staging files are asserted. File selections are controlled test grants: they do
not satisfy native picker selection/sandbox receipts. The ordinary fixture
invocation retains its existing single smoke run behavior.

The driver samples the host and native/browser child processes, rejects growth
in surviving child count, rejects memory growth across every successive quarter or >20% growth between
first/last quarter median cycle memory, and rejects any child surviving the 15-second shutdown deadline (including
asynchronous browser-helper exit after the host exits). Per-cycle process
identities and Linux process names/states are retained, along with natural
shutdown observations. Forced cleanup never satisfies this gate. At least 60
completed cycles and the entire requested duration (at least 30 minutes) are
mandatory. Resource, operation and shutdown checks remain unchanged; the strict runner also retains both memory checks. The
raw cycle records,
artifact hashes, adapter hash, source revision, environment and timing remain
in the receipt, including a failed receipt on error. A shorter debug run cannot
produce a passing release receipt. The `demo-preview` release policy requires
`soak-thirty-minutes` (at least 1,800 seconds); `full-v1` still requires
`soak-two-hours` (at least 7,200 seconds). A passing 30-minute runner receipt
does not satisfy the two-hour gate. Preserve old raw receipts and their original
duration, source and artifact bindings; do not rename or relabel past evidence
to match a new policy. Human native selection, clipboard, screen
reader, IME, scaling, pilots and registry consumption remain separate gates.

The package footprint runner now deletes its task-owned temporary consumer and
caches in `finally`; durable reports and graphs remain under
`artifacts/host-footprint`. Preserve those artifacts before cleanup by the CI job.

## Final candidate procedure

After the frozen commit's complete CI succeeds, download its
`host-footprint-<rid>-<run-id>` artifact. Point this helper at the directory
containing `matrix.json`. It checks the successful main-branch push through the repository CI workflow,
repository and head-repository identity, and current frozen remote main revision.
It then freshly downloads the named artifact from that exact run, checks its
immutable artifact ID/digest before and after download, and compares the complete
local footprint tree with the authenticated download. Reports and provenance
come from that fresh copy, never local report claims. Temporary downloads are
removed after configs are generated using the matching baseline environment:

```sh
bun eng/reliability/prepare-footprint.mjs /absolute/candidate/matrix-directory artifacts/reliability/candidate CI_RUN_ID FULL_COMMIT artifacts/reliability
bun eng/reliability/run.mjs measure artifacts/reliability/candidate/desktop-default-candidate-config.json artifacts/reliability/candidate/desktop-default-candidate.json
bun eng/reliability/run.mjs compare artifacts/reliability/desktop-default-baseline.json artifacts/reliability/candidate/desktop-default-candidate.json
```

Repeat `measure` and `compare` for `desktop-minimal` and `cswebui-default`.
Measure `desktop-default-provider` separately and compare it to the candidate
`desktop-default` receipt using `--provider`. The footprint matrix explicitly
sets `RunicNativeProvider=None` for the three equivalent baseline configurations;
the fourth case statically enables only the build OS provider. Dependency graphs
assert these distinctions. Preserve build SDK/report metadata and investigate
compiler or workload differences before attributing a performance regression.

For historical baseline preparation only, append `--historical-baseline`. This
relaxes the current remote main match while retaining all repository, push,
workflow, artifact download and byte comparisons. Outputs are explicitly labeled
`baseline`; the option does not produce candidate provenance.

## Demo-only residual memory trend

The user explicitly accepted the small residual memory trend as a preview
nonblocker. The strict soak runner still reports failure and exits unsuccessfully
when every quarter median increases. `full-v1` remains strict. This exception
cannot waive the minimum duration, requested longer duration, 60 completed
cycles, actual operations, zero resource counters, process cleanup, natural
shutdown within 15 seconds, or the first/last median 20% growth bound.

A demo soak acceptance receipt may use `outcome: "known-issue"` instead of `pass`,
with `nativeArtifactHashes` bound to the raw native artifact manifest and a
`waiver` object of schema `runic.soak-known-issue/1`. Its `policy` must exactly
match `demoMemoryTrendWaiver` from `eng/release/policy.mjs`; `rawReceiptFile` is
the fixed companion name `native-soak.json` and `rawReceiptSha256` hashes its
original UTF-8 bytes. Keep the original raw file unchanged; do not embed it in
the envelope (which retains its 1 MiB decoder limit). Companion files have a
64 MiB bound and must be plain files in a trusted directory without symlinks. Ordinary candidate,
source, package hash, timestamp and validator bindings still apply. The validator
recomputes cycle and memory checks, checks completed natural shutdown, and
requires the sole raw failure to be `Memory medians grow in every quarter`.
The exception is Linux-only, and last-minus-first quarter median growth must
be at most **5 MiB AND 1%**. The raw receipt must contain `finalArtifactHashes`
identical to its initial artifact hashes. The runner checks final binary bytes
even on failure and never turns that failure into a pass; historical receipts
without this proof are ineligible and must not be retroactively edited.
A waiver cannot be labeled `pass` or applied to another gate. Reseal the candidate
policy and bind fresh evidence when source or artifact bytes change.

Observed context, not transferable acceptance: the patched
`native-atspi-fixed-30m-0c821f62` run completed 5,556 cycles in 1,800,112 ms;
quarter medians were 522.852 / 523.633 / 524.688 / 525.059 MiB (about 0.42%
growth). Resource counters were zero and helpers exited naturally in 100.36 ms.
A system-package contribution is suspected, not established as the sole cause:
the patched run still grew. This is an acknowledged residual issue, not a
verified leak fix. Do not substitute that historical run for a changed source.

Transport contract: prepare a public-safe data-only commit in the canonical
repository containing only `native-soak.json`. Supply its full immutable commit
ID as `evidence_commit` to the evidence workflow. `fetch-companion` reads the
commit/tree/blob through the GitHub API, verifies the regular file, size, Git
object hash and receipt SHA-256, and writes the original bytes plus transport
metadata. It never checks out or executes the evidence commit. The raw soak's
source must still match the frozen candidate source, not the evidence commit.

The evidence workflow retains the companion alongside the validated candidate
and receipts in `preview-acceptance`. Publication consumes that retained
artifact and rechecks the raw hash and gate constraints. The temporary evidence
branch can be removed after artifact retention is verified; it must contain no
private data. When there is no known issue, leave `evidence_commit` empty.
Missing companion context fails closed. Local `gates` and `publish` commands
accept the reviewed companion directory as an optional final argument.
