import {
  applicationBridgeReceiver,
  createDesktopFrameChannel,
} from "../dist/esm/index.js";

const channel = createDesktopFrameChannel();
channel.subscribe((event) => {
  if (event._tag !== "Frame") return;
  document.body.dataset.result = Array.from(event.bytes).join(",");
  document.title = "PASS";
});

try {
  await channel.reconnect();
  await channel.send(Uint8Array.of(1, 0, 2));
} catch (error) {
  document.body.dataset.result = error instanceof Error ? `${error.name}:${error.message}` : String(error);
  document.title = "FAIL";
}

globalThis.addEventListener("beforeunload", () => {
  void channel.close(`${applicationBridgeReceiver} page unloaded`);
});
