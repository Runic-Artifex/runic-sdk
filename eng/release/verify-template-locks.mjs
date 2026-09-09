import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { verifyPackagedTemplateLocks } from "./template-locks.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const workspace = JSON.parse(readFileSync(join(root, "eng/workspace.json"), "utf8"));
const [nuget = join(root, "artifacts/packages/nuget"), npm = join(root, "artifacts/packages/npm")] = process.argv.slice(2);
const result = verifyPackagedTemplateLocks(
  join(nuget, `Runic.Application.Templates.${workspace.version}.nupkg`),
  workspace.npm.map(p => join(npm, `${p.name.replace("@", "").replace("/", "-")}-${workspace.version}.tgz`)),
);
console.log(`Verified ${result.entries} immutable Runic resolutions in ${result.locks} shipped template locks.`);
