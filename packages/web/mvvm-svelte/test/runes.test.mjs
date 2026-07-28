import assert from "node:assert/strict";
import test from "node:test";

import { readable } from "svelte/store";

import { toSvelteMvvmRune } from "../dist/esm/runes.js";

test("Svelte 5 rune helper exposes the readable's current value", () => {
  const source = readable({ status: "idle" });
  const rune = toSvelteMvvmRune(source);
  assert.deepEqual(rune.current, { status: "idle" });
});
