import assert from "node:assert/strict";
import { test } from "node:test";
import { createCsWebUiFrameChannel } from "../dist/esm/cs-webui.js";
const credential = "a".repeat(64);

test("CS-WebUI serializes native callbacks and publishes frames before the next call", async () => {
  const trace: string[] = [];
  let active = 0;
  const channel = createCsWebUiFrameChannel({ credential, client: {
    isConnected: () => true,
    call: async (binding, secret, bytes) => {
      assert.equal(secret, credential); assert.equal(active++, 0);
      await new Promise(resolve => setTimeout(resolve, 2));
      active--;
      trace.push("call");
      return JSON.stringify([{ kind: "receipt", value: (bytes as Uint8Array)[0] }]);
    },
  } });
  channel.subscribe(event => { if (event._tag === "Frame") trace.push("frame"); });
  await channel.reconnect();
  await Promise.all([channel.send(new Uint8Array([1])), channel.send(new Uint8Array([2]))]);
  assert.deepEqual(trace, ["call", "frame", "call", "frame"]);
  await channel.close("done");
});

test("CS-WebUI fails bounded malformed and stalled callbacks, and allows reconnection", async () => {
  let response = "null";
  const channel = createCsWebUiFrameChannel({ credential, connectionTimeoutMs: 20, client: {
    isConnected: () => true, call: async () => response === "stall" ? new Promise(() => {}) : response,
  } });
  await channel.reconnect();
  await assert.rejects(channel.send(new Uint8Array([1])), /batch/);
  assert.equal(channel.state, "disconnected");
  response = "stall";
  await channel.reconnect();
  await assert.rejects(channel.send(new Uint8Array([1])), /timed out/);
  response = "[]";
  await channel.reconnect();
  await channel.send(new Uint8Array([1]));
  await channel.close("done");
  await assert.rejects(channel.reconnect(), /closed/);
});

test("CS-WebUI polls after initialization and stops delivering after close", async () => {
  let polls = 0;
  const channel = createCsWebUiFrameChannel({ credential, client: {
    isConnected: () => true,
    call: async (binding) => {
      if (binding.endsWith("Poll")) { polls++; return '[{"kind":"event"}]'; }
      return '[{"kind":"snapshot"}]';
    },
  } });
  await channel.reconnect();
  await channel.send(new Uint8Array([1]));
  await new Promise(resolve => setTimeout(resolve, 40));
  assert.ok(polls > 0);
  await channel.close("done");
  const final = polls;
  await new Promise(resolve => setTimeout(resolve, 40));
  assert.equal(polls, final);
});
