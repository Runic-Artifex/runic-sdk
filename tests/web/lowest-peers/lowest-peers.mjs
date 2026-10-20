// Runs the tests of the React, Vue and Svelte adapters and of @runic-artifex/views-effect against the
// lowest framework version their peer range accepts. Each package's tracked files are copied to a
// temporary directory, its framework dev dependencies are set to that floor, @runic-artifex/views is
// linked from the workspace build, and the package's own `test` script (build, type check, Vitest) runs
// there. Usage: bun tests/web/lowest-peers/lowest-peers.mjs [package...]; package names are directory
// names under packages/web, and names without a lowest-peer run are ignored. Without arguments the
// packages come from RUNIC_LOWEST_PEER_PACKAGES, a JSON array (CI passes the plan's web list), or are all.
import { spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const views = join(root, "packages/web/views");

// The framework whose floor each package is tested at, and the packages that move with it.
const frameworks = {
  react: "react",
  "views-effect": "react",
  vue: "vue",
  svelte: "svelte",
};
const companions = {
  // react-dom has the same version as react; the type packages follow its minor.
  react: floor => ({ "react-dom": floor, "@types/react": tilde(floor), "@types/react-dom": tilde(floor) }),
  vue: () => ({}),
  svelte: () => ({}),
};

export function lowestVersion(range) {
  const match = /^\s*(?:>=|\^|~)?\s*(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)(?:\s|$|\s*<)/.exec(range);
  if (!match) throw new Error(`Cannot read the lowest version of the peer range "${range}".`);
  return match[1];
}

function tilde(version) {
  const [major, minor] = version.split(".");
  return `~${major}.${minor}.0`;
}

// The dev dependency versions a package is tested with at its framework's floor.
export function lowestPeerDevDependencies(manifest, framework) {
  const range = manifest.peerDependencies?.[framework];
  if (!range) throw new Error(`${manifest.name} has no ${framework} peer dependency.`);
  const floor = lowestVersion(range);
  const versions = { [framework]: floor, ...companions[framework](floor) };
  const devDependencies = { ...manifest.devDependencies };
  for (const [name, version] of Object.entries(versions)) if (name in devDependencies) devDependencies[name] = version;
  devDependencies["@runic-artifex/views"] = `file:${views}`;
  return { floor, devDependencies };
}

function run(command, args, cwd) {
  const result = spawnSync(command, args, { cwd, stdio: "inherit", env: process.env });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${command} ${args.join(" ")} failed in ${cwd} with exit code ${result.status}.`);
}

function trackedFiles(directory) {
  const result = spawnSync("git", ["ls-files", "-z", "--", directory], { cwd: root, encoding: "utf8" });
  if (result.status !== 0) throw new Error(`git ls-files failed: ${result.stderr}`);
  return result.stdout.split("\0").filter(Boolean);
}

// Copies the package's tracked files and makes `extends` of its tsconfig files absolute, since they point
// into the repository (eng/typescript).
function copyPackage(name, target) {
  const source = join("packages/web", name);
  for (const file of trackedFiles(source)) {
    const destination = join(target, file.slice(source.length + 1));
    mkdirSync(dirname(destination), { recursive: true });
    cpSync(join(root, file), destination);
    if (/(^|\/)tsconfig[^/]*\.json$/.test(file)) {
      const text = readFileSync(destination, "utf8");
      writeFileSync(destination, text.replace(/("extends"\s*:\s*")(\.[^"]*)"/, (_, prefix, path) => `${prefix}${resolve(root, source, path)}"`));
    }
  }
}

function main(names) {
  const selected = (names.length ? names : Object.keys(frameworks)).filter(name => name in frameworks);
  if (selected.length === 0) {
    console.log("No package with a lowest-peer run was selected.");
    return;
  }
  if (!existsSync(join(views, "dist/index.js"))) run(process.execPath, ["run", "--bun", "build"], views);
  const bun = process.execPath;
  const temporary = mkdtempSync(join(tmpdir(), "runic-lowest-peers-"));
  try {
    for (const name of selected) {
      const started = Date.now();
      const target = join(temporary, name);
      copyPackage(name, target);
      const manifestPath = join(target, "package.json");
      const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
      const { floor, devDependencies } = lowestPeerDevDependencies(manifest, frameworks[name]);
      writeFileSync(manifestPath, `${JSON.stringify({ ...manifest, devDependencies }, null, 2)}\n`);
      console.log(`::group::${manifest.name} with ${frameworks[name]} ${floor}`);
      run(bun, ["install", "--no-save"], target);
      run(bun, ["run", "--bun", "test"], target);
      console.log("::endgroup::");
      console.log(`${manifest.name} passed with ${frameworks[name]} ${floor} in ${Math.round((Date.now() - started) / 1000)} s.`);
    }
  } finally {
    rmSync(temporary, { recursive: true, force: true });
  }
}

if (import.meta.main) {
  const names = process.argv.slice(2);
  main(names.length || !process.env.RUNIC_LOWEST_PEER_PACKAGES ? names : JSON.parse(process.env.RUNIC_LOWEST_PEER_PACKAGES));
}
