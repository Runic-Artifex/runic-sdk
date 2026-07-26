import assert from "node:assert/strict";
import test from "node:test";

import {
  decodeUtf8,
  encodeUtf8,
  serializeJson,
  startMvvmApplication,
} from "../dist/esm/index.js";

const contract = "Example.Reconnect";
const session = "abcdef00-0000-4000-8000-000000000004";
const capability = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

class TestChannel {
  observer = undefined;
  sent = [];

  async send(frame) {
    this.sent.push(JSON.parse(decodeUtf8(frame)));
  }

  async close(reason) {
    this.observer?.close(reason);
  }

  subscribe(observer) {
    this.observer = observer;
    return () => {
      if (this.observer === observer) this.observer = undefined;
    };
  }

  host(message) {
    this.observer?.frame(encodeUtf8(serializeJson(message)));
  }

  disconnect() {
    this.observer?.close("test disconnect");
  }
}

const tick = () => new Promise((resolve) => setImmediate(resolve));

function handshakeResult(request) {
  return {
    v: 1,
    kind: "handshakeResult",
    request,
    payload: {
      selectedVersion: 1,
      capabilities: ["cancellation", "collections", "commandResults", "patches", "validation"],
      limits: {
        maxFrameBytes: 1_048_576,
        maxJsonDepth: 32,
        maxSessions: 16,
        maxPendingRequests: 64,
        maxSnapshotMembers: 4_096,
        maxPatchChanges: 1_024,
        maxCollectionItems: 10_000,
        commandTimeoutMilliseconds: 30_000,
      },
    },
  };
}

test("application rebinds a disconnected channel and recovers an authoritative snapshot", async () => {
  const first = new TestChannel();
  const opening = startMvvmApplication({ contract, channel: first, clientId: "abcdef00-0000-4000-8000-000000000002" });
  await tick();
  first.host(handshakeResult(first.sent.at(-1).request));
  await tick();
  const open = first.sent.at(-1);
  first.host({
    v: 1,
    kind: "opened",
    contract,
    session,
    view: open.view,
    request: open.request,
    capability,
    payload: {
      snapshot: {
        revision: 0n,
        members: [{ type: "property", member: 1, value: "before" }],
      },
    },
  });
  const application = await opening;
  assert.equal(application.projection.property(1), "before");

  first.disconnect();
  assert.equal(application.projection.snapshot.phase, "disconnected");

  const second = new TestChannel();
  const reconnecting = application.reconnect(second);
  await tick();
  assert.equal(second.sent.at(-1).kind, "handshake");
  second.host(handshakeResult(second.sent.at(-1).request));
  await tick();
  const snapshot = second.sent.at(-1);
  assert.equal(snapshot.kind, "requestSnapshot");
  second.host({
    v: 1,
    kind: "snapshot",
    session,
    view: snapshot.view,
    request: snapshot.request,
    payload: {
      revision: 7n,
      members: [{ type: "property", member: 1, value: "recovered" }],
    },
  });
  await reconnecting;

  assert.equal(application.projection.snapshot.synchronized, true);
  assert.equal(application.projection.snapshot.revision, 7n);
  assert.equal(application.projection.property(1), "recovered");
  await application.dispose();
});

test("application rejects channel replacement before disconnect and after disposal", async () => {
  const first = new TestChannel();
  const opening = startMvvmApplication({ contract, channel: first, clientId: "abcdef00-0000-4000-8000-000000000003" });
  await tick();
  first.host(handshakeResult(first.sent.at(-1).request));
  await tick();
  const open = first.sent.at(-1);
  first.host({
    v: 1,
    kind: "opened",
    contract,
    session,
    view: open.view,
    request: open.request,
    capability,
    payload: { snapshot: { revision: 0n, members: [] } },
  });
  const application = await opening;

  await assert.rejects(
    application.reconnect(new TestChannel()),
    /must disconnect/);
  first.disconnect();
  await application.dispose();
  await assert.rejects(
    application.reconnect(new TestChannel()),
    /disposed/);
});
