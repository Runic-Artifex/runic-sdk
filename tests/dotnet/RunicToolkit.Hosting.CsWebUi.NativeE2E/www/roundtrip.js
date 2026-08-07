const output = document.querySelector("#count");
const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8", { fatal: true });
await waitForBinding("__runicToolkit_applicationBridge_send");

const responses = new Map();
globalThis.__runicToolkit_applicationBridge_receiveHostEvent = (bytes) => {
  const message = JSON.parse(decoder.decode(new Uint8Array(bytes)));
  const pending = responses.get(message.commandId);
  if (pending !== undefined) {
    responses.delete(message.commandId);
    pending(message);
  }
};

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

function send(kind, payload, sessionId, expectedRevision) {
  const commandId = crypto.randomUUID();
  const response = new Promise((resolve) => responses.set(commandId, resolve));
  const envelope = {
    protocol: "runic.artifex.native-e2e",
    version: 1,
    kind,
    commandId,
    ...(sessionId === undefined ? {} : { sessionId }),
    ...(expectedRevision === undefined ? {} : { expectedRevision }),
    payload,
  };
  void globalThis.__runicToolkit_applicationBridge_send(encoder.encode(JSON.stringify(envelope)));
  return response;
}

async function waitForBinding(name) {
  for (let attempt = 0; attempt < 400; attempt++) {
    if (typeof globalThis[name] === "function") return;
    await new Promise((resolve) => globalThis.setTimeout(resolve, 25));
  }
  throw new Error(`The CsWebUi binding '${name}' was not installed.`);
}
