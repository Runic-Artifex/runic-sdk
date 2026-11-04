// region-collections/1 on the client (specs/application/README.md, "Region collections"): a region call made while a
// reconnect's hello is on its way waits for that hello instead of reading the features of a connection that has none.
import assert from "node:assert/strict";
import { test } from "node:test";
import type { RunicBridgeClient } from "../dist/index.js";
import { connectView } from "../dist/generated.js";
import * as bridgeWire from "../dist/wire.js";

const runtimeKey = Symbol.for("runic.views.generated-client-runtime");
const host = globalThis as unknown as Record<string | symbol, unknown>;
const tick = (ms = 0) => new Promise(resolve => setTimeout(resolve, ms));
const features = ["text-splice/1", "presentation-epoch/1", "presented/1", "region-collections/1"];

/** A host bridge that answers `open` at once and holds every hello after the first until the test releases it. */
function installBridge() {
  const connection = new Set<(connected: boolean) => void>();
  let connected = true;
  let hellos = 0;
  const held: (() => void)[] = [];
  const regions: Record<string, unknown>[] = [];
  let hello: string | undefined;
  const bridge: RunicBridgeClient = {
    isConnected: () => connected,
    onConnectionChange(report) { connection.add(report); report(connected); return () => { connection.delete(report); }; },
    onMessage() { return () => {}; },
    request(text) {
      const { session, ...message } = JSON.parse(text) as Record<string, unknown>;
      if (!connected) return Promise.reject(new Error("disconnected"));
      // request-session/1: every request names the session of its connection's latest hello.
      if (message["kind"] === "hello") hello = session as string;
      else assert.equal(session, hello, `${String(message["kind"])} does not name the session of its hello`);
      const reply = (value: object) => Promise.resolve(JSON.stringify(value));
      switch (message["kind"]) {
        case "hello": {
          const answer = JSON.stringify({ ok: true, protocol: 4, implementation: { name: "test", version: "0" }, features });
          if (++hellos === 1) return Promise.resolve(answer);
          return new Promise(resolve => { held.push(() => resolve(answer)); });
        }
        case "open": return reply({ ok: true, presentation: "ok", revision: 1, state: { documents: { regions: [{ key: "1", content: null }], selected: 0 } } });
        case "close": return reply({ ok: true, presentation: "ok" });
        case "region":
          regions.push(message);
          return reply({ ok: true, revision: 1, outcome: { kind: "Committed", rejection: null, arrival: "None" } });
        default: return reply({ ok: false, error: { kind: "rejected", reason: "invalid-request", message: "unexpected" } });
      }
    },
  };
  (host[runtimeKey] as { dispose?: () => void } | undefined)?.dispose?.();
  delete host[runtimeKey];
  host["__runicBridge"] = bridge;
  return {
    regions,
    get heldHellos() { return held.length; },
    releaseHello() { held.shift()?.(); },
    setConnected(next: boolean) {
      connected = next;
      for (const report of [...connection]) report(next);
    },
  };
}

type State = { readonly documents: unknown };
const members = { documents: (value: unknown) => bridgeWire.json(value) };

test("a region call during a reconnect waits for the new hello and is sent once it succeeded", async () => {
  const bridge = installBridge();
  const view = await connectView<State>({ contract: "Tests.Tabs:fp-1", route: "tabs", members });
  bridge.setConnected(false);
  bridge.setConnected(true);
  await tick(5);
  assert.equal(bridge.heldHellos, 1, "the reconnect sent its hello");

  const call = view.region("documents", "select", "1");
  let settled = false;
  void call.then(() => { settled = true; }, () => { settled = true; });
  await tick(5);
  assert.equal(settled, false, "the call did not wait for the hello");
  assert.equal(bridge.regions.length, 0);

  bridge.releaseHello();
  assert.deepEqual(await call, { kind: "Committed", rejection: null, arrival: "None" });
  assert.deepEqual(bridge.regions, [{ kind: "region", route: "tabs", member: "documents", action: "select", key: "1" }]);
  view.dispose();
});
