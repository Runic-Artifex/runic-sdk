import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { root, workspace, affectedComponents } from "../run.mjs";

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

// Files that only the always-run jobs read. Package READMEs and test or template
// fixtures under packages/, tools/ and tests/ are inputs to builds and packs.
// Every other workflow and every engineering test is linted or run by `engineering`.
export function engineeringOnly(file) {
  return (file.endsWith(".md") && !/^(packages|tools|tests)\//.test(file))
    || file.startsWith("tests/engineering/")
    || /^eng\/.*\.test\.mjs$/.test(file)
    || (/^\.github\/workflows\/[^/]+\.ya?ml$/.test(file) && file !== ".github/workflows/ci.yml");
}

const owner = file => Object.entries(workspace.components)
  .find(([, component]) => component.paths.some(path => file === path || file.startsWith(`${path}/`)))?.[0];

// `files` lists every path a pull request adds, changes, deletes or renames
// (both names). `null` means unknown and plans everything, as on main.
export function plan(files) {
  const components = files === null ? Object.keys(workspace.components)
    : affectedComponents(files.filter(file => !engineeringOnly(file)));
  const has = (...names) => names.some(name => components.includes(name));
  const managed = managedGroups.filter(group =>
    managedTests(root, "linux").some(test => test.group === group && has(owner(test.path))));
  const web = webTests().filter(test => has(owner(`packages/web/${test.package}`)));
  // Package candidates, their consumers and templates cover every shipped component.
  const packages = components.length > 0;
  const run = {
    managed: managed.length > 0,
    web: web.length > 0,
    "framework-consumers": has("svelte"),
    views: has("examples"),
    packages,
    "package-consumers": packages,
    templates: packages,
    // Desktop contracts, native windows, platform services, NativeAOT Views apps and Windows administration.
    native: has("desktop", "platform", "application", "examples", "administration-windows"),
  };
  return {
    full: files === null,
    components,
    managed,
    web: { include: web },
    skip: skippableJobs.filter(job => !run[job]),
  };
}

// A pull_request checkout is GitHub's merge of the head into the base branch.
// Its first parent is the base, so this diff is exactly what the pull request changes.
export function changedFiles(event, git = args => execFileSync("git", args, { cwd: root, encoding: "utf8" })) {
  if (event !== "pull_request") return null;
  try {
    git(["rev-parse", "--verify", "--quiet", "HEAD^2"]);
    return git(["diff", "--name-only", "--no-renames", "-z", "HEAD^1", "HEAD"]).split("\0").filter(Boolean);
  } catch {
    return null;
  }
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

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const files = process.argv[2] === "--files" ? process.argv.slice(3) : changedFiles(process.env.GITHUB_EVENT_NAME);
  const result = plan(files);
  console.error(files === null ? "Planning every job (not a pull request merge, or the diff is unavailable)."
    : `Planning ${files.length} changed files: components ${result.components.join(", ") || "none"}; skipping ${result.skip.join(", ") || "nothing"}.`);
  console.log(outputs(result));
}
