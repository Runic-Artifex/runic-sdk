import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createServer } from "vite";

test("SvelteKit dev server exposes the official bounded Runic DevTools state", async () => {
  const bridgeIr = JSON.parse(
    await readFile(new URL("../../Contract/bridge.ir.json", import.meta.url), "utf8"),
  );
  const server = await createServer({
    mode: "mock",
    logLevel: "silent",
    server: { host: "127.0.0.1", port: 0, strictPort: false },
  });
  try {
    await server.listen();
    const address = server.httpServer?.address();
    assert.ok(address && typeof address === "object");
    const origin = `http://127.0.0.1:${address.port}`;
    const response = await fetch(`${origin}/__runic/state`);
    assert.equal(response.status, 200);
    const state = await response.json();
    assert.deepEqual(state.contract, {
      identity: "runic.artifex.setup",
      version: "1",
      fingerprint: bridgeIr.fingerprint.value,
    });
    assert.equal(state.vite.mode, "mock");
    assert.deepEqual(state.operations, []);
    assert.ok(state.timeline.length > 0);
    for (const entry of state.timeline) {
      assert.equal(entry.source, "application-bridge");
      assert.equal(entry.kind, "event");
      assert.equal(entry.label, "Application bridge contract is current");
      assert.deepEqual(entry.detail, { status: "current" });
    }

    const virtualClient = await server.transformRequest("virtual:runic/client");
    assert.match(
      virtualClient?.code ?? "",
      /@vitejs(?:\/|_)devtools(?:\/|_)client(?:\/|_)inject-passive/,
    );
    assert.match(virtualClient?.code ?? "", /vite-plugin-runic(?:\/|_)client/);
  } finally {
    await server.close();
  }
});
