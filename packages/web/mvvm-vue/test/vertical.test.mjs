import assert from "node:assert/strict";
import test from "node:test";

import {
  MvvmClient,
  ProtocolTransport,
  createMvvmProjection,
  decodeUtf8,
  encodeUtf8,
  serializeJson,
} from "@webuitoolkit/mvvm";
import { createVueMvvmAdapter } from "../dist/esm/index.js";

const view = "abcdef00-0000-4000-8000-000000000102";
const session = "abcdef00-0000-4000-8000-000000000104";
const capability = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
const tick = () => new Promise((resolve) => setImmediate(resolve));

class VerticalChannel {
  sent = [];
  observer = undefined;
  async send(frame) { this.sent.push(frame.slice()); }
  async close() {}
  subscribe(observer) {
    this.observer = observer;
    return () => { this.observer = undefined; };
  }
  host(message) { this.observer.frame(encodeUtf8(serializeJson(message))); }
}

function requestIds() {
  let value = 100;
  return () => `00000000-0000-4000-8000-${String(++value).padStart(12, "0")}`;
}

function sent(channel) {
  return JSON.parse(decodeUtf8(channel.sent.at(-1)));
}

test("G5 amount-submit-v1 runs through the Vue adapter", async () => {
  const channel = new VerticalChannel();
  const client = new MvvmClient(new ProtocolTransport(channel), { requestIdFactory: requestIds() });
  const opening = client.start("Core.AmountSubmit", view);
  await tick();
  channel.host({
    v: 1,
    kind: "handshakeResult",
    request: sent(channel).request,
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
  });
  await tick();
  channel.host({
    v: 1,
    kind: "opened",
    contract: "Core.AmountSubmit",
    session,
    view,
    request: sent(channel).request,
    capability,
    payload: {
      snapshot: {
        revision: 0n,
        members: [
          { type: "property", member: 1, value: 0 },
          { type: "command", member: 2, canExecute: true, isExecuting: false },
        ],
      },
    },
  });
  await opening;

  const projection = createMvvmProjection(client);
  const adapter = createVueMvvmAdapter(projection);
  const amount = adapter.property(1);
  const submit = adapter.command(2);

  const property = adapter.setProperty(1, 7);
  await tick();
  const propertyRequest = sent(channel);
  channel.host({
    v: 1,
    kind: "patch",
    session,
    view,
    payload: {
      fromRevision: 0n,
      toRevision: 1n,
      changes: [{ type: "property", member: 1, value: 7 }],
    },
  });
  channel.host({
    v: 1,
    kind: "result",
    session,
    view,
    request: propertyRequest.request,
    payload: { operation: "setProperty", revision: 1n },
  });
  await property;

  const command = adapter.execute(2);
  await tick();
  channel.host({
    v: 1,
    kind: "patch",
    session,
    view,
    payload: {
      fromRevision: 1n,
      toRevision: 2n,
      changes: [{ type: "command", member: 2, canExecute: true, isExecuting: false }],
    },
  });
  channel.host({
    v: 1,
    kind: "result",
    session,
    view,
    request: command.request,
    payload: { operation: "execute", revision: 2n, value: { submissions: 1 } },
  });
  const result = await command.completion;

  assert.equal(amount.value, 7);
  assert.equal(submit.value.canExecute, true);
  assert.equal(adapter.state.value.revision, 2n);
  assert.deepEqual({ ...result.value }, { submissions: 1 });
  adapter.dispose();
  projection.dispose();
  console.log("G5-VERTICAL: vue/amount-submit-v1 amount=7 submissions=1 commits=2");
});
