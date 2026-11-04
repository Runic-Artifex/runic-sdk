import { configureTranslatedText, resolveText, subscribeText } from "@runic-artifex/views";
import { connectProject } from "./generated/project";
import { createTranslations } from "./translations";

// The Window's language comes from the ViewModel (IRunicWindowLocale); until it arrives, the page uses the browser's.
configureTranslatedText(createTranslations(navigator.language.split("-")[0] ?? "en"));

const project = await connectProject();
const title = document.querySelector<HTMLHeadingElement>("#title")!;
const status = document.querySelector<HTMLOutputElement>("#status")!;
const theme = document.querySelector<HTMLOutputElement>("#theme")!;
const desktopScheme = document.querySelector<HTMLOutputElement>("#desktop-scheme")!;
const rename = document.querySelector<HTMLFormElement>("#rename")!;
const newTitle = document.querySelector<HTMLInputElement>("#new-title")!;
const titleError = document.querySelector<HTMLParagraphElement>("#title-error")!;
const language = document.querySelector<HTMLSelectElement>("#language")!;

const render = () => {
  const state = project.snapshot;
  title.textContent = state.title;
  // Text references, shown in the Window's language: the status line and the title's validation message.
  status.value = resolveText(state.status);
  const error = state.validation.errors.find(candidate => candidate.path[0] === "draft");
  titleError.textContent = error ? resolveText(error.text, error.message) : "";
  newTitle.setAttribute("aria-invalid", String(error !== undefined));
  document.documentElement.lang = state.language;
  language.value = state.language;
  theme.value = state.theme;
  desktopScheme.value = state.desktopScheme;
};
project.subscribe(render);
// A language switch formats the references again, without a call to .NET per string.
subscribeText(render);
render();

newTitle.addEventListener("input", () => { void project.setDraft(newTitle.value); });
rename.addEventListener("submit", async event => {
  event.preventDefault();
  if (project.snapshot.validation.hasErrors) return;
  await project.rename(newTitle.value);
  newTitle.value = "";
});
document.querySelector("#new-window")!.addEventListener("click", async () => { await project.newWindow(); });
for (const button of document.querySelectorAll<HTMLButtonElement>("[data-theme]"))
  button.addEventListener("click", async () => { await project.chooseTheme(button.dataset.theme!); });
language.addEventListener("change", async () => { await project.chooseLanguage(language.value); });
