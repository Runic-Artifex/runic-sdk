import assert from "node:assert/strict";
import { test } from "node:test";
import { mkdirSync, mkdtempSync, rmSync, symlinkSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { isWithinDirectory } from "./path-boundary.mjs";

test("package boundary resolves temporary aliases and rejects external links and prefix siblings", () => {
  const temporary = mkdtempSync(join(tmpdir(), "runic-path-boundary-"));
  try {
    const consumer = join(temporary, "consumer");
    const alias = join(temporary, "alias");
    const sibling = join(temporary, "consumer-external");
    mkdirSync(join(consumer, "node_modules"), { recursive: true });
    mkdirSync(sibling);
    symlinkSync(consumer, alias, "junction");
    symlinkSync(sibling, join(consumer, "node_modules", "external"), "junction");
    assert.ok(isWithinDirectory(alias, join(consumer, "node_modules")));
    assert.ok(isWithinDirectory(consumer, join(alias, "node_modules")));
    assert.equal(isWithinDirectory(consumer, sibling), false);
    assert.equal(isWithinDirectory(consumer, join(alias, "node_modules", "external")), false);
  } finally {
    rmSync(temporary, { recursive: true, force: true });
  }
});
