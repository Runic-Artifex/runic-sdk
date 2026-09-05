import { resolve } from "node:path";
import { root, workspace, run } from "./run.mjs";

const npm = (name) =>
  resolve(root, "artifacts/packages/npm", `${name}-${workspace.version}.tgz`);
run(
  "bash",
  [
    "packages/runic-toolkit/tests/RunicToolkit.TemplateAcceptance/Test-Templates.sh",
    workspace.version,
    resolve(root, "artifacts/packages/nuget"),
    npm("runic-artifex-application-bridge"),
    npm("runic-artifex-application-bridge-tooling"),
    npm("runic-artifex-angular"),
    npm("runic-artifex-svelte"),
    npm("runic-artifex-vite-plugin-runic"),
    npm("runic-artifex-desktop"),
  ],
  root,
  { RUNIC_VERIFICATION_FEED: resolve(root, "artifacts/packages/nuget") },
);
