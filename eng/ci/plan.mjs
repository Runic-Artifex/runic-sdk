import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { root, workspace } from "../run.mjs";

export const managedGroups = ["platform", "assets", "command-line", "translations", "application"];
export function managedTests(base = root, platform = process.platform) {
  return [...readFileSync(resolve(base, "RunicSdk.Core.slnx"), "utf8").matchAll(/<Project Path="([^"]+)"/g)]
    .map(([, path]) => path)
    .filter(path => {
      if (!/Tests\.csproj$/.test(path)) return false;
      const project = readFileSync(resolve(base, path), "utf8");
      return /<OutputType>Exe<\/OutputType>/.test(project)
        && (platform === "win32" || !/<TargetFramework>[^<]*-windows<\/TargetFramework>/.test(project));
    })
    .map(path => ({ path, group: path.includes("Runic.Application.") ? "application"
      : path.includes("Runic.Translations") ? "translations"
      : path.includes("Runic.Assets") ? "assets"
      : path.includes("Runic.CommandLine") || path.startsWith("examples/command-line/") ? "command-line"
      : "platform" }));
}

export function webTests() {
  return workspace.npm.filter(item => JSON.parse(readFileSync(resolve(root, item.path, "package.json"), "utf8")).scripts?.test)
    .map(item => ({ package: item.path.split("/").at(-1), node: item.name === "@runic-artifex/vite-plugin-runic" }));
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url))
  console.log(`web=${JSON.stringify({ include: webTests() })}`);
