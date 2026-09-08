# Workspace engineering

`run.mjs` supplies focused build and package commands. Verification is defined in
`.github/workflows/ci.yml`; [ci/local.mjs](ci/README.md) runs that workflow locally
with `act`. `workspace.json` defines package identity,
artifact paths, and the component dependency graph. Source builds use project and
workspace links; `verify-packages.mjs` exercises independent installation from
packed artifacts in a temporary directory outside the checkout.

The imported per-product `eng` directories contain useful targeted checks alongside
historical multi-repository release orchestration. Use root commands for unified
builds and release candidates. The root workflow needs no Runic package registry
credentials and never publishes packages.

Release and source control boundaries:

- `Versions.props`: current shared .NET candidate version.
- `workspace.json`: current artifact inventory and dependency ownership.
- `release/`: retained publication authority, schemas, and validators. Its original
  source revisions are historical evidence and must not be relabeled as validation
  of a new monorepo commit.
- `migration/imports.json`: original repository heads, worktree digests, and paths.

Current preview preparation is described in the
[release guide](../docs/guides/releases/0.2.0-preview.1.md) and
[human acceptance handoff](preview-human-acceptance.md). The current monorepo release
definition is derived from `workspace.json`; preserve imported release receipts and
source pins as history. Full CI is the verification authority. A separate preview
publication workflow must select the frozen commit's successful run and verify the
immutable artifacts and required demo-preview acceptance receipts before publication.

Registry ownership under NuGet organization `runic-artifex` and manual sessions on
this Linux system and the available Windows VM require user-coordinated evidence.
Automated native CI remains required on all three OS targets. Real macOS interaction,
unavailable Wayland checks, broader accessibility and independent pilots are deferred
before v1; record them as follow-ups, not demo-preview blockers or passes. Missing
required gates delay publication. Candidate-only
checks do not establish public-registry availability, and public installation smoke
checks follow successful registry publication.
