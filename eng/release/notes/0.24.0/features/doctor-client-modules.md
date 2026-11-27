## `dotnet runic doctor` checks how the page's library imports resolve

`doctor` gains two checks, each with a fix:

- **`client-modules`**: each specifier that the page imports from a library or workspace package resolves from the
  frontend as Vite resolves it: every `RunicApplicationFrontendClientModule` specifier to a file that the package's
  `exports` name and that exists, and every package that the frontend's `package.json` declares to the workspace's
  package rather than another copy. A tsconfig `paths` alias counts when the Vite configuration has a matching alias.
- **`views-dedupe`**: when a linked package that the page depends on imports `@runic-artifex/views`, the Vite
  configuration should set `resolve.dedupe: ["@runic-artifex/views"]`, so the page loads one copy. Doctor names the
  two copies when the install has them.

See [Doctor checks](../../../tools/dotnet-runic/README.md#doctor-checks).
