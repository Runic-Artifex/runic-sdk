#!/usr/bin/env node

import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

const root = resolve(import.meta.dirname, "..");
const matrix = json("eng/frontend-support-matrix.json");
if (matrix.schema !== "runic-toolkit.frontend-support-matrix/3") {
  fail("Unsupported frontend support matrix schema.");
}
if (matrix.protocolOwner !== "@runic-artifex/application-bridge") {
  fail("Effect Application Bridge must be the only frontend protocol owner.");
}
for (const renderer of ["react", "vue", "svelte", "angular"]) {
  if (matrix.renderers[renderer]?.ownsProtocolState !== false) {
    fail(`${renderer} must not own protocol, reconnect, revision, or cancellation state.`);
  }
}
for (const gate of Object.values(matrix.sharedGates)) requirePath(gate);

const package_ = json("web/packages/application-bridge/package.json");
if (package_.name !== matrix.protocolOwner || package_.license !== "MIT") {
  fail("The Application Bridge npm package has inconsistent identity metadata.");
}
if (package_.dependencies?.effect === undefined) {
  fail("The Application Bridge runtime must have an explicit Effect dependency.");
}
const runtime = text("web/packages/application-bridge/src/runtime.ts");
contains(runtime, "ManagedRuntime", "single owned Effect runtime");
contains(runtime, "Stream.fromPubSub", "Effect host event stream");
contains(runtime, "CsWebUiApplicationBridgeLive", "production Layer");
contains(text("web/packages/application-bridge/src/mock.ts"), "MockApplicationBridge", "mock Layer");
contains(text("web/packages/application-bridge/src/mock.ts"), "TestApplicationBridge", "fault-injection Layer");

console.log("Framework-neutral Application Bridge frontend policy passed.");

function json(path) { return JSON.parse(text(path)); }
function text(path) {
  const absolute = resolve(root, path);
  if (!existsSync(absolute)) fail(`Missing required frontend artifact '${path}'.`);
  return readFileSync(absolute, "utf8");
}
function requirePath(path) {
  if (!existsSync(resolve(root, path))) fail(`Missing required frontend path '${path}'.`);
}
function contains(source, expected, label) {
  if (!source.includes(expected)) fail(`Missing ${label}: '${expected}'.`);
}
function fail(message) { throw new Error(message); }
