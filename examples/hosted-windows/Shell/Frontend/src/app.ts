import { connectShell } from "./generated/shell";

const shell = await connectShell();
const state = document.querySelector<HTMLOutputElement>("#counter-state")!;
const radios = (name: string) => [...document.querySelectorAll<HTMLInputElement>(`input[name="${name}"]`)];

const render = () => {
  const snapshot = shell.snapshot;
  state.value = snapshot.counterState;
  for (const radio of radios("language")) radio.checked = radio.value === snapshot.language;
  for (const radio of radios("theme")) radio.checked = radio.value === snapshot.theme;
};
shell.subscribe(render);
render();

document.querySelector("#open-counter")!.addEventListener("click", async () => { await shell.openCounter(); });
document.querySelector("#stop-counter")!.addEventListener("click", async () => { await shell.stopCounter(); });
for (const radio of radios("language")) radio.addEventListener("change", async () => { await shell.chooseLanguage(radio.value); });
for (const radio of radios("theme")) radio.addEventListener("change", async () => { await shell.chooseTheme(radio.value); });
