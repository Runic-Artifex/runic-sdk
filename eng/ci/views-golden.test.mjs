import assert from "node:assert/strict";
import { test } from "node:test";
import { configuration } from "../run.mjs";
import { built, compareFile, main } from "./views-golden.mjs";

// R7 G6 (D080): the engineering job restores the CI build, so it runs the golden check of the Views TypeScript clients
// there: the emitter's output for every built example equals its committed clients, and for the generator test assembly
// the TypeScript goldens. The template lanes run it on each generated project (tests/templates/Test-Templates.sh).
test("the Views TypeScript emitter writes the committed clients and the goldens byte for byte", { timeout: 180_000 }, async t => {
  if (!built(configuration)) {
    assert.ok(!process.env.CI, `CI restores the ${configuration} build, but the generator tests' contract IR is missing`);
    t.skip(`no ${configuration} build; run bun run build first`);
    return;
  }
  const lines = [];
  assert.equal(await main([], line => lines.push(line)), 0, lines.join("\n"));
});

test("a difference names its file and cause with the lines that differ", () => {
  const expected = "a\nb\nconstraints: { max: 1E+20 },\nc\n";
  const actual = "a\nb\nconstraints: { max: 100000000000000000000 },\nc\n";
  const difference = compareFile("x.ts", expected, actual);
  assert.equal(difference.cause, "number formatting (a double's exponent form, such as 1E+20)");
  assert.deepEqual(difference.text, [
    "@@ committed line 3, emitter line 3 @@",
    "            a",
    "            b",
    "- committed constraints: { max: 1E+20 },",
    "+ emitter   constraints: { max: 100000000000000000000 },",
    "            c",
    "            ",
  ]);
  assert.equal(compareFile("x.ts", expected, expected), null);
  assert.equal(compareFile("x.ts", expected, undefined).cause, "committed, but not written by the emitter");
  assert.equal(compareFile("x.ts", undefined, expected).cause, "written by the emitter, but not committed");
  assert.equal(compareFile("x.ts", expected, undefined, "golden").cause, "pinned by a golden, but not written by the emitter");
  assert.equal(compareFile("x.ts", "aß\n", "a\\u00DF\n").cause, "string escaping (\\uXXXX)");
});
