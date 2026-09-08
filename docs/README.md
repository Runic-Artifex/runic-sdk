# Runic documentation

The public site is [docs.runic-artifex.eu](https://docs.runic-artifex.eu). This
workspace contains its SvelteKit source plus [product and architecture guides](guides/README.md).
Use the [SDK contributor guide](../CONTRIBUTING.md) for development and verification.

From the SDK root:

```sh
bun run bootstrap
bun run dev:docs
bun run build
```

The package catalog is generated from `eng/workspace.json`; toolchain versions come
from the shared `eng/toolchain.mjs` reader. Run `bun run generate:release-data` after
changing those inputs. `bun run test` checks generated data and rendered routes.
Candidate versions are explicitly unpublished until verified publication evidence
is introduced; a successful local build never establishes availability.

Start with the [preview guide](guides/releases/0.2.0-preview.1.md) and
[guide index](guides/README.md). The root SDK workflow verifies this site. Website
deployment is a separate operation.
