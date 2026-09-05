# Workspace engineering

`run.mjs` is the active entrypoint. `workspace.json` defines package identity,
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

For remote cutover, create the intended `Runic-Artifex/runic-sdk` repository and
push `main` with its complete merge ancestry and namespaced tags. No old repository
needs to be deleted. Preserve imported branch tips too if their additional branch
history is wanted remotely. Then run the root CI on the new remote before preparing
new compatibility and release attestations. Archiving old repositories or publishing
packages is a separate decision.

The next structural task is the [repository reorganisation](repository-reorganisation.md),
sequenced after native close interception. It defines the destination layout, move
order and verification criteria without rewriting import or release evidence.
