#!/usr/bin/env node

import { Buffer } from "node:buffer";
import { resolve } from "node:path";

import { build } from "esbuild";

const repositoryRoot = resolve(import.meta.dirname, "..");
const result = await build({
  entryPoints: [resolve(repositoryRoot, "eng/fixtures/todo-mock.test.ts")],
  bundle: true,
  format: "esm",
  platform: "node",
  target: "node24",
  write: false,
  logLevel: "silent",
});
const output = result.outputFiles[0];
if (output === undefined) {
  throw new Error("The Todo mock verification bundle was not emitted.");
}
try {
  await import(
    `data:text/javascript;base64,${Buffer.from(output.contents).toString("base64")}`
  );
} catch (error) {
  const message = error instanceof Error ? error.message : String(error);
  throw new Error(`Todo mock verification failed: ${message}`);
}
