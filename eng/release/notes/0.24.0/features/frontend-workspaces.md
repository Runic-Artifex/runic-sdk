## Frontends in JavaScript workspaces

A frontend that is a member of a Bun, npm or pnpm workspace, such as an application's frontend beside the frontend
package of a client library, now builds from a fresh clone with plain `dotnet build`. The build finds the workspace root
(the nearest folder above the frontend whose `package.json` has `workspaces`, or that has `pnpm-workspace.yaml`) and:

- installs once at the root, with the root's lock file (`--frozen-lockfile` or `npm ci`, with `--ignore-scripts`), for
  every member. Builds of the workspace's projects that run at the same time wait for one install, and a build with
  nothing changed runs none;
- takes the package manager from the root's `packageManager` or lock file;
- checks that the workspace lists the frontend (`RUNICBRIDGE087`), and warns about a member that declares another
  package manager (`RUNICBRIDGE089`).

`RunicApplicationFrontendWorkspaceRoot` names the root, or `none` installs a frontend on its own. A frontend with a lock
file of its own, as the `runic-app` template creates, is its own root and builds as before. The build resolves the
workspace with `runic-frontend-graph.mjs`, which writes `obj/runic/frontend-graph.json` (`rax.frontend-graph/1`); the
same script is the `@runic-artifex/views/frontend-graph` export. See
[JavaScript workspaces](../../../packages/dotnet/Runic.Application.Views/README.md#javascript-workspaces).

### Faster rebuilds

A rebuild with nothing changed runs no package manager at all, also not its `--version`. The install runs when its
hash changed: the install command, the lock file, every `package.json` of the workspace and the package manager's
executable. It also runs when `node_modules` is gone or the manager's own record of a complete install is missing (npm's
`.package-lock.json`, pnpm's `.modules.yaml`). A touched but unchanged lock file no longer installs, and an npm workspace
no longer installs on every build. The new `RunicApplicationFrontendInstallMode` property chooses `Auto` (the default),
`Always` (install on every build) or `Never` (the same as `RunicApplicationFrontendInstallEnabled=false`, which keeps
working). The frontend build reruns after an install that installed something else. See
[Fast iteration](../../../packages/dotnet/Runic.Application.Views/README.md#fast-iteration).
