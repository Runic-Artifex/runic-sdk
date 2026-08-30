import assert from "node:assert/strict";
import { pathToFileURL } from "node:url";
import { Effect, Schema } from "effect";

const url = process.argv[2];
const modulePath = process.env.RUNIC_APPLICATION_BRIDGE_MODULE;
if (url === undefined || modulePath === undefined) throw new Error("Expected a WebSocket URL and the compiled Application Bridge module path.");
const {
  ApplicationBridgeLive,
  createApplicationBridgeController,
  createWebSocketFrameChannel,
  defineApplicationContract,
} = await import(pathToFileURL(modulePath).href);

const contract = defineApplicationContract({
  identity: "runic.test",
  version: 1,
  fingerprint: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  command: Schema.Union(
    Schema.TaggedStruct("InitializeApplication", {}),
    Schema.TaggedStruct("Navigate", { target: Schema.String }),
  ),
  receipt: Schema.TaggedStruct("NavigationAccepted", { revision: Schema.Int }),
  event: Schema.TaggedStruct("NavigationChanged", { revision: Schema.Int, view: Schema.String }),
  snapshot: Schema.Struct({ revision: Schema.Int, view: Schema.String }),
  initialize: { _tag: "InitializeApplication" },
});

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
