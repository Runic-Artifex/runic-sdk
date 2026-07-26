import {
  MvvmClient,
  ProtocolTransport,
  createMvvmProjection,
} from "./vendor/mvvm/index.js";
import { CsWebUiFrameChannel } from "./vendor/webuitoolkit-mvvm-cswebui.mjs";

const status = document.querySelector("#status");
const count = document.querySelector("#count");
const increment = document.querySelector("#increment");
const value = document.querySelector("#value");
const set = document.querySelector("#set");

const channel = new CsWebUiFrameChannel();
const transport = new ProtocolTransport(channel);
const client = new MvvmClient(transport);
const projection = createMvvmProjection(client);

function render(snapshot) {
  const current = snapshot.properties.get(1) ?? 0;
  const command = snapshot.commands.get(2);
  count.textContent = String(current);
  value.value = String(current);
  increment.disabled = !snapshot.synchronized || !command?.canExecute || command.isExecuting;
  set.disabled = !snapshot.synchronized;
  status.className = snapshot.synchronized
    ? "alert alert-success"
    : "alert alert-secondary";
  status.textContent = snapshot.synchronized
    ? `Connected · revision ${snapshot.revision}`
    : `MVVM ${snapshot.phase}`;
}

projection.subscribe((event) => {
  if (event.type === "state") render(event.snapshot);
});

increment.addEventListener("click", async () => {
  await projection.execute(2).completion;
});

set.addEventListener("click", async () => {
  const next = Number.parseInt(value.value, 10);
  if (Number.isSafeInteger(next)) await projection.setProperty(1, next);
});

try {
  render(await client.start("samples.native-mvvm-counter", crypto.randomUUID()));
} catch {
  status.className = "alert alert-danger";
  status.textContent = "The native MVVM session could not be opened.";
}

globalThis.addEventListener("pagehide", () => {
  projection.dispose();
  void transport.close("window unloaded");
}, { once: true });
