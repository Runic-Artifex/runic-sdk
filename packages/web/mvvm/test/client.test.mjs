import assert from "node:assert/strict";
import test from "node:test";

import {
  MvvmClient,
  ClientProtocolError,
  MvvmDisconnectedError,
  MvvmFaultError,
  ProtocolTransport,
  decodeUtf8,
  encodeUtf8,
  serializeJson,
} from "../dist/esm/index.js";

const view = "abcdef00-0000-4000-8000-000000000002";
const session = "abcdef00-0000-4000-8000-000000000004";
const capability = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

class TestChannel {
  sent = [];
  observer = undefined;
  async send(frame) { this.sent.push(frame.slice()); }
  async close() {}
  subscribe(observer) { this.observer = observer; return () => { this.observer = undefined; }; }
  host(message) { this.observer.frame(encodeUtf8(serializeJson(message))); }
  disconnect() { this.observer.close(); }
}

function requestIds() {
  let value = 0;
  return () => `00000000-0000-4000-8000-${String(++value).padStart(12, "0")}`;
}

function sent(channel, offset = 1) {
  return JSON.parse(decodeUtf8(channel.sent.at(-offset)));
}

const tick = () => new Promise((resolve) => setImmediate(resolve));

function handshakeResult(request, overrides = {}) {
  return {
    v: 1,
    kind: "handshakeResult",
    request,
    payload: {
      selectedVersion: 1,
      capabilities: overrides.capabilities ?? ["cancellation", "collections", "commandResults", "patches", "validation"],
      limits: {
        maxFrameBytes: 1_048_576,
        maxJsonDepth: 32,
        maxSessions: 16,
        maxPendingRequests: 64,
        maxSnapshotMembers: 4_096,
        maxPatchChanges: 1_024,
        maxCollectionItems: 10_000,
        commandTimeoutMilliseconds: 30_000,
        ...overrides.limits,
      },
    },
  };
}

function opened(request) {
  return {
    v: 1,
    kind: "opened",
    contract: "Example.Counter",
    session,
    view,
    request,
    capability,
    payload: {
      snapshot: {
        revision: 0n,
        members: [
          { type: "property", member: 1, value: "ready" },
          { type: "collection", member: 2, items: [1, 2, 3] },
          { type: "command", member: 3, canExecute: true, isExecuting: false },
          { type: "validation", member: 1, errors: [] },
        ],
      },
    },
  };
}

async function startClient() {
  const channel = new TestChannel();
  const transport = new ProtocolTransport(channel);
  const client = new MvvmClient(transport, { requestIdFactory: requestIds() });
  const completion = client.start("Example.Counter", view);
  await tick();
  const handshake = sent(channel);
  channel.host(handshakeResult(handshake.request));
  await tick();
  const open = sent(channel);
  channel.host(opened(open.request));
  return { channel, transport, client, snapshot: await completion };
}

test("opens with an authoritative snapshot of every projected state kind", async () => {
  const { snapshot } = await startClient();
  assert.equal(snapshot.phase, "connected");
  assert.equal(snapshot.synchronized, true);
  assert.equal(snapshot.revision, 0n);
  assert.equal(snapshot.properties.get(1), "ready");
  assert.deepEqual(snapshot.collections.get(2), [1, 2, 3]);
  assert.deepEqual(snapshot.commands.get(3), { canExecute: true, isExecuting: false });
  assert.deepEqual(snapshot.validation.get(1), []);
});

test("applies a consecutive patch atomically and ignores a byte-identical duplicate", async () => {
  const { channel, client } = await startClient();
  const patch = {
    v: 1,
    kind: "patch",
    session,
    view,
    payload: {
      fromRevision: 0n,
      toRevision: 1n,
      changes: [
        { type: "property", member: 1, value: "saved" },
        { type: "collection", member: 2, operation: "insert", index: 3, items: [4] },
        { type: "collectionMove", member: 2, from: 0, to: 2, count: 1 },
        { type: "command", member: 3, canExecute: false, isExecuting: true },
        { type: "validation", member: 1, errors: ["Required"] },
      ],
    },
  };
  channel.host(patch);
  channel.host(patch);
  await tick();

  assert.equal(client.state.revision, 1n);
  assert.equal(client.state.properties.get(1), "saved");
  assert.deepEqual(client.state.collections.get(2), [2, 3, 1, 4]);
  assert.deepEqual(client.state.commands.get(3), { canExecute: false, isExecuting: true });
  assert.deepEqual(client.state.validation.get(1), ["Required"]);
  assert.equal(client.state.phase, "connected");
});

test("a revision gap requests a snapshot and replaces all local state", async () => {
  const { channel, client } = await startClient();
  channel.host({
    v: 1,
    kind: "patch",
    session,
    view,
    payload: {
      fromRevision: 2n,
      toRevision: 3n,
      changes: [{ type: "property", member: 1, value: "speculative" }],
    },
  });
  await tick();

  assert.equal(client.state.phase, "recovering");
  assert.equal(client.state.properties.get(1), "ready");
  const request = sent(channel);
  assert.equal(request.kind, "requestSnapshot");
  channel.host({
    v: 1,
    kind: "snapshot",
    session,
    view,
    request: request.request,
    payload: {
      revision: 14n,
      members: [{ type: "property", member: 9, value: "authoritative" }],
    },
  });
  await tick();

  assert.equal(client.state.phase, "connected");
  assert.equal(client.state.revision, 14n);
  assert.equal(client.state.properties.has(1), false);
  assert.equal(client.state.properties.get(9), "authoritative");
  assert.equal(client.state.collections.size, 0);
  assert.equal(client.state.commands.size, 0);
  assert.equal(client.state.validation.size, 0);
});

test("command completion follows its patch and preserves result presence", async () => {
  const { channel, client } = await startClient();
  const invocation = client.execute(3, { argument: null });
  await tick();
  const request = sent(channel);
  assert.equal(request.kind, "execute");
  assert.equal(request.baseRevision, 0);
  assert.ok(Object.hasOwn(request.payload, "argument"));

  channel.host({
    v: 1,
    kind: "patch",
    session,
    view,
    payload: {
      fromRevision: 0n,
      toRevision: 1n,
      changes: [{ type: "command", member: 3, canExecute: true, isExecuting: false }],
    },
  });
  channel.host({
    v: 1,
    kind: "result",
    session,
    view,
    request: invocation.request,
    payload: { operation: "execute", revision: 1n, value: { saved: true } },
  });

  const result = await invocation.completion;
  assert.equal(result.request, invocation.request);
  assert.equal(result.revision, 1n);
  assert.equal(result.valuePresent, true);
  assert.deepEqual({ ...result.value }, { saved: true });
});

test("cancellation produces one correlated target fault and an accepted cancel result", async () => {
  const { channel, client } = await startClient();
  const invocation = client.execute(3);
  await tick();
  const cancellation = invocation.cancel();
  await tick();
  const cancelRequest = sent(channel);
  assert.equal(cancelRequest.kind, "cancel");
  assert.equal(cancelRequest.payload.targetRequest, invocation.request);

  channel.host({
    v: 1,
    kind: "fault",
    session,
    view,
    request: invocation.request,
    payload: { code: "request.cancelled", message: "The request was cancelled.", retryable: false },
  });
  channel.host({
    v: 1,
    kind: "result",
    session,
    view,
    request: cancelRequest.request,
    payload: {
      operation: "cancel",
      revision: 0n,
      targetRequest: invocation.request,
      accepted: true,
    },
  });

  await assert.rejects(invocation.completion, (error) => error instanceof MvvmFaultError && error.code === "request.cancelled");
  assert.deepEqual(await cancellation, {
    request: cancelRequest.request,
    revision: 0n,
    targetRequest: invocation.request,
    accepted: true,
  });
});

test("disconnect rejects uncertain work and reconnects by handshake plus authoritative snapshot", async () => {
  const { channel: first, transport, client } = await startClient();
  const invocation = client.execute(3);
  await tick();
  first.disconnect();
  await assert.rejects(invocation.completion, MvvmDisconnectedError);
  assert.equal(client.state.phase, "disconnected");

  const second = new TestChannel();
  transport.replaceChannel(second);
  const recovery = client.reconnect();
  await tick();
  const handshake = sent(second);
  second.host(handshakeResult(handshake.request));
  await tick();
  const snapshotRequest = sent(second);
  assert.equal(snapshotRequest.kind, "requestSnapshot");
  second.host({
    v: 1,
    kind: "snapshot",
    session,
    view,
    request: snapshotRequest.request,
    payload: { revision: 14n, members: [{ type: "validation", member: 1, errors: ["Recovered"] }] },
  });

  const snapshot = await recovery;
  assert.equal(snapshot.phase, "connected");
  assert.equal(snapshot.revision, 14n);
  assert.deepEqual(snapshot.validation.get(1), ["Recovered"]);
  assert.equal(snapshot.properties.size, 0);
});

test("correlates a reentrant open response before send completes", async () => {
  class ReentrantChannel extends TestChannel {
    async send(frame) {
      this.sent.push(frame.slice());
      const message = JSON.parse(decodeUtf8(frame));
      if (message.kind === "handshake") this.host(handshakeResult(message.request));
      if (message.kind === "open") this.host(opened(message.request));
    }
  }
  const channel = new ReentrantChannel();
  const client = new MvvmClient(new ProtocolTransport(channel), { requestIdFactory: requestIds() });
  const snapshot = await client.start("Example.Counter", view);
  assert.equal(snapshot.phase, "connected");
  assert.deepEqual(channel.sent.map((frame) => JSON.parse(decodeUtf8(frame)).kind), ["handshake", "open"]);
});

test("enforces negotiated snapshot, patch, and collection-growth limits atomically", async () => {
  const channel = new TestChannel();
  const client = new MvvmClient(new ProtocolTransport(channel), { requestIdFactory: requestIds() });
  const opening = client.start("Example.Counter", view);
  await tick();
  channel.host(handshakeResult(sent(channel).request, {
    limits: { maxSnapshotMembers: 4, maxPatchChanges: 1, maxCollectionItems: 3 },
  }));
  await tick();
  channel.host(opened(sent(channel).request));
  await opening;

  channel.host({
    v: 1, kind: "patch", session, view,
    payload: {
      fromRevision: 0n, toRevision: 1n,
      changes: [{ type: "collection", member: 2, operation: "insert", index: 3, items: [4] }],
    },
  });
  await tick();
  assert.equal(client.state.phase, "failed");
  assert.equal(client.state.revision, 0n);
  assert.deepEqual(client.state.collections.get(2), [1, 2, 3]);

  const excessive = new TestChannel();
  const second = new MvvmClient(new ProtocolTransport(excessive), { requestIdFactory: requestIds() });
  const rejected = second.start("Example.Counter", view);
  await tick();
  excessive.host(handshakeResult(sent(excessive).request, { limits: { maxSnapshotMembers: 1 } }));
  await tick();
  const openRequest = sent(excessive);
  excessive.host({ ...opened(openRequest.request), payload: { snapshot: { revision: 0n, members: [
    { type: "property", member: 1, value: 1 }, { type: "property", member: 2, value: 2 },
  ] } } });
  await assert.rejects(rejected, ClientProtocolError);
  assert.equal(second.state.properties.size, 0);
});

test("rejects a regressing ordinary or reconnect snapshot and a reconnect snapshot fault", async () => {
  const { channel, transport, client } = await startClient();
  channel.host({ v: 1, kind: "patch", session, view, payload: {
    fromRevision: 0n, toRevision: 1n, changes: [{ type: "property", member: 1, value: "new" }],
  } });
  await tick();
  const snapshotCompletion = client.requestSnapshot();
  await tick();
  const snapshotRequest = sent(channel);
  channel.host({ v: 1, kind: "snapshot", session, view, request: snapshotRequest.request,
    payload: { revision: 0n, members: [{ type: "property", member: 1, value: "old" }] } });
  await assert.rejects(snapshotCompletion, ClientProtocolError);
  assert.equal(client.state.revision, 1n);
  assert.equal(client.state.properties.get(1), "new");

  const { channel: first, transport: reconnectTransport, client: reconnectClient } = await startClient();
  first.disconnect();
  const second = new TestChannel();
  reconnectTransport.replaceChannel(second);
  const reconnect = reconnectClient.reconnect();
  await tick();
  second.host(handshakeResult(sent(second).request));
  await tick();
  const request = sent(second);
  second.host({ v: 1, kind: "fault", session, view, request: request.request,
    payload: { code: "request.invalid", message: "snapshot rejected", retryable: false } });
  await assert.rejects(reconnect, (error) => error instanceof MvvmFaultError && error.code === "request.invalid");
  assert.equal(reconnectClient.state.phase, "disconnected");
});

test("queues cancellation behind its execute and rechecks queued mutations after disconnect", async () => {
  const { channel, client } = await startClient();
  const first = client.execute(3);
  const second = client.execute(3);
  const cancellation = second.cancel();
  await tick();
  assert.deepEqual(channel.sent.slice(2).map((frame) => JSON.parse(decodeUtf8(frame)).kind), ["execute"]);
  channel.host({ v: 1, kind: "patch", session, view, payload: { fromRevision: 0n, toRevision: 1n,
    changes: [{ type: "command", member: 3, canExecute: true, isExecuting: false }] } });
  channel.host({ v: 1, kind: "result", session, view, request: first.request,
    payload: { operation: "execute", revision: 1n } });
  await first.completion;
  await tick();
  assert.deepEqual(channel.sent.slice(2).map((frame) => JSON.parse(decodeUtf8(frame)).kind), ["execute", "execute", "cancel"]);
  void second.completion.catch(() => undefined);
  void cancellation.catch(() => undefined);
  channel.disconnect();
  await tick();

  const { channel: disconnectedChannel, client: disconnectedClient } = await startClient();
  const pendingOne = disconnectedClient.setProperty(1, "one");
  const pendingTwo = disconnectedClient.setProperty(1, "two");
  await tick();
  disconnectedChannel.disconnect();
  await Promise.all([
    assert.rejects(pendingOne, MvvmDisconnectedError),
    assert.rejects(pendingTwo, ClientProtocolError),
  ]);
  assert.equal(disconnectedChannel.sent.filter((frame) => JSON.parse(decodeUtf8(frame)).kind === "setProperty").length, 1);
});

test("requires mutation base plus one and correlates cancel targets", async () => {
  const { channel, client } = await startClient();
  const mutation = client.setProperty(1, "bad");
  await tick();
  const request = sent(channel);
  channel.host({ v: 1, kind: "result", session, view, request: request.request,
    payload: { operation: "setProperty", revision: 0n } });
  await assert.rejects(mutation, ClientProtocolError);
  assert.equal(client.state.revision, 0n);

  const started = await startClient();
  const invocation = started.client.execute(3);
  await tick();
  const cancel = invocation.cancel();
  await tick();
  const cancelRequest = sent(started.channel);
  started.channel.host({ v: 1, kind: "result", session, view, request: cancelRequest.request, payload: {
    operation: "cancel", revision: 0n,
    targetRequest: "00000000-0000-4000-8000-999999999999", accepted: true,
  } });
  await assert.rejects(cancel, ClientProtocolError);
  await assert.rejects(invocation.completion, ClientProtocolError);
});

test("blocks public snapshots and patches until reconnect recovery completes", async () => {
  const { channel, transport, client } = await startClient();
  channel.disconnect();
  assert.throws(() => client.requestSnapshot(), ClientProtocolError);
  const rebound = new TestChannel();
  transport.replaceChannel(rebound);
  assert.throws(() => client.requestSnapshot(), ClientProtocolError);
  rebound.host({ v: 1, kind: "patch", session, view, payload: {
    fromRevision: 0n, toRevision: 1n, changes: [{ type: "property", member: 1, value: "speculative" }],
  } });
  await tick();
  assert.equal(client.state.revision, 0n);
  assert.equal(client.state.properties.get(1), "ready");
  assert.equal(rebound.sent.length, 0);
});

test("enforces negotiated capabilities and ignores a late duplicate terminal", async () => {
  const noPatches = new TestChannel();
  const client = new MvvmClient(new ProtocolTransport(noPatches), { requestIdFactory: requestIds() });
  const opening = client.start("Example.Counter", view);
  await tick();
  noPatches.host(handshakeResult(sent(noPatches).request, {
    capabilities: ["cancellation", "collections", "commandResults", "validation"],
  }));
  await tick();
  noPatches.host(opened(sent(noPatches).request));
  await opening;
  noPatches.host({ v: 1, kind: "patch", session, view, payload: { fromRevision: 0n, toRevision: 1n, changes: [] } });
  await tick();
  assert.equal(client.state.phase, "failed");

  const started = await startClient();
  const invocation = started.client.execute(3);
  await tick();
  started.channel.host({ v: 1, kind: "fault", session, view, request: invocation.request,
    payload: { code: "request.cancelled", message: "lost race", retryable: false } });
  await assert.rejects(invocation.completion, (error) => error instanceof MvvmFaultError && error.code === "request.cancelled");
  started.channel.host({ v: 1, kind: "result", session, view, request: invocation.request,
    payload: { operation: "execute", revision: 1n } });
  await tick();
  assert.equal(started.client.state.phase, "connected");
});

test("enforces negotiated pending, patch-change, and snapshot capability bounds", async () => {
  const pendingChannel = new TestChannel();
  const pendingClient = new MvvmClient(new ProtocolTransport(pendingChannel), { requestIdFactory: requestIds() });
  const opening = pendingClient.start("Example.Counter", view);
  await tick();
  pendingChannel.host(handshakeResult(sent(pendingChannel).request, { limits: { maxPendingRequests: 1 } }));
  await tick();
  pendingChannel.host(opened(sent(pendingChannel).request));
  await opening;
  const ack = pendingClient.acknowledge();
  await tick();
  const count = pendingChannel.sent.length;
  assert.throws(() => pendingClient.acknowledge(), ClientProtocolError);
  assert.equal(pendingChannel.sent.length, count);
  void ack.catch(() => undefined);
  pendingChannel.disconnect();

  const patchChannel = new TestChannel();
  const patchClient = new MvvmClient(new ProtocolTransport(patchChannel), { requestIdFactory: requestIds() });
  const patchOpening = patchClient.start("Example.Counter", view);
  await tick();
  patchChannel.host(handshakeResult(sent(patchChannel).request, { limits: { maxPatchChanges: 1 } }));
  await tick();
  patchChannel.host(opened(sent(patchChannel).request));
  await patchOpening;
  patchChannel.host({ v: 1, kind: "patch", session, view, payload: { fromRevision: 0n, toRevision: 1n, changes: [
    { type: "property", member: 1, value: "first" }, { type: "property", member: 2, value: "second" },
  ] } });
  await tick();
  assert.equal(patchClient.state.phase, "failed");
  assert.equal(patchClient.state.properties.get(1), "ready");

  for (const [missing, member] of [
    ["collections", { type: "collection", member: 2, items: [] }],
    ["validation", { type: "validation", member: 1, errors: [] }],
  ]) {
    const channel = new TestChannel();
    const client = new MvvmClient(new ProtocolTransport(channel), { requestIdFactory: requestIds() });
    const result = client.start("Example.Counter", view);
    await tick();
    const capabilities = ["cancellation", "collections", "commandResults", "patches", "validation"].filter((x) => x !== missing);
    channel.host(handshakeResult(sent(channel).request, { capabilities }));
    await tick();
    const request = sent(channel);
    channel.host({ ...opened(request.request), payload: { snapshot: { revision: 0n, members: [member] } } });
    await assert.rejects(result, ClientProtocolError);
  }
});

test("rejects command values without commandResults before advancing revision", async () => {
  const channel = new TestChannel();
  const client = new MvvmClient(new ProtocolTransport(channel), { requestIdFactory: requestIds() });
  const opening = client.start("Example.Counter", view);
  await tick();
  channel.host(handshakeResult(sent(channel).request, {
    capabilities: ["cancellation", "collections", "patches", "validation"],
  }));
  await tick();
  channel.host(opened(sent(channel).request));
  await opening;
  const invocation = client.execute(3);
  await tick();
  channel.host({ v: 1, kind: "result", session, view, request: invocation.request,
    payload: { operation: "execute", revision: 1n, value: "forbidden" } });
  await assert.rejects(invocation.completion, ClientProtocolError);
  assert.equal(client.state.revision, 0n);
});

test("returned snapshots deeply clone property JSON", async () => {
  const { channel, client } = await startClient();
  channel.host({ v: 1, kind: "patch", session, view, payload: { fromRevision: 0n, toRevision: 1n,
    changes: [{ type: "property", member: 1, value: { nested: [1, 2] } }] } });
  await tick();
  const first = client.state;
  first.properties.get(1).nested.push(3);
  assert.deepEqual({ ...client.state.properties.get(1), nested: [...client.state.properties.get(1).nested] }, { nested: [1, 2] });
});
