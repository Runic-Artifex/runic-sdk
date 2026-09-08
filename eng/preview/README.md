# Current preview release tooling

`eng/workspace.json` is the current package authority. `eng/release` retains historical receipts and is never read by these commands. The current authority must contain exactly 27 NuGet and 8 npm identities at `0.2.0-preview.1`; implementation tools and generators are excluded.

Use the locked development environment for Bun, Python archive inspection, `gh`, npm (running under Node), and dotnet. Run focused checks with `bun test eng/preview/contracts.test.mjs`.

## Acceptance profiles

The currently authorized profile is `demo-preview`. It requires actual selected-file and clipboard interaction on **local Linux** (`interactive-native-linux-local`) and the **available Windows VM** (`interactive-native-windows-vm`). The original Windows/X11/Wayland/macOS/signed-sandbox human profiles, NVDA/VoiceOver/Orca accessibility profiles, and both independent pilots are explicitly recorded as `deferred`, with the user's scope-change authorization. These are not passing results or verified support claims. The local Linux receipt must identify the display system and backend actually tested; it does not establish coverage of another backend.

All engineering checks remain required, including full CI, JIT and NativeAOT on all three operating systems, core/application/package tests, isolation, matched performance, two-hour soak, and independent review. The `full-v1` profile retains the original full acceptance requirements. `policy.mjs` is the exact supported policy definition; arbitrary missing gates or edited policy records are rejected.

`seal` defaults to `demo-preview`; append `full-v1` to select the original policy. The complete policy, including each deferred gate and its rationale, is sealed into `candidate.acceptancePolicy`. Changing profiles requires resealing and rebinding acceptance evidence.

Create a demo evidence envelope without inventing any passing receipts:

```sh
bun -e 'import {readFileSync,writeFileSync} from "node:fs"; const c=JSON.parse(readFileSync("artifacts/preview/candidate.json","utf8")); writeFileSync("artifacts/preview/receipts.json",JSON.stringify({schema:"runic.preview-evidence/1",policy:c.acceptancePolicy,receipts:[]},null,2),{flag:"wx"})'
```

The `policy` object has schema `runic.preview-policy/1`, with `profile`, `authorization`, `required` gate IDs, and `deferred` records containing `gate`, `outcome: "deferred"`, and `reason`. Every required receipt retains the fields documented below. The evidence policy must exactly equal the sealed policy. Deferred checks must not appear as passing receipts. The legacy receipt array is supported only with `full-v1`.

## Candidate preparation

Pack npm manifests with `gitHead` set to the full source commit and NuGet nuspec repository metadata with that same commit. Repository URLs must resolve to `https://github.com/Runic-Artifex/runic-sdk`. Internal package dependencies must pin this exact preview. Place only distributable `.nupkg` files in `artifacts/packages/nuget` and only `.tgz` files in `artifacts/packages/npm`. No additional files or directories are accepted inside `artifacts/packages`.

After a frozen commit has a successful full push CI run:

```sh
mkdir -p artifacts/preview
bun eng/preview/cli.mjs seal artifacts/packages FULL_SOURCE_SHA CI_RUN_ID artifacts/preview/candidate.json
bun eng/preview/cli.mjs verify artifacts/packages artifacts/preview/candidate.json
bun eng/preview/cli.mjs gates artifacts/preview/candidate.json artifacts/preview/receipts.json
bun eng/preview/cli.mjs final-ci artifacts/preview/candidate.json
```

Sealing refuses an existing destination. It records exact package hashes, source revision, metadata, dependency graph, and CI run identity. It queries GitHub to require a successful push run of `.github/workflows/ci.yml` on the current remote default branch `main`, with the candidate equal to its current HEAD. The exact full job inventory is expanded from the frozen workflow and web test matrix; every job, matrix entry, and final `verify` must be present exactly once and successful. It downloads that run's package artifact and verifies that every package byte matches. A local rebuild cannot substitute for the CI artifact. Human results must reference this candidate's digest and package hashes.

`gates.mjs` defines the required receipt fields and gate identifiers. Receipts and the policy envelope are supplied by the responsible validators. No command manufactures passing receipts. Each record includes `gate`, `outcome`, `candidateDigest`, full `source`, `artifactHashes` (every candidate filename to SHA-256), `environment`, `scenario`, `validator`, `evidence`, and `recordedAt`. Performance, soak, interactive native, and accessibility gates have additional fields enforced in that module. Review the underlying evidence, not just the declared outcome, before accepting receipts or uploading the `preview-acceptance` artifact. Its two files are `candidate.json` and `receipts.json`.

## Publication

The drafts `eng/preview/publish-preview.yml` and `eng/preview/preview-evidence.yml` must be installed under `.github/workflows/` with the same filenames by the integration owner before the final source freeze. Configure the GitHub `preview` environment, NuGet organization `runic-artifex` ownership and new-package scopes, `vars.NUGET_USER` (the NuGet profile username, not an email address), and trusted publishers for repository `Runic-Artifex/runic-sdk`, workflow `publish-preview.yml`, environment `preview`. npm publishing uses npm under Node, never Bun's publisher.

The four initially missing npm identities (`@runic-artifex/application-bridge-tooling`, `@runic-artifex/angular`, `@runic-artifex/desktop`, and `@runic-artifex/vite-plugin-runic`) require the approved one-time interactive login/2FA bootstrap with the verified candidate archives. The registry inventory identifies those four names; never infer them from a failed publish. Before each bootstrap, run all four verification commands above and verify every already-published candidate package against registry contents. Run `npm login --registry=https://registry.npmjs.org` interactively, then `npm publish EXACT_VERIFIED_ARCHIVE --tag preview --access public --registry=https://registry.npmjs.org`. Configure that identity's trusted publisher immediately afterward. Do not create placeholder packages. Interactive local bootstrap does not claim GitHub OIDC provenance.

Create the evidence artifact with `preview-evidence.yml` at the frozen source commit. Supply `ci_run_id` and `receipts_json` containing the reviewed envelope. The workflow downloads that exact run's packages, seals them against successful full CI, parses the input as JSON through an environment variable, checks every required gate, and uploads `preview-acceptance` only on success. It generates no passing human results. Local `seal` produces the same deterministic candidate digest for preparing the receipts beforehand.

For NuGet, select organization `runic-artifex` as trusted policy owner and configure the matching profile username in repository variable `NUGET_USER`; the workflow's `NuGet/login` user field is not an email address or an API key. Configure ownership/scopes on nuget.org according to the official trusted-publishing instructions.

The publishing workflow accepts the CI run ID and a run holding the reviewed `preview-acceptance` artifact. It verifies both before exchanging an OIDC token through `NuGet/login@v1`. npm automatically uses GitHub OIDC. Existing registry versions are skipped only after complete byte comparison (npm) or comparison of every unsigned package entry (NuGet, allowing the registry-added signature). Any differing content requires a new preview number. All existing packages are checked before any new publication begins. Re-run only the same frozen commit and immutable candidate if publication partially succeeds.

`bun eng/preview/cli.mjs registry artifacts/preview/candidate.json` verifies public availability and contents. Registry indexing may lag. This command retries 404 availability responses and transient 429/503 responses with `Retry-After`, bounded to 12 attempts and three minutes per HTTP lookup. A longer server-requested wait fails without retrying early. Content mismatches fail immediately. Rerun the read-only check later if indexing exceeds that budget. This check alone is **not** the public install/template/tool/application smoke gate. The integration owner must run the independent clean public-registry consumption scenarios after availability, retain their evidence, and only then create `v0.2.0-preview.1` and the GitHub prerelease. This draft deliberately does not create a tag or release automatically. Existing npm `latest` tags are preserved by publishing solely with `preview`.

Sources: [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing), [npm trusted publishing](https://docs.npmjs.com/trusted-publishers/), checked September 8, 2026.
