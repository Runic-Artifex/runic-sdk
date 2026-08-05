import {
  MvvmClient,
  ProtocolTransport,
  createMvvmProjection,
} from "./mvvm/index.js";
import { CsWebUiFrameChannel } from "./runic-toolkit-mvvm-cswebui.mjs";

const output = document.querySelector("#count");
await waitForBinding("__runicToolkit_mvvm_send");
const channel = new CsWebUiFrameChannel();
const transport = new ProtocolTransport(channel);
const client = new MvvmClient(transport);
const projection = createMvvmProjection(client);

transport.subscribe((event) => {
  if (event.type !== "protocolError") return;
  document.body.dataset.transportError = event.error.message;
  document.body.dataset.transportCause =
    event.error.cause instanceof Error
      ? event.error.cause.message
      : String(event.error.cause ?? "");
});

projection.subscribe((event) => {
  if (event.type !== "state") return;
  const count = event.snapshot.properties.get(1);
  if (typeof count === "number") output.textContent = String(count);
});

try {
  await client.start("tests.native-cswebui-roundtrip", crypto.randomUUID());
  await projection.execute(2).completion;
  document.body.dataset.result = output.textContent === "1" ? "pass" : "fail";
} catch (error) {
  document.body.dataset.result = "error";
  document.body.dataset.message = error instanceof Error ? error.name : "unknown";
}

async function waitForBinding(name) {
  for (let attempt = 0; attempt < 400; attempt++) {
    if (typeof globalThis[name] === "function") return;
    await new Promise((resolve) => globalThis.setTimeout(resolve, 25));
  }
  throw new Error(`The CsWebUi binding '${name}' was not installed.`);
}
