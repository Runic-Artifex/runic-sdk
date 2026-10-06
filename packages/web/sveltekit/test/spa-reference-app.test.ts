import { execFile } from "node:child_process";
import { readdir, readFile, rm } from "node:fs/promises";
import { resolve } from "node:path";
import { promisify } from "node:util";
import { afterEach, describe, expect, test } from "vitest";

const execute = promisify(execFile);
const fixture = resolve("test/fixtures/spa-app");
const output = resolve(fixture, "build");
const generated = resolve(fixture, ".svelte-kit");
const vite = resolve("node_modules/vite/bin/vite.js");

afterEach(async () => {
  await Promise.all([
    rm(output, { recursive: true, force: true }),
    rm(generated, { recursive: true, force: true }),
  ]);
});

describe("SPA reference application", () => {
  test("builds the relocatable host fallback without an adapter manifest", async () => {
    await execute(process.execPath, [vite, "build"], { cwd: fixture });
    const fallback = await readFile(resolve(output, "200.html"), "utf8");
    const emitted = await readdir(output);

    expect(fallback).toContain("kit.start(app, element)");
    expect(fallback).toContain('href="./_app/immutable/');
    expect(fallback).toContain('<script src="./runic-desktop.js"></script>');
    expect(fallback).not.toMatch(/(?:href|src)="\/_app\//);
    expect(fallback).not.toContain('import("/_app/');
    expect(emitted.filter((name) => name.endsWith(".json"))).toEqual([]);
  }, 30_000);
});
