- **`dotnet runic dev` builds the library packages that the page imports.** `dev` turned the frontend build off with a
  global property, which reached every project it built, so a client library's compiled frontend package was never
  built under `dev`: on a fresh clone Vite logged `Failed to resolve import "@acme/shared-ui"` over a thousand times and
  `dev` stopped after 30 s with `RAPPDEV1007`. `dev` now names the project it serves in
  `RunicApplicationFrontendDevSessionProject`, and only that project skips its production frontend build; libraries
  build their packages, so `dev` reaches Ready from a fresh clone.
- **`dotnet runic dev` no longer reinstalls the frontend packages on every start.** It ran the package manager's install
  itself before each build (`npm ci` deletes and reinstalls `node_modules`, and the clients were then regenerated).
  The build's incremental install now runs instead, so a start with nothing changed runs no install: in the
  shared-library example's npm workspace a warm start takes 3.5 s instead of 4.3 s and starts 3 JavaScript processes
  (the Vite configuration probe, `npm run dev` and Vite) instead of 8.
- **`dotnet runic dev` starts in a Bun or pnpm workspace.** Its own install took the package manager from the
  frontend's folder, so a member without `packageManager` was installed with npm and failed (`RAPPDEV1006`). The build
  installs the workspace at its root with the workspace's manager.
