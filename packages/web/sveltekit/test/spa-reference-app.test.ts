import { execFile } from "node:child_process";
import { readdir, readFile, rm } from "node:fs/promises";
import { resolve } from "node:path";
import { promisify } from "node:util";
import { afterEach, describe, expect, test } from "vitest";

const execute = promisify(execFile);
const fixture = resolve("test/fixtures/spa-app");
const output = resolve(fixture, "build");
const prerenderedFixture = resolve("test/fixtures/prerendered-app");
const prerenderedOutput = resolve(prerenderedFixture, "build");
const browserFixture = resolve("test/fixtures/browser-spa-app");
const vite = resolve("node_modules/vite/bin/vite.js");

afterEach(async () => {
  await Promise.all([fixture, prerenderedFixture, browserFixture].flatMap((app) => [
    rm(resolve(app, "build"), { recursive: true, force: true }),
    rm(resolve(app, ".svelte-kit"), { recursive: true, force: true }),
  ]));
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

describe("Browser SPA reference application", () => {
  // Its +layout.ts applies runicSpaPageOptions with `export const { ssr, prerender } = …`. SvelteKit rejects
  // page options with the hash router, so only a SPA without `desktop` can use them.
  test("applies the SPA page options and leaves the entrypoint unchanged without desktop", async () => {
    await execute(process.execPath, [vite, "build"], { cwd: browserFixture });
    const fallback = await readFile(resolve(browserFixture, "build/200.html"), "utf8");

    expect(await readdir(resolve(browserFixture, "build"))).not.toContain("index.html");
    expect(fallback).not.toContain("runic-desktop.js");
  }, 30_000);
});

describe("Prerendered reference application", () => {
  // The strict adapter fails unless +layout.ts, `export const { prerender } = runicPrerenderedPageOptions`,
  // makes the route prerenderable.
  test("prerenders the routes that apply runicPrerenderedPageOptions", async () => {
    await execute(process.execPath, [vite, "build"], { cwd: prerenderedFixture });
    const page = await readFile(resolve(prerenderedOutput, "index.html"), "utf8");

    expect(page).toContain("<h1>Runic prerendered fixture</h1>");
    expect(page).toContain('<script src="./runic-desktop.js"></script>');
  }, 30_000);
});
