import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { root, workspace } from "../run.mjs";

export const managedGroups = ["application", "assets", "command-line", "translations"];
export function managedTests(base = root) {
  return [...readFileSync(resolve(base, "RunicSdk.Core.slnx"), "utf8").matchAll(/<Project Path="([^"]+)"/g)]
    .map(([, path]) => path)
    .filter(path => /Tests\.csproj$/.test(path) && /<OutputType>Exe<\/OutputType>/.test(readFileSync(resolve(base, path), "utf8")))
    .map(path => ({ path, group: path.includes("Runic.Translations") ? "translations"
      : path.includes("Runic.Assets") ? "assets" : path.includes("Runic.CommandLine") ? "command-line" : "application" }));
}

export function webTests() {
  return workspace.npm.filter(item => JSON.parse(readFileSync(resolve(root, item.path, "package.json"), "utf8")).scripts?.test)
    .map(item => ({ package: item.path.split("/").at(-1), node: item.name === "@runic-artifex/vite-plugin-runic" }));
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url))
  console.log(`web=${JSON.stringify({ include: webTests() })}`);
