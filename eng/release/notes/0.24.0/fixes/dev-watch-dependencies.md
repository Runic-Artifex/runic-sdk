- **`dotnet runic dev` runs the development server with the workspace's package manager.** In a Bun or pnpm workspace
  whose frontend declares no `packageManager`, `dev` ran `npm run dev`, because it read only the frontend's folder. It
  now finds the workspace root as the build does, with the same order: `RunicApplicationFrontendPackageManager`, the
  root's `packageManager`, the root's lock file, then the frontend's own.
- **`dotnet runic dev` reads the targets' dev session protocol after restoring a fresh clone.** It checked for the
  restore's assets file after the restore had written it, so an unrestored project that takes the Views targets from
  its packages fell back to the pre-0.24 behaviour (`RAPPDEV1019`).
