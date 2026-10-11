import { connectProject } from "./generated/project";

const project = await connectProject();
const title = document.querySelector<HTMLHeadingElement>("#title")!;
const theme = document.querySelector<HTMLOutputElement>("#theme")!;
const desktopScheme = document.querySelector<HTMLOutputElement>("#desktop-scheme")!;
const rename = document.querySelector<HTMLFormElement>("#rename")!;
const newTitle = document.querySelector<HTMLInputElement>("#new-title")!;

const render = () => {
  const state = project.snapshot;
  title.textContent = state.title;
  theme.value = state.theme;
  desktopScheme.value = state.desktopScheme;
};
project.subscribe(render);
render();

rename.addEventListener("submit", async event => {
  event.preventDefault();
  await project.rename(newTitle.value);
  newTitle.value = "";
});
document.querySelector("#new-window")!.addEventListener("click", async () => { await project.newWindow(); });
for (const button of document.querySelectorAll<HTMLButtonElement>("[data-theme]"))
  button.addEventListener("click", async () => { await project.chooseTheme(button.dataset.theme!); });
