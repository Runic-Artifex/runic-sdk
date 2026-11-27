## `dotnet runic doctor` checks the JavaScript workspace and the library packages that the page imports

`doctor` gains four checks, each with a fix:

- **`frontend-workspace`**: the JavaScript workspace root that installs the frontend, found as the build finds it, and
  that the root's `workspaces` (or `pnpm-workspace.yaml`) list the frontend (the build's `RUNICBRIDGE087`). A frontend
  with its own lock file below a workspace warns, since it installs on its own.
- **`package-manager-agreement`**: the root's `packageManager` and lock files, `RunicApplicationFrontendPackageManager`,
  and the `packageManager` of the frontend, the workspace's members and the packages that the page depends on name one
  manager (the build's `RUNICBRIDGE089`).
- **`frontend-dependencies`**: every client library's package and workspace package that the page depends on is built,
  or is built by `dotnet build` and `dev`; a package whose exports are missing and whose build is off, or a source
  package that exports a file nothing writes, fails. Doctor reads the packages from `RunicViewsGetFrontendDependencies`
  without a build.
- **`windows-targeting`**: on Linux and macOS, a project of a Windows target framework, such as a WPF project on
  `net10.0-windows`, needs `EnableWindowsTargeting=true` at restore time, or the restore fails with `NETSDK1100`. The fix
  is the line for the application's `Directory.Build.props`.

See [Doctor checks](../../../tools/dotnet-runic/README.md#doctor-checks).
