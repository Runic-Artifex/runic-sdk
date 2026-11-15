import { expect, test } from "bun:test";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const root = resolve(import.meta.dir, "../..");

// Committed generated clients: <view>.ts, .mock.ts, types.ts, <view>.contract.d.ts and its declaration map (#952).
const files = execFileSync("git", ["ls-files", "-z", "--", "*/generated/*.ts", "*.contract.d.ts.map"], { cwd: root, encoding: "utf8" })
  .split("\0").filter(Boolean);

// CI compares the checkout with what the build generates byte for byte, on Windows too, so every generated file must be
// checked out with the generator's LF line endings whatever core.autocrlf is.
test("committed generated clients are checked out with LF line endings", () => {
  expect(files.some(file => file.endsWith(".contract.d.ts.map"))).toBe(true);
  const attributes = execFileSync("git", ["check-attr", "eol", "--", ...files], { cwd: root, encoding: "utf8" })
    .trim().split("\n").filter(line => !line.endsWith(": eol: lf"));
  expect(attributes).toEqual([]);
  expect(files.filter(file => readFileSync(resolve(root, file), "utf8").includes("\r"))).toEqual([]);
});

// A map's sources are relative to the map with forward slashes, so a committed map is the same on every OS and checkout.
test("committed declaration maps name their sources portably", () => {
  for (const file of files.filter(file => file.endsWith(".map"))) {
    const map = JSON.parse(readFileSync(resolve(root, file), "utf8"));
    expect(map.file).toBe(file.split("/").at(-1).slice(0, -".map".length));
    for (const source of map.sources) expect({ file, source, portable: !/\\|^\/|^[A-Za-z]:/.test(source) }).toEqual({ file, source, portable: true });
  }
});
