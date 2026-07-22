import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const packageRoot = new URL("../", import.meta.url);
const manifest = JSON.parse(await readFile(new URL("package.json", packageRoot), "utf8"));

test("package metadata exposes ESM, browser, and declaration entry points", () => {
  assert.equal(manifest.name, "@webuitoolkit/mvvm");
  assert.equal(manifest.type, "module");
  assert.equal(manifest.sideEffects, false);
  assert.equal(manifest.main, "./dist/esm/index.js");
  assert.equal(manifest.module, "./dist/esm/index.js");
  assert.equal(manifest.browser, "./dist/esm/index.js");
  assert.equal(manifest.types, "./dist/esm/index.d.ts");
  assert.deepEqual(manifest.exports["."], {
    types: "./dist/esm/index.d.ts",
    import: "./dist/esm/index.js",
    default: "./dist/esm/index.js",
  });
  assert.deepEqual(manifest.files, ["dist", "README.md"]);
});

test("ESM publishes the complete runtime surface", async () => {
  const esm = await import(new URL("dist/esm/index.js", packageRoot));

  for (const name of [
    "CAPABILITIES",
    "FAULT_CODES",
    "MvvmClient",
    "PROTOCOL_IDENTITY",
    "PROTOCOL_LIMITS",
    "PROTOCOL_VERSION",
    "ProtocolTransport",
    "ProtocolTransportError",
    "ProtocolValidationError",
    "assertClientMessage",
    "assertHostMessage",
    "decodeUtf8",
    "encodeUtf8",
    "parseClientMessage",
    "parseHostMessage",
    "serializeJson",
    "stringifyClientMessage",
    "stringifyHostMessage",
    "validateClientMessage",
    "validateHostMessage",
  ]) {
    assert.ok(Object.hasOwn(esm, name), `missing public export ${name}`);
  }
});

test("published declarations describe the parent SDK surface", async () => {
  const declarations = await readFile(new URL("dist/esm/index.d.ts", packageRoot), "utf8");
  for (const moduleName of ["./protocol.js", "./validation.js", "./transport.js", "./client.js"]) {
    assert.match(declarations, new RegExp(`export \\* from ["']${moduleName.replace(".", "\\.")}["']`));
  }
});
