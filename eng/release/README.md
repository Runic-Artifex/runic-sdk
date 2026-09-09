# Release tooling

`eng/workspace.json` is the package and version authority. Every candidate must contain exactly its NuGet and npm identities; matching counts with different names are rejected. Implementation-only tools and generators are excluded by that inventory.

Use the locked development environment for Bun, Python archive inspection, `gh`, npm (running under Node), and dotnet. Run focused checks with `bun test eng/release/contracts.test.mjs`.

## Acceptance profiles

The currently authorized profile is `demo-preview`. It requires actual selected-file and clipboard interaction on **local Linux** (`interactive-native-linux-local`) and the **available Windows VM** (`interactive-native-windows-vm`). The original Windows/X11/Wayland/macOS/signed-sandbox human profiles, NVDA/VoiceOver/Orca accessibility profiles, and both independent pilots are explicitly recorded as `deferred`, with the user's scope-change authorization. These are not passing results or verified support claims. The local Linux receipt must identify the display system and backend actually tested; it does not establish coverage of another backend.

All engineering checks remain required, including full CI, JIT and NativeAOT on all three operating systems, core/application/package tests, isolation, matched performance, a native soak, and independent review. The user-authorized `demo-preview` profile requires `soak-thirty-minutes` with at least 1,800 elapsed seconds. The `full-v1` profile retains `soak-two-hours` with at least 7,200 elapsed seconds and all other original requirements. Resource, operation and shutdown criteria remain required; the explicit demo-only memory-trend exception below does not waive the 20% growth bound. `policy.mjs` is the exact supported policy definition; arbitrary missing gates or edited policy records are rejected.

`seal` defaults to `demo-preview`; append `full-v1` to select the original policy. The complete policy, including each deferred gate and its rationale, is sealed into `candidate.acceptancePolicy`. Changing profiles or the supported policy requires resealing and rebinding acceptance evidence. Retain old raw soak receipts unchanged: a policy update does not rename their gate, shorten their recorded duration or associate them with different source/artifact bytes. A 30-minute receipt cannot satisfy `full-v1`.

Create a demo evidence envelope without inventing any passing receipts:

```sh
bun -e 'import {readFileSync,writeFileSync} from "node:fs"; const c=JSON.parse(readFileSync("artifacts/preview/candidate.json","utf8")); writeFileSync("artifacts/preview/receipts.json",JSON.stringify({schema:"runic.preview-evidence/1",policy:c.acceptancePolicy,receipts:[]},null,2),{flag:"wx"})'
```

The `policy` object has schema `runic.preview-policy/1`, with `profile`, `authorization`, `required` gate IDs, and `deferred` records containing `gate`, `outcome: "deferred"`, and `reason`. Every required receipt retains the fields documented below. The evidence policy must exactly equal the sealed policy. Deferred checks must not appear as passing receipts. The legacy receipt array is supported only with `full-v1`.

## Candidate preparation

Pack npm manifests with `gitHead` set to the full source commit and NuGet nuspec repository metadata with that same commit. Repository URLs must resolve to `https://github.com/Runic-Artifex/runic-sdk`. Internal package dependencies must pin this exact preview. Place only distributable `.nupkg` files in `artifacts/packages/nuget` and only `.tgz` files in `artifacts/packages/npm`. No additional files or directories are accepted inside `artifacts/packages`.

Pack the final npm archives before the NuGet application templates. Their npm,
pnpm and Bun locks are stamped in disposable staging files using those archive
bytes; source locks remain development inputs. `bun eng/release/verify-template-locks.mjs`
checks all twelve locks inside the completed NuGet package against the final npm
archives. Candidate-feed acceptance may redirect npm download URLs, but must never
repair shipped versions or integrity hashes.

After a frozen commit has a successful full push CI run:

```sh
mkdir -p artifacts/preview
bun eng/release/cli.mjs seal artifacts/packages FULL_SOURCE_SHA CI_RUN_ID artifacts/preview/candidate.json
bun eng/release/cli.mjs verify artifacts/packages artifacts/preview/candidate.json
bun eng/release/cli.mjs gates artifacts/preview/candidate.json artifacts/preview/receipts.json
bun eng/release/cli.mjs final-ci artifacts/preview/candidate.json
```

Sealing refuses an existing destination. It records exact package hashes, source revision, metadata, dependency graph, and CI run identity. It queries GitHub to require a successful push run of `.github/workflows/ci.yml` on the current remote default branch `main`, with the candidate equal to its current HEAD. The exact full job inventory is expanded from the frozen workflow and web test matrix; every job, matrix entry, and final `verify` must be present exactly once and successful. It downloads that run's package artifact and verifies that every package byte matches. A local rebuild cannot substitute for the CI artifact. Human results must reference this candidate's digest and package hashes.

`gates.mjs` defines the required receipt fields and gate identifiers. Receipts and the policy envelope are supplied by the responsible validators. No command manufactures passing receipts. Each record includes `gate`, `outcome`, `candidateDigest`, full `source`, `artifactHashes` (every candidate filename to SHA-256), `environment`, `scenario`, `validator`, `evidence`, and `recordedAt`. Performance, soak, interactive native, and accessibility gates have additional fields enforced in that module. Review the underlying evidence, not just the declared outcome, before accepting receipts or uploading the `preview-acceptance` artifact. Its two files are `candidate.json` and `receipts.json`.

## Publication

The publication and evidence workflows are installed under `.github/workflows/`; these are the only maintained workflow definitions. Configure the GitHub `preview` environment, NuGet organization `runic-artifex` ownership and new-package scopes, `vars.NUGET_USER` (the NuGet profile username, not an email address), and trusted publishers for repository `Runic-Artifex/runic-sdk`, workflow `publish-preview.yml`, environment `preview`. npm publishing uses npm under Node, never Bun's publisher.

New npm trusted publishers must explicitly allow direct `npm publish`; the registry's default stage-only permission does not authorize this workflow. After interactive login/2FA, inspect existing publishers with `npm trust list PACKAGE`. If the required publisher is absent, configure each existing package (and each new identity after its verified bootstrap):

```sh
npm trust github PACKAGE --file publish-preview.yml --repo Runic-Artifex/runic-sdk --env preview --allow-publish --yes
```

Replace `PACKAGE` with an exact inventory name. Preserve unrelated existing publishers. See the [npm trust command](https://docs.npmjs.com/cli/v12/commands/npm-trust/) for interactive authentication requirements.

New npm identities require the approved one-time interactive login/2FA bootstrap with the verified candidate archives. Determine missing identities from the current registry inventory; never infer them from a failed publish. Before each bootstrap, run all four verification commands above and verify every already-published candidate package against registry contents. Run `npm login --registry=https://registry.npmjs.org` interactively, then `npm publish EXACT_VERIFIED_ARCHIVE --tag preview --access public --registry=https://registry.npmjs.org`. Configure that identity's trusted publisher immediately afterward. Do not create placeholder packages. Interactive local bootstrap does not claim GitHub OIDC provenance.

Create the evidence artifact with `preview-evidence.yml` dispatched at the same frozen source commit as successful full CI. Supply `ci_run_id` and `receipts_gzip_base64`: the reviewed envelope encoded as UTF-8, gzip, then base64. Pass input through the environment, never executable shell interpolation or a remote evidence URL. For example, encode the completed local envelope without altering its contents:

```sh
python3 -c 'import base64,gzip,pathlib,sys; sys.stdout.write(base64.b64encode(gzip.compress(pathlib.Path(sys.argv[1]).read_bytes())).decode("ascii"))' artifacts/preview/receipts.json > artifacts/preview/receipts.gzip.base64
```

The encoded input is limited to 60,000 characters and the expanded envelope to 1 MiB. The decoder rejects malformed base64/gzip/UTF-8/JSON, duplicate object keys, non-finite numbers and an incorrect envelope schema. It preserves the exact decompressed bytes and refuses to overwrite a receipt. A decoded envelope is not acceptance: the unchanged gate validator must still verify every outcome, policy, source, digest and artifact hash before upload.

The workflow downloads the specified CI packages and seals them with `$GITHUB_SHA`, the selected workflow source. Existing CI verification rejects a different source, stale main HEAD, incomplete jobs or substituted artifact bytes. No previous release commit, run or candidate digest is hardcoded. Local `seal` produces the same deterministic candidate digest for preparing receipts beforehand. The workflow generates no passing human results and uploads `preview-acceptance` only after all required gates pass.

For NuGet, select organization `runic-artifex` as trusted policy owner and configure the matching profile username in repository variable `NUGET_USER`; the workflow's `NuGet/login` user field is not an email address or an API key. Configure ownership/scopes on nuget.org according to the official trusted-publishing instructions.

The publishing workflow accepts the CI run ID and a run holding the reviewed `preview-acceptance` artifact. It verifies both before exchanging an OIDC token through `NuGet/login@v1`. npm automatically uses GitHub OIDC. Existing registry versions are skipped only after complete byte comparison (npm) or comparison of every unsigned package entry (NuGet, allowing the registry-added signature). Any differing content requires a new preview number. All existing packages are checked before any new publication begins. Re-run only the same frozen commit and immutable candidate if publication partially succeeds. All preview publication runs share one concurrency group, including runs selected from different refs or versions.

`bun eng/release/cli.mjs registry artifacts/preview/candidate.json` verifies public availability and contents. Registry indexing may lag. This command retries 404 availability responses and transient 429/503 responses with `Retry-After`, bounded to 12 attempts and three minutes per HTTP lookup. A longer server-requested wait fails without retrying early. Content mismatches fail immediately. Rerun the read-only check later if indexing exceeds that budget. This check alone is **not** the public install/template/tool/application smoke gate. The integration owner must run the independent clean public-registry consumption scenarios after availability, retain their evidence, and only then create the versioned tag and GitHub prerelease matching the sealed candidate. The workflow does not create a tag or release automatically. Existing npm `latest` tags are preserved by publishing solely with `preview`.

Sources: [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing), [npm trusted publishing](https://docs.npmjs.com/trusted-publishers/), checked September 8, 2026.

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
