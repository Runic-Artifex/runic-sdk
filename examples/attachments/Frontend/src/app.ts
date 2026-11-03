import { isBridgeError } from "@runic-artifex/views";
import { connectProfile } from "./generated/profile";

const profile = await connectProfile();
const avatar = document.querySelector<HTMLImageElement>("#avatar")!;
const list = document.querySelector<HTMLUListElement>("#attachments")!;
const input = document.querySelector<HTMLInputElement>("#file")!;
const progress = document.querySelector<HTMLProgressElement>("#progress")!;
const status = document.querySelector<HTMLOutputElement>("#status")!;

// A blob is a URL the host serves: an <img> and an <a download> use it as it is.
function render(): void {
  const state = profile.snapshot;
  if (avatar.src !== state.avatar.url) avatar.src = state.avatar.url;
  list.replaceChildren(...state.attachments.map(attachment => {
    const link = document.createElement("a");
    link.href = attachment.file.url;
    link.download = attachment.name;
    link.textContent = `${attachment.name} (${attachment.size} bytes)`;
    const item = document.createElement("li");
    item.append(link);
    return item;
  }));
}
profile.subscribe(render);
render();

document.querySelector("#redraw")!.addEventListener("click", () => { void profile.redraw(); });

// The file streams to the host over HTTP; the command then takes its reference.
async function attach(file: File): Promise<void> {
  progress.hidden = false;
  progress.value = 0;
  status.value = `Uploading ${file.name}`;
  try {
    const ref = await profile.upload(file, { onProgress: ({ loaded, total }) => { progress.value = total === 0 ? 1 : loaded / total; } });
    await profile.attach(ref);
    status.value = `Attached ${file.name}`;
  } catch (error) {
    status.value = isBridgeError(error) ? `Not attached: ${error.message}` : String(error);
  } finally {
    progress.hidden = true;
  }
}
input.addEventListener("change", () => {
  const file = input.files?.[0];
  if (file) void attach(file).then(() => { input.value = ""; });
});
// For browser-smoke.mjs, which has no file chooser.
(globalThis as { attachFile?: (file: File) => Promise<void> }).attachFile = attach;
