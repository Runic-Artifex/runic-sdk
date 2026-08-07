const output = document.querySelector("#count");
const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8", { fatal: true });
await waitForBinding("__runicToolkit_applicationBridge_send");

const hostEvents = [];
const receiveHostEvent = (bytes) => {
  const message = JSON.parse(decoder.decode(new Uint8Array(bytes)));
  if (message.kind === "event") hostEvents.push(message);
};
globalThis.__runicToolkit_applicationBridge_receiveHostEvent = receiveHostEvent;

try {
  const initialized = await send("initialize", { _tag: "InitializeApplication" });
  output.textContent = String(initialized.payload.count);
  const incremented = await send("dispatch", { _tag: "Increment" }, initialized.sessionId, initialized.revision);
  output.textContent = String(incremented.payload.count);
  document.body.dataset.result = output.textContent === "1" ? "pass" : "fail";
} catch (error) {
  document.body.dataset.result = "error";
  document.body.dataset.message = error instanceof Error ? error.message : "unknown";
}

async function send(kind, payload, sessionId, expectedRevision) {
  const commandId = crypto.randomUUID();
  const envelope = {
    protocol: "runic.artifex.native-e2e",
    version: 1,
    kind,
    commandId,
    ...(sessionId === undefined ? {} : { sessionId }),
    ...(expectedRevision === undefined ? {} : { expectedRevision }),
    payload,
  };
  const pending = globalThis.__runicToolkit_applicationBridge_send(
    encoder.encode(JSON.stringify(envelope)),
  );
  globalThis.__runicToolkit_applicationBridge_receiveHostEvent = receiveHostEvent;
  const response = await pending;
  globalThis.__runicToolkit_applicationBridge_receiveHostEvent = receiveHostEvent;
  const decoded = JSON.parse(String(response));
  const frames = Array.isArray(decoded) ? decoded : [decoded];
  for (const frame of frames) {
    if (frame.kind === "event") hostEvents.push(frame);
  }
  const correlated = frames.find((frame) => frame.commandId === commandId);
  if (correlated === undefined) throw new Error("The host response omitted its correlated frame.");
  return correlated;
}

async function waitForBinding(name) {
  for (let attempt = 0; attempt < 400; attempt++) {
    if (typeof globalThis[name] === "function") return;
    await new Promise((resolve) => globalThis.setTimeout(resolve, 25));
  }
  throw new Error(`The CsWebUi binding '${name}' was not installed.`);
}
