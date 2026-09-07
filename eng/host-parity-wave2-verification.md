# Host selection verification — 2026-09-07

Verified on this NixOS Linux x64 machine through the repository's `nix develop`
environment (.NET SDK 10.0.302, Node 24.18.0, Bun 1.4.0 and pinned Chromium).

- CLI tests: 23 passed. Asset tests: 25 passed.
- Real development acceptance: CS-WebUI followed by Desktop, selecting each
  with `dev --host`, restoring its dependencies, connecting the customer editor,
  editing CSS through Vite and preserving the unsaved draft through HMR.
- Template acceptance: default React and Svelte, all four frontends with each
  host, and all four frontends with pnpm and Bun. Builds, typechecks, incremental
  rebuilds, generated manifests and 17 application smoke tests passed.
- Package canaries verify isolated package consumers and reject Desktop and
  ASP.NET Core dependencies in the CS-WebUI runtime graph.

The development tests caught and corrected buffered CLI output, restore ordering
when changing host, and native bootstrap URLs on isolated Desktop surfaces.
The Release package canary now uses its selected configuration consistently.

These checks do not claim native window feature equivalence. Native close veto
remains a Desktop enhancement, and the CS-WebUI integration owns the native
runtime for the lifetime of one process. Footprint measurements follow separately.
