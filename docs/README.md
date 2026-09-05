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

The site keeps its package catalog and release status generated from the shared,
pinned authority described in `scripts/release-authority.mjs`. `bun run test` checks
that generated release data is current and verifies the rendered routes. Publication
status comes from release evidence, never from a successful local build. The original
release receipts under `eng/release` retain their historical meaning.

The build produces static files in `docs/build`. Root CI owns verification; former
standalone deployment workflows are preserved in `eng/archive/docs`. Deploying the
site is a separate release operation. The independent `runic-site` repository owns
the project landing page at [runic-artifex.eu](https://runic-artifex.eu).
