- **A workspace member's build no longer asks for a lock file it cannot have.** A frontend in a Bun, npm or pnpm
  workspace failed with `RUNICBRIDGE026` ("has no bun.lock … Create the lock file with 'bun install'"), because the build
  looked for the lock file only in the frontend's folder; the advice could not be followed, as the install in a member
  writes the root's lock. The build now uses the root's lock file, and `RUNICBRIDGE026` names the root when it is
  missing.
- **The Views guide no longer tells a workspace to turn the install off.** Its advice to set
  `RunicApplicationFrontendInstallEnabled=false` in a workspace made a fresh clone fail with `RUNICBRIDGE084`, because
  nothing installed the workspace. The build now installs it at the root.
- **A member without a lock file of its own installs with its workspace's package manager.** A member that declared no
  `packageManager` was installed with npm, which fails in a Bun or pnpm workspace on `workspace:*`
  (`npm error Unsupported URL Type "workspace:"`).
- **An npm workspace builds once and stays installed.** npm installs every member's packages into the root's
  `node_modules`, so the member's build failed with `RUNICBRIDGE016` ("did not create …/node_modules") and installed
  again on every build. The build checks the root's `node_modules`.
- **A missing package manager is named before the install.** Without Bun (or Node.js) on `PATH`, the install failed
  with `RUNICBRIDGE016` and exit code 127. The build now runs the manager's `--version` before the install and reports
  `RUNICBRIDGE090` with the manager and where it was declared; a frontend build that exits with 127 because the manager
  is missing is reported the same way.
