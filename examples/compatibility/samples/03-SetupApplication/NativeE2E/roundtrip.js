const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8", { fatal: true });
const pending = new Map();
let sessionId;
let revision = 0;
await waitForBinding("__runicToolkit_applicationBridge_send");

globalThis.__runicToolkit_applicationBridge_receiveHostEvent = (bytes) => {
  const message = JSON.parse(decoder.decode(new Uint8Array(bytes)));
  sessionId = message.sessionId;
  revision = message.revision;
  if (message.kind === "event") {
    if (message.payload._tag === "OperationProgress") {
      document.body.dataset.progress = String(message.payload.completed);
    }
    if (message.payload._tag === "OperationCompleted") {
      document.body.dataset.view = "Complete";
      document.body.dataset.result = document.body.dataset.progress === "5" ? "pass" : "fail";
    }
    if (message.payload._tag === "OperationFailed" || message.payload._tag === "OperationCancelled") {
      document.body.dataset.result = "fail";
    }
    return;
  }
  const resolve = pending.get(message.commandId);
  if (resolve !== undefined) {
    pending.delete(message.commandId);
    resolve(message);
  }
};

try {
  const initialized = await send("initialize", { _tag: "InitializeApplication" });
  document.body.dataset.view = initialized.payload.viewId;
  await send("dispatch", { _tag: "Navigate", target: "Destination", expectedRevision: revision });
  const selected = await send("dispatch", { _tag: "SelectDestination" });
  await send("dispatch", { _tag: "Navigate", target: "Features", expectedRevision: revision });
  await send("dispatch", {
    _tag: "StartInstallation",
    destinationSelectionId: selected.payload.destination.selectionId,
    selectedFeatures: ["core", "examples"],
  });
} catch (error) {
  document.body.dataset.result = "error";
  document.body.dataset.message = error instanceof Error ? error.message : "unknown";
}

function send(kind, payload) {
  const commandId = crypto.randomUUID();
  const response = new Promise((resolve) => pending.set(commandId, resolve));
  const envelope = {
    protocol: "runic.artifex.setup",
    version: 1,
    kind,
    commandId,
    ...(sessionId === undefined ? {} : { sessionId }),
    ...(kind === "initialize" ? {} : { expectedRevision: revision }),
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
