import { configureTranslatedText, resolveText, subscribeText, syncDocumentLocale, textLocale } from "@runic-artifex/views";
import { connectProject } from "./generated/project";
// The catalog's ESM, which the .NET build generates from Frontend/translations (MultiWindow.csproj).
import { m } from "../.runic/translations/messages.js";
import { configureSharedLocaleResolver, createLocaleSource, localeDirection, locales } from "../.runic/translations/runtime.js";
import { resolveTextReference } from "../.runic/translations/transport.js";

// The Window's language comes from the application (IRunicApplicationLocale); until it arrives, the page uses the
// browser's. The page's own m.*() text and the ViewModel's text references both follow it.
const locale = createLocaleSource({ initialLocale: navigator.language });
configureSharedLocaleResolver(() => textLocale() ?? locale.getRequestedLocale());
configureTranslatedText({
  ...locale,
  resolve: (reference, language) => resolveTextReference(reference, m, { locale: language }).text,
  direction: localeDirection,
});
// <html lang dir> follow the language: Arabic lays the page out right to left.
syncDocumentLocale();

const project = await connectProject();
const title = document.querySelector<HTMLHeadingElement>("#title")!;
const status = document.querySelector<HTMLOutputElement>("#status")!;
const theme = document.querySelector<HTMLOutputElement>("#theme")!;
const desktopScheme = document.querySelector<HTMLOutputElement>("#desktop-scheme")!;
const account = document.querySelector<HTMLOutputElement>("#account")!;
const rename = document.querySelector<HTMLFormElement>("#rename")!;
const newTitle = document.querySelector<HTMLInputElement>("#new-title")!;
const titleError = document.querySelector<HTMLParagraphElement>("#title-error")!;
const language = document.querySelector<HTMLSelectElement>("#language")!;
const titleLabel = document.querySelector<HTMLSpanElement>("#title-label")!;
const renameButton = document.querySelector<HTMLButtonElement>("#rename-button")!;
const languageLabel = document.querySelector<HTMLSpanElement>("#language-label")!;
// The catalog's languages (runic.json "locales"), each named in its own language.
language.replaceChildren(...locales.map(tag =>
  new Option(new Intl.DisplayNames([tag], { type: "language" }).of(tag) ?? tag, tag)));

const render = () => {
  const state = project.snapshot;
  title.textContent = state.title;
  // Text references, shown in the Window's language: the status line and the title's validation message.
  status.value = resolveText(state.status);
  const error = state.validation.errors.find(candidate => candidate.path[0] === "draft");
  titleError.textContent = error ? resolveText(error.text, error.message) : "";
  newTitle.setAttribute("aria-invalid", String(error !== undefined));
  language.value = state.language;
  // The page's own text, from the same catalog.
  titleLabel.textContent = m.page_title_label();
  renameButton.textContent = m.page_rename();
  languageLabel.textContent = m.page_language();
  theme.value = state.theme;
  desktopScheme.value = state.desktopScheme;
  account.value = state.account;
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
for (const button of document.querySelectorAll<HTMLButtonElement>("[data-document]"))
  button.addEventListener("click", async () => { await project.openDocument(button.dataset.document!); });
// The dialog blocks this Window until it closes; the command returns once it did, with the account it chose, if any.
document.querySelector("#choose-account")!.addEventListener("click", async () => { await project.chooseAccount(); });
