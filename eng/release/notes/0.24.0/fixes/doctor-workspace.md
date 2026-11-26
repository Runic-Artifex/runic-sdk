- **`dotnet runic doctor` checks the workspace's package manager and lock file.** In a Bun or pnpm workspace whose
  frontend declares no `packageManager` and has no lock file of its own, `javascript-runtime` and `package-manager`
  checked Node and npm, and `lock-file` failed for a missing `package-lock.json` in the frontend's folder. They now use the
  manager that the build uses, from `RunicApplicationFrontendPackageManager`, the workspace root's `packageManager` or
  lock file, then the frontend's own, and `lock-file` looks for the lock at the workspace root. An unsupported
  `packageManager` now fails the check instead of stopping doctor.
