import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { Effect, Schema } from "effect";
import setup, { SetupSnapshot } from "../../../../protocol/application-bridge/setup/application.bridge.ts";
import { ClientEnvelopeSchema, HostEnvelopeSchema } from "../dist/esm/index.js";

const fixture = (name: string) => new URL(
  `../../../../protocol/application-bridge/setup/fixtures/${name}`,
  import.meta.url,
);

test("committed Setup fixtures decode through the Effect wire schemas", async () => {
  const client = JSON.parse(await readFile(fixture("initialize.client.json"), "utf8"));
  const host = JSON.parse(await readFile(fixture("initialized.host.json"), "utf8"));
  const envelope = await Effect.runPromise(Schema.decodeUnknown(ClientEnvelopeSchema)(client));
  const command = setup.commands[0];
  assert.ok(command);
  assert.deepEqual(
    await Effect.runPromise(Schema.decodeUnknown(command.schema)(envelope.payload)),
    { _tag: "InitializeApplication" },
  );
  const decodedHost = await Effect.runPromise(Schema.decodeUnknown(HostEnvelopeSchema)(host));
  assert.deepEqual(
    await Effect.runPromise(Schema.decodeUnknown(SetupSnapshot)(decodedHost.payload)),
    host.payload,
  );
});
