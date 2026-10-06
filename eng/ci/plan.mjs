import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { root, workspace, affectedComponents, engineeringOnly } from "../run.mjs";

export const managedGroups = ["platform", "assets", "application"];
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
      : path.includes("Runic.Assets") ? "assets"
      : "platform" }));
}

export function webTests() {
  return workspace.npm.filter(item => JSON.parse(readFileSync(resolve(root, item.path, "package.json"), "utf8")).scripts?.test)
    .map(item => ({ package: item.path.split("/").at(-1), node: item.name === "@runic-artifex/vite-plugin-runic" }));
}

// Jobs that always run: `plan`, `build` (which also checks generated files) and
// `engineering` (workflow lint, contracts, Markdown links). `verify` accepts a
// skipped job only when it is listed here and the plan skipped it.
export const skippableJobs = ["managed", "web", "framework-consumers", "views", "packages", "package-consumers", "templates", "native"];

const owner = file => Object.entries(workspace.components)
  .find(([, component]) => component.paths.some(path => file === path || file.startsWith(`${path}/`)))?.[0];

// Which components a change affects, judged by this checkout's component map.
// `files` lists every path a pull request adds, changes, deletes or renames (both
// names); `null` means unknown. Any unowned path that is not engineering-only
// (see engineeringOnly in eng/run.mjs) affects everything.
export function affected(files) {
  const full = files === null || files.some(file => !engineeringOnly(file) && !owner(file));
  return { full, components: full ? Object.keys(workspace.components) : affectedComponents(files) };
}

// The jobs and suites to run for the affected components, as defined by this checkout.
export function planFor({ full, components }) {
  const has = (...names) => names.some(name => components.includes(name));
  const managed = managedGroups.filter(group =>
    managedTests(root, "linux").some(test => test.group === group && (full || has(owner(test.path)))));
  const web = webTests().filter(test => full || has(owner(`packages/web/${test.package}`)));
  // Package candidates, their consumers and templates cover every shipped component.
  const packages = full || components.length > 0;
  const run = {
    managed: managed.length > 0,
    web: web.length > 0,
    "framework-consumers": full || has("svelte"),
    views: full || has("examples"),
    packages,
    "package-consumers": packages,
    templates: packages,
    // Desktop contracts, native windows, platform services, NativeAOT Views apps and Windows administration.
    native: full || has("desktop", "platform", "application", "examples", "administration-windows"),
  };
  return {
    full,
    components: full ? Object.keys(workspace.components) : components,
    managed,
    web: { include: web },
    skip: skippableJobs.filter(job => !run[job]),
  };
}

export const plan = files => planFor(affected(files));

// Accept another checkout's affected() result. Anything else (such as an older base
// planner that does not support --affected) plans every job.
export function parseAffected(text) {
  try {
    const value = JSON.parse(text);
    const known = Object.keys(workspace.components);
    if (typeof value?.full === "boolean" && Array.isArray(value.components) && value.components.every(name => known.includes(name)))
      return value;
  } catch {}
  return null;
}

export function outputs(result) {
  return [
    `full=${result.full}`,
    `components=${JSON.stringify(result.components)}`,
    `managed=${JSON.stringify(result.managed)}`,
    `web=${JSON.stringify(result.web)}`,
    `skip=${JSON.stringify(result.skip)}`,
  ].join("\n");
}

// CI runs `--affected` with the base branch's planner on the pull request's
// NUL-separated changed files (stdin), then `--plan <result>` with this checkout to
// enumerate suites. Without arguments every job is planned. `--files <paths>` previews
// a plan locally with this checkout alone.
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [mode, ...rest] = process.argv.slice(2);
  if (mode === "--affected") {
    console.log(JSON.stringify(affected(readFileSync(0, "utf8").split("\0").filter(Boolean))));
  } else {
    const input = mode === "--plan" ? parseAffected(rest[0] ?? "") : mode === "--files" ? affected(rest) : null;
    const result = input ? planFor(input) : plan(null);
    console.error(result.full ? `Planning every job${mode === "--plan" && !input ? " (the base planner gave no usable result)" : ""}.`
      : `Components ${result.components.join(", ") || "none"}; skipping ${result.skip.join(", ") || "nothing"}.`);
    console.log(outputs(result));
  }
}
