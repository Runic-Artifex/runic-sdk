import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";
import { nodeCompatibility } from "./node-compatibility.mjs";

test("npm compatibility resolves actual Node even under bun run --bun", () => {
  const compatibility = nodeCompatibility();
  for (const command of [compatibility.executable, "node"]) {
    const probe = spawnSync(command, ["-p", "JSON.stringify({bun:Boolean(process.versions.bun), node:process.versions.node})"],
      { env: compatibility.env, encoding: "utf8", timeout: 5000 });
    assert.equal(probe.status, 0, probe.stderr);
    const runtime = JSON.parse(probe.stdout);
    assert.equal(runtime.bun, false);
    assert.match(runtime.node, /^24\./);
  }
});
