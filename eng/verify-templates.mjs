import { verifyPackagedTemplateLocks } from './release/template-locks.mjs';
import { nodeCompatibility } from "./node-compatibility.mjs";
import { resolve } from "node:path";
import { root, workspace, run } from "./run.mjs";

const npm = (name) =>
  resolve(root, "artifacts/packages/npm", `${name}-${workspace.version}.tgz`);
const archives = workspace.npm.map(p => npm(p.name.replace("@", "").replace("/", "-")));
const checked = verifyPackagedTemplateLocks(
  resolve(root, "artifacts/packages/nuget", `Runic.Application.Templates.${workspace.version}.nupkg`), archives,
);
console.log(`Verified ${checked.entries} immutable Runic resolutions in ${checked.locks} shipped template locks.`);
run(
  "bash",
  [
    "tests/templates/Test-Templates.sh",
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
  { ...nodeCompatibility().env, RUNIC_VERIFICATION_FEED: resolve(root, "artifacts/packages/nuget") },
);
