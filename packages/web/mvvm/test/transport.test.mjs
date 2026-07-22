import assert from "node:assert/strict";
import test from "node:test";

import {
  ProtocolTransport,
  ProtocolTransportError,
  decodeUtf8,
  encodeUtf8,
  serializeJson,
} from "../dist/esm/index.js";

const ids = Object.freeze({
  session: "00000000-0000-4000-8000-000000000004",
  view: "00000000-0000-4000-8000-000000000002",
  request: "00000000-0000-4000-8000-000000000005",
  capability: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
});

class TestChannel {
  sent = [];
  closeReasons = [];
  observer = undefined;
  unsubscribeCount = 0;

  async send(frame) {
    this.sent.push(frame.slice());
  }

  async close(reason) {
    this.closeReasons.push(reason);
  }

  subscribe(observer) {
    this.observer = observer;
    return () => {
      this.unsubscribeCount += 1;
      this.observer = undefined;
    };
  }

  receive(text) {
    this.observer?.frame(encodeUtf8(text));
  }

  disconnect(cause) {
    this.observer?.close(cause);
  }
}

function clientMutation() {
  return {
    v: 1,
    kind: "setProperty",
    session: ids.session,
    view: ids.view,
    request: ids.request,
    baseRevision: 9_223_372_036_854_775_807n,
    capability: ids.capability,
    payload: { member: 1, value: "Grüße" },
  };
}

function hostSnapshot(revision = 14n) {
  return {
    v: 1,
    kind: "snapshot",
    session: ids.session,
    view: ids.view,
    request: ids.request,
    payload: {
      revision,
      members: [{ type: "property", member: 1, value: "ready" }],
    },
  };
}

function clientMutationWithFrameBytes(byteLength) {
  const emptyLength = encodeUtf8(serializeJson({ ...clientMutation(), payload: { member: 1, value: "" } })).byteLength;
  return { ...clientMutation(), payload: { member: 1, value: "x".repeat(byteLength - emptyLength) } };
}

function hostSnapshotWithFrameBytes(byteLength) {
  const empty = hostSnapshot();
  empty.payload.members[0].value = "";
  const emptyLength = encodeUtf8(serializeJson(empty)).byteLength;
  const result = hostSnapshot();
  result.payload.members[0].value = "x".repeat(byteLength - emptyLength);
  return result;
}

test("serializes bigint revisions as bare lossless JSON integers", () => {
  const encoded = serializeJson(clientMutation());
  assert.match(encoded, /"baseRevision":9223372036854775807/);
  assert.doesNotMatch(encoded, /9223372036854776000/);
  assert.equal(serializeJson({ order: [1n, 2n], value: null }), '{"order":[1,2],"value":null}');
});

test("UTF-8 helpers round-trip Unicode and reject malformed input", () => {
  const text = "Grüße 🌍";
  assert.equal(decodeUtf8(encodeUtf8(text)), text);
  assert.throws(() => encodeUtf8("\ud800"), TypeError);
  assert.throws(() => decodeUtf8(Uint8Array.from([0xc0, 0x80])), TypeError);
  assert.throws(() => decodeUtf8(Uint8Array.from([0xef, 0xbb, 0xbf, 0x7b, 0x7d])), TypeError);
});

test("validates and sends client frames without losing revisions", async () => {
  const channel = new TestChannel();
  const transport = new ProtocolTransport(channel);

  await transport.send(clientMutation());

  assert.equal(channel.sent.length, 1);
  assert.match(decodeUtf8(channel.sent[0]), /"baseRevision":9223372036854775807/);
  assert.equal(transport.state, "connected");
});

test("rejects invalid outbound envelopes before invoking the channel", async () => {
  const channel = new TestChannel();
  const transport = new ProtocolTransport(channel);
  const invalid = { ...clientMutation(), capability: "secret" };

  await assert.rejects(transport.send(invalid), (error) => {
    assert.ok(error instanceof ProtocolTransportError);
    assert.equal(error.code, "invalid-client-message");
    assert.doesNotMatch(error.message, /secret/);
    return true;
  });
  assert.equal(channel.sent.length, 0);
  assert.equal(transport.state, "connected");
});

test("enforces negotiated frame-byte limits in both directions", async () => {
  const channel = new TestChannel();
  const transport = new ProtocolTransport(channel);
  const events = [];
  transport.subscribe((event) => events.push(event));
  transport.configureLimits({ maxFrameBytes: 1_024, maxJsonDepth: 32 });

  await transport.send(clientMutationWithFrameBytes(1_024));
  assert.equal(channel.sent[0].byteLength, 1_024);
  await assert.rejects(transport.send(clientMutationWithFrameBytes(1_025)), (error) => {
    assert.equal(error.code, "frame-too-large");
    return true;
  });
  assert.equal(channel.sent.length, 1);

  channel.receive(serializeJson(hostSnapshotWithFrameBytes(1_024)));
  assert.equal(events.filter((event) => event.type === "message").length, 1);
  channel.receive(serializeJson(hostSnapshotWithFrameBytes(1_025)));
  await Promise.resolve();
  assert.equal(transport.state, "faulted");
  assert.equal(events.filter((event) => event.type === "message").length, 1);
  assert.equal(events.findLast((event) => event.type === "protocolError")?.error.code, "frame-too-large");
});

test("enforces negotiated JSON depth for outbound and inbound documents", async () => {
  const outboundChannel = new TestChannel();
  const outbound = new ProtocolTransport(outboundChannel);
  outbound.configureLimits({ maxFrameBytes: 1_024, maxJsonDepth: 3 });
  await outbound.send({ ...clientMutation(), payload: { member: 1, value: [null] } });
  await assert.rejects(
    outbound.send({ ...clientMutation(), payload: { member: 1, value: [[null]] } }),
    (error) => error.code === "invalid-client-message",
  );
  assert.equal(outboundChannel.sent.length, 1);

  const inboundChannel = new TestChannel();
  const inbound = new ProtocolTransport(inboundChannel);
  const events = [];
  inbound.subscribe((event) => events.push(event));
  inbound.configureLimits({ maxFrameBytes: 1_024, maxJsonDepth: 4 });
  inboundChannel.receive(serializeJson(hostSnapshot()));
  assert.equal(events.filter((event) => event.type === "message").length, 1);

  const nested = hostSnapshot();
  nested.payload.members[0].value = [null];
  inboundChannel.receive(serializeJson(nested));
  assert.equal(inbound.state, "faulted");
  assert.equal(events.filter((event) => event.type === "message").length, 1);
  assert.equal(events.findLast((event) => event.type === "protocolError")?.error.code, "invalid-host-message");
});

test("validates negotiated limit configuration atomically and copies it", async () => {
  const channel = new TestChannel();
  const transport = new ProtocolTransport(channel);
  const limits = { maxFrameBytes: 2_048, maxJsonDepth: 4 };
  transport.configureLimits(limits);
  limits.maxFrameBytes = 1_024;
  limits.maxJsonDepth = 1;

  assert.throws(() => transport.configureLimits({ maxFrameBytes: 1_023, maxJsonDepth: 4 }), RangeError);
  assert.throws(() => transport.configureLimits({ maxFrameBytes: 2_048, maxJsonDepth: 33 }), RangeError);
  assert.throws(() => transport.configureLimits({ maxFrameBytes: Number.NaN, maxJsonDepth: 4 }), RangeError);
  await transport.send({ ...clientMutation(), payload: { member: 1, value: [null] } });
  assert.equal(channel.sent.length, 1);
});

test("reset and channel replacement restore hard transport ceilings", () => {
  const first = new TestChannel();
  const second = new TestChannel();
  const transport = new ProtocolTransport(first);
  const events = [];
  transport.subscribe((event) => events.push(event));
  transport.configureLimits({ maxFrameBytes: 1_024, maxJsonDepth: 1 });
  transport.resetLimits();
  first.receive(serializeJson(hostSnapshotWithFrameBytes(1_025)));
  assert.equal(events.filter((event) => event.type === "message").length, 1);

  transport.configureLimits({ maxFrameBytes: 1_024, maxJsonDepth: 1 });
  transport.replaceChannel(second);
  second.receive(serializeJson(hostSnapshotWithFrameBytes(1_025)));
  assert.equal(events.filter((event) => event.type === "message").length, 2);
});

test("dispatches only parsed host messages and retains raw frame identity separately", () => {
  const channel = new TestChannel();
  const transport = new ProtocolTransport(channel);
  const events = [];
  transport.subscribe((event) => events.push(event));
  const source = serializeJson(hostSnapshot());

  channel.receive(source);

  const messageEvent = events.find((event) => event.type === "message");
  assert.ok(messageEvent);
  assert.equal(messageEvent.message.payload.revision, 14n);
  assert.equal(decodeUtf8(messageEvent.rawFrame), source);
});

test("faults and closes the channel on an invalid inbound envelope", async () => {
  const channel = new TestChannel();
  const transport = new ProtocolTransport(channel);
  const events = [];
  transport.subscribe((event) => events.push(event));

  channel.receive('{"v":1,"kind":"hostile","payload":{"token":"secret"}}');
  await Promise.resolve();

  assert.equal(transport.state, "faulted");
  assert.deepEqual(channel.closeReasons, ["protocol error"]);
  assert.equal(events.filter((event) => event.type === "message").length, 0);
  const error = events.find((event) => event.type === "protocolError")?.error;
  assert.equal(error?.code, "invalid-host-message");
  assert.doesNotMatch(error?.message ?? "", /secret|hostile/);
});

test("disconnect and channel replacement produce deterministic reconnect state", () => {
  const first = new TestChannel();
  const second = new TestChannel();
  const transport = new ProtocolTransport(first);
  const transitions = [];
  transport.subscribe((event) => {
    if (event.type === "state") transitions.push([event.previous, event.current]);
  });

  first.disconnect();
  assert.equal(transport.state, "disconnected");
  transport.replaceChannel(second);

  assert.equal(first.unsubscribeCount, 1);
  assert.equal(transport.state, "connected");
  assert.deepEqual(transitions, [
    ["connected", "disconnected"],
    ["disconnected", "connected"],
  ]);
});

test("close is idempotent and permanently prevents reconnect and send", async () => {
  const channel = new TestChannel();
  const transport = new ProtocolTransport(channel);

  await transport.close("done");
  await transport.close("ignored");

  assert.equal(transport.state, "closed");
  assert.deepEqual(channel.closeReasons, ["done"]);
  assert.throws(() => transport.replaceChannel(new TestChannel()), ProtocolTransportError);
  await assert.rejects(transport.send(clientMutation()), ProtocolTransportError);
});
