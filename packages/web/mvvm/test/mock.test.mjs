import assert from "node:assert/strict";
import test from "node:test";

import {
  MvvmMockFrameChannel,
  createMvvmMockChannelFactory,
  createMvvmReplayFixture,
  startMvvmApplication,
} from "../dist/esm/index.js";

test("mock host drives the production client, revisions, validation, commands, and push path", async () => {
  const channel = new MvvmMockFrameChannel({
    contract: "tests.mock.counter",
    initial: [
      { type: "property", member: 1, value: 0 },
      { type: "validation", member: 1, errors: [] },
      { type: "command", member: 2, canExecute: true, isExecuting: false },
    ],
    setProperty(request) {
      const value = request.payload.value;
      return {
        changes: [
          { type: "property", member: 1, value },
          {
            type: "validation",
            member: 1,
            errors: typeof value === "number" && value >= 0
              ? []
              : ["Count must be zero or greater."],
          },
        ],
      };
    },
    execute(_request, context) {
      return {
        changes: [{ type: "property", member: 1, value: Number(context.revision) + 10 }],
        result: "incremented",
      };
    },
  });
  const application = await startMvvmApplication({
    contract: "tests.mock.counter",
    clientId: "00000000-0000-4000-8000-000000000103",
    channel,
  });

  assert.equal(application.projection.snapshot.properties.get(1), 0);
  await application.projection.setProperty(1, -1);
  assert.equal(application.projection.snapshot.revision, 1n);
  assert.deepEqual(
    application.projection.snapshot.validation.get(1),
    ["Count must be zero or greater."],
  );

  const command = application.projection.execute(2);
  const completed = await command.completion;
  assert.equal(completed.value, "incremented");
  assert.equal(application.projection.snapshot.revision, 2n);

  await channel.push([{ type: "property", member: 1, value: 42 }]);
  assert.equal(application.projection.snapshot.properties.get(1), 42);
  assert.equal(application.projection.snapshot.revision, 3n);
  assert.equal(channel.mode, "mock");

  await application.dispose();
});

test("semantic replay matches only sanitized operation shape", async () => {
  const channel = new MvvmMockFrameChannel(createMvvmReplayFixture({
    contract: "tests.mock.replay",
    initial: [{ type: "property", member: 1, value: "redacted" }],
    steps: [
      {
        kind: "setProperty",
        member: 1,
        mutation: {
          changes: [{ type: "property", member: 1, value: "accepted fixture value" }],
        },
      },
    ],
  }));
  const application = await startMvvmApplication({
    contract: "tests.mock.replay",
    clientId: "00000000-0000-4000-8000-000000000105",
    channel,
  });

  await application.projection.setProperty(1, "customer input is not recorded");
  assert.equal(
    application.projection.snapshot.properties.get(1),
    "accepted fixture value",
  );
  await assert.rejects(
    application.projection.setProperty(1, "second"),
    /no remaining mutation/,
  );
  await application.dispose();
});

test("mock host exposes deterministic faults without bypassing client recovery", async () => {
  const channel = new MvvmMockFrameChannel({
    contract: "tests.mock.failure",
    initial: [{ type: "command", member: 1, canExecute: true, isExecuting: false }],
    execute() {
      return {
        fault: {
          code: "request.invalid",
          message: "Fixture requested failure",
        },
      };
    },
  });
  const application = await startMvvmApplication({
    contract: "tests.mock.failure",
    clientId: "00000000-0000-4000-8000-000000000104",
    channel,
  });

  await assert.rejects(
    application.projection.execute(1).completion,
    /Fixture requested failure/,
  );
  await application.dispose();
});

test("closing a mock channel exposes the production reconnect transition", async () => {
  const first = new MvvmMockFrameChannel({
    contract: "tests.mock.reconnect",
    initial: [{ type: "property", member: 1, value: "first" }],
  });
  const application = await startMvvmApplication({
    contract: "tests.mock.reconnect",
    clientId: "00000000-0000-4000-8000-000000000106",
    channel: first,
  });
  assert.equal(application.projection.snapshot.properties.get(1), "first");

  first.close();
  const second = new MvvmMockFrameChannel({
    contract: "tests.mock.reconnect",
    initial: [{ type: "property", member: 1, value: "second" }],
  });
  await application.reconnect(second);
  assert.equal(application.projection.snapshot.properties.get(1), "second");

  await application.dispose();
});

test("mock channel factory preserves accepted state and revision across reconnect", async () => {
  const createChannel = createMvvmMockChannelFactory({
    contract: "tests.mock.persistent-reconnect",
    initial: [{ type: "property", member: 1, value: "initial" }],
  });
  const first = createChannel();
  const application = await startMvvmApplication({
    contract: "tests.mock.persistent-reconnect",
    clientId: "00000000-0000-4000-8000-000000000107",
    channel: first,
  });
  await application.projection.setProperty(1, "retained");
  assert.equal(application.projection.snapshot.revision, 1n);

  first.close();
  await application.reconnect(createChannel());
  assert.equal(application.projection.snapshot.revision, 1n);
  assert.equal(application.projection.snapshot.properties.get(1), "retained");

  await application.dispose();
});
