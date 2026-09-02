import assert from "node:assert/strict";
import { pathToFileURL } from "node:url";
import { Effect, Schema } from "effect";

const url = process.argv[2];
const modulePath = process.env.RUNIC_APPLICATION_BRIDGE_MODULE;
if (url === undefined || modulePath === undefined) throw new Error("Expected a WebSocket URL and the compiled Application Bridge module path.");
const {
  ApplicationBridgeLive,
  bridge,
  createApplicationBridgeController,
  createWebSocketFrameChannel,
  defineApplicationBridgeContract,
  materializeApplicationBridgeContract,
} = await import(pathToFileURL(modulePath).href);

const Initialize = Schema.TaggedStruct("InitializeApplication", {});
const Navigate = Schema.TaggedStruct("Navigate", { target: Schema.String });
const Receipt = Schema.TaggedStruct("NavigationAccepted", { revision: Schema.Int });
const definition = defineApplicationBridgeContract({
  protocol: { identity: "runic.test", version: 1 },
  csharp: { namespace: "Runic.Test", contractName: "Test" },
  snapshot: Schema.Struct({ revision: Schema.Int, view: Schema.String }),
  commands: [bridge.command(Initialize, { receipt: Receipt }), bridge.command(Navigate, { receipt: Receipt })],
  events: [Schema.TaggedStruct("NavigationChanged", { revision: Schema.Int, view: Schema.String })],
  errors: [],
  initialize: { _tag: "InitializeApplication" },
});
const contract = materializeApplicationBridgeContract(definition, "a".repeat(64));

const channel = createWebSocketFrameChannel(() => new WebSocket(url));
await channel.reconnect();
const controller = createApplicationBridgeController(contract, ApplicationBridgeLive(contract, channel));
try {
  const event = new Promise((resolve, reject) => controller.subscribe(resolve, reject));
  assert.deepEqual(await controller.initialize(), { revision: 0, view: "Welcome" });
  assert.deepEqual(await controller.dispatch({ _tag: "Navigate", target: "Complete" }), {
    _tag: "NavigationAccepted",
    revision: 1,
  });
  assert.deepEqual(await event, { _tag: "NavigationChanged", revision: 1, view: "Complete" });
  assert.deepEqual(await controller.reconnect(), { revision: 0, view: "Welcome" });
} finally {
  await controller.dispose();
}
console.log("hosted-web-client-ok");
