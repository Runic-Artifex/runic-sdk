import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
const semver = String.raw`\d+\.\d+\.\d+(?:-[\w.-]+)?`;
function uniquePin(text, pattern, label) {
  const values = [...text.matchAll(pattern)].map(match => match[1]);
  assert.equal(values.length, 1, `Expected one maintained ${label} pin`);
  return values[0];
}
// package.json, global.json, .node-version and the setup-sdk action own the pins.
export function parseToolchain({ global, node, workspace, setup }) {
  const version = new RegExp(`^${semver}$`);
  const pins = {
    dotnetSdk: global.sdk?.version,
    node: node.trim(),
    bun: uniquePin(workspace.packageManager ?? "", /^bun@(\S+)$/g, "Bun packageManager"),
    npm: uniquePin(setup, new RegExp(String.raw`\bnpm@(${semver})(?=\s|$)`, "g"), "npm CI install"),
    pnpm: uniquePin(setup, new RegExp(String.raw`\bpnpm@(${semver})(?=\s|$)`, "g"), "pnpm CI install"),
  };
  for (const [name, value] of Object.entries(pins)) assert(typeof value === "string" && version.test(value), `Invalid ${name} pin`);
  assert.equal(uniquePin(setup, new RegExp(String.raw`bun-version:\s*["']?(${semver})["']?(?=\s|$)`, "g"), "Bun CI setup"), pins.bun, "CI Bun differs from workspace packageManager");
  return pins;
}
// Every other copy must match the pins: the workspace Bun engine, the template
// package-manager defaults and the Nix shell's Bun, npm and pnpm archives.
// The flake derives .NET and Node from global.json and .node-version itself.
export function checkToolchainCopies(pins, { workspace, templates, flake }) {
  assert.equal(workspace.engines?.bun, pins.bun, "package.json engines.bun differs from packageManager");
  for (const [property, name] of [["BunTemplateVersion", "bun"], ["NpmTemplateVersion", "npm"], ["PnpmTemplateVersion", "pnpm"]]) {
    const pattern = new RegExp(String.raw`<${property}\b[^>]*>(${semver})</${property}>`, "g");
    assert.equal(uniquePin(templates, pattern, `template ${property}`), pins[name], `Template ${property} differs from the ${name} pin`);
  }
  const flakePins = {
    bun: [/releases\/download\/bun-v([^/]+)\/bun-/g, /pname = "bun";\s*version = "([^"]+)";/g],
    npm: [/registry\.npmjs\.org\/npm\/-\/npm-([^"]+)\.tgz/g, /runCommand "npm-([^"]+)"/g],
    pnpm: [/pname = "pnpm";\s*version = "([^"]+)";/g, /\/-\/exe\.\$\{pnpmArchive\.platform\}-([^"]+)\.tgz/g],
  };
  for (const [name, patterns] of Object.entries(flakePins))
    for (const pattern of patterns)
      assert.equal(uniquePin(flake, pattern, `flake.nix ${name}`), pins[name], `flake.nix ${name} differs from the ${name} pin (${pattern.source})`);
}
export function readToolchain(base = root) {
  const read = path => readFileSync(resolve(base, path), "utf8");
  const workspace = JSON.parse(read("package.json"));
  const pins = parseToolchain({
    global: JSON.parse(read("global.json")),
    node: read(".node-version"),
    workspace,
    setup: read(".github/actions/setup-sdk/action.yml"),
  });
  checkToolchainCopies(pins, {
    workspace,
    templates: read("tools/Runic.Application.Templates/Runic.Application.Templates.csproj"),
    flake: read("flake.nix"),
  });
  return pins;
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const pins = readToolchain();
  const key = process.argv[2];
  if (key === undefined) console.log(JSON.stringify(pins, null, 2));
  else { assert(Object.hasOwn(pins, key), `Unknown toolchain pin: ${key}`); console.log(pins[key]); }
}
