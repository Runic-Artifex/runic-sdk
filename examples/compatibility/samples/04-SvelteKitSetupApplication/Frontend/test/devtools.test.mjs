import assert from "node:assert/strict";
import { test } from "node:test";
import { createServer } from "vite";

test("SvelteKit dev server exposes the official bounded Runic DevTools state", async () => {
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
      fingerprint: "",
    });
    assert.equal(state.vite.mode, "mock");
    assert.deepEqual(state.operations, []);
    assert.deepEqual(state.timeline, []);

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
