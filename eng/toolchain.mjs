import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("..", import.meta.url));
function uniquePin(text, pattern, label) {
  const values = [...text.matchAll(pattern)].map(match => match[1]);
  assert.equal(values.length, 1, `Expected one maintained ${label} pin`);
  return values[0];
}
export function parseToolchain({ global, node, workspace, setup }) {
  const version = /^\d+\.\d+\.\d+(?:-[\w.-]+)?$/;
  const pins = {
    dotnetSdk: global.sdk?.version,
    node: node.trim(),
    bun: uniquePin(workspace.packageManager ?? "", /^bun@(\S+)$/g, "Bun packageManager"),
    npm: uniquePin(setup, /\bnpm@(\d+\.\d+\.\d+(?:-[\w.-]+)?)(?=\s|$)/g, "npm CI install"),
    pnpm: uniquePin(setup, /\bpnpm@(\d+\.\d+\.\d+(?:-[\w.-]+)?)(?=\s|$)/g, "pnpm CI install"),
  };
  for (const [name, value] of Object.entries(pins)) assert(typeof value === "string" && version.test(value), `Invalid ${name} pin`);
  assert.equal(uniquePin(setup, /bun-version:\s*["']?(\d+\.\d+\.\d+(?:-[\w.-]+)?)["']?(?=\s|$)/g, "Bun CI setup"), pins.bun, "CI Bun differs from workspace packageManager");
  return pins;
}
export function readToolchain(base = root) {
  return parseToolchain({
    global: JSON.parse(readFileSync(resolve(base, "global.json"), "utf8")),
    node: readFileSync(resolve(base, ".node-version"), "utf8"),
    workspace: JSON.parse(readFileSync(resolve(base, "package.json"), "utf8")),
    setup: readFileSync(resolve(base, ".github/actions/setup-sdk/action.yml"), "utf8"),
  });
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const pins = readToolchain();
  const key = process.argv[2];
  if (key === undefined) console.log(JSON.stringify(pins, null, 2));
  else { assert(Object.hasOwn(pins, key), `Unknown toolchain pin: ${key}`); console.log(pins[key]); }
}
