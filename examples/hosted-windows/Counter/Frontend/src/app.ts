import { configureTranslatedText, resolveText, subscribeText, syncDocumentLocale, textLocale } from "@runic-artifex/views";
import { connectCounter } from "./generated/counter";
// The catalog's ESM, which the .NET build generates from Frontend/translations (CounterTool.csproj).
import { m } from "../.runic/translations/messages.js";
import { configureSharedLocaleResolver, createLocaleSource, localeDirection } from "../.runic/translations/runtime.js";
import { resolveTextReference } from "../.runic/translations/transport.js";

// The Window's language is the application's, which follows the shell's when a shell hosts the counter. Until it
// arrives, the page uses the browser's.
const locale = createLocaleSource({ initialLocale: navigator.language });
configureSharedLocaleResolver(() => textLocale() ?? locale.getRequestedLocale());
configureTranslatedText({
  ...locale,
  resolve: (reference, language) => resolveTextReference(reference, m, { locale: language }).text,
  direction: localeDirection,
});
syncDocumentLocale();

const counter = await connectCounter();
const element = <T extends HTMLElement>(id: string) => document.querySelector<T>(`#${id}`)!;
const count = element<HTMLOutputElement>("count");
const status = element<HTMLOutputElement>("status");
const increment = element<HTMLButtonElement>("increment");
const reset = element<HTMLButtonElement>("reset");
const documentation = element<HTMLButtonElement>("documentation");

const render = () => {
  const state = counter.snapshot;
  count.value = String(state.count);
  status.value = state.status ? resolveText(state.status) : "";
  document.title = element("title").textContent = m.page_title();
  element("count-label").textContent = m.page_count();
  increment.textContent = m.page_increment();
  reset.textContent = m.page_reset();
  documentation.textContent = m.page_documentation();
};
counter.subscribe(render);
// A language switch shows the page's text and the ViewModel's text references in the new language.
subscribeText(render);
render();

increment.addEventListener("click", async () => { await counter.increment(); });
reset.addEventListener("click", async () => { await counter.reset(); });
documentation.addEventListener("click", async () => { await counter.openDocumentation(); });
