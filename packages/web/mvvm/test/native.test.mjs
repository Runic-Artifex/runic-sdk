import assert from "node:assert/strict";
import test from "node:test";

import {
  decodeUtf8,
  encodeUtf8,
  serializeJson,
  startNativeMvvmApplication,
} from "../dist/esm/index.js";

const contractName = "Example.NativeOwner";
const session = "abcdef00-0000-4000-8000-000000000010";
const capability = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

class ExampleContract {
  static contractName = contractName;

  constructor(projection) {
    this.projection = projection;
  }
}

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
}

class TestPageLifetime {
  listener = undefined;

  addEventListener(type, listener) {
    assert.equal(type, "pagehide");
    this.listener = listener;
  }

  removeEventListener(type, listener) {
    assert.equal(type, "pagehide");
    if (this.listener === listener) this.listener = undefined;
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

async function completeOpen(channel, revision, value) {
  await tick();
  channel.host(handshakeResult(channel.sent.at(-1).request));
  await tick();
  const open = channel.sent.at(-1);
  channel.host({
    v: 1,
    kind: "opened",
    contract: contractName,
    session,
    view: open.view,
    request: open.request,
    capability,
    payload: {
      snapshot: {
        revision,
        members: [{ type: "property", member: 1, value }],
      },
    },
  });
}

test("native owner creates a typed contract, reconnects, and follows page lifetime", async () => {
  const channels = [];
  let readinessChecks = 0;
  const bridge = {
    CsWebUiFrameChannel: class extends TestChannel {
      constructor() {
        super();
        channels.push(this);
      }
    },
    async waitForCsWebUiBinding() {
      readinessChecks += 1;
    },
  };
  const pageLifetime = new TestPageLifetime();
  const opening = startNativeMvvmApplication({
    contract: ExampleContract,
    loadBridge: async () => bridge,
    pageLifetime,
    clientId: "abcdef00-0000-4000-8000-000000000011",
  });
  while (channels.length === 0) await tick();
  await completeOpen(channels[0], 0n, "before");
  const application = await opening;

  assert.ok(application.contract instanceof ExampleContract);
  assert.equal(application.contract.projection, application.projection);
  assert.equal(application.projection.property(1), "before");
  assert.equal(readinessChecks, 1);

  const reconnecting = application.reconnect();
  await tick();
  const second = channels[1];
  second.host(handshakeResult(second.sent.at(-1).request));
  await tick();
  const snapshot = second.sent.at(-1);
  second.host({
    v: 1,
    kind: "snapshot",
    session,
    view: snapshot.view,
    request: snapshot.request,
    payload: {
      revision: 4n,
      members: [{ type: "property", member: 1, value: "after" }],
    },
  });
  await reconnecting;
  assert.equal(readinessChecks, 2);
  assert.equal(application.projection.property(1), "after");

  pageLifetime.listener();
  await tick();
  assert.equal(pageLifetime.listener, undefined);
  await assert.rejects(application.reconnect(), /disposed/);
});

test("native owner can use a development channel factory without loading CsWebUi", async () => {
  const channels = [];
  let bridgeLoads = 0;
  const opening = startNativeMvvmApplication({
    contract: ExampleContract,
    channelFactory: () => {
      const channel = new TestChannel();
      channels.push(channel);
      return channel;
    },
    pageLifetime: null,
    loadBridge: undefined,
    clientId: "abcdef00-0000-4000-8000-000000000012",
  });
  while (channels.length === 0) await tick();
  await completeOpen(channels[0], 0n, "mock");
  const application = await opening;

  assert.equal(application.projection.property(1), "mock");
  assert.equal(bridgeLoads, 0);
  await application.dispose();
});

test("native owner rejects ambiguous bridge and channel configuration", async () => {
  await assert.rejects(
    startNativeMvvmApplication({
      contract: ExampleContract,
      channelFactory: () => new TestChannel(),
      bridgeUrl: "/bridge.mjs",
      pageLifetime: null,
    }),
    /cannot be combined/,
  );
});
