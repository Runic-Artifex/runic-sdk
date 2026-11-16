// The "Runic" pane of the Elements panel: what Runic Inspect's `describe($0)` says about the selected element (its
// component, the ViewModel members it binds, the commands it calls with their C# handlers, its recent calls and texts).
import { createEvaluator, devtoolsEvaluate } from "./page.js";
import { describeSections, statusMessage } from "./model.js";
import { applyTheme, h, locationLink } from "./render.js";

const devtools = globalThis.chrome?.devtools;
const main = document.querySelector("main");
const status = document.querySelector(".status");
let evaluator;
let generation = 0;

applyTheme();

async function openLocation(location) {
  const result = await evaluator.call("open", [JSON.stringify(location)]);
  return result.status === "ok" && result.value === true;
}

function showStatus(text) {
  status.textContent = text;
  status.hidden = text === "";
}

function render(description) {
  main.replaceChildren(...describeSections(description).flatMap((section) => [
    h("h2", {}, section.title),
    h("ul", {}, section.items.map((item) => h("li", { className: item.note ? "note" : "" },
      item.location ? locationLink(item.location, item.text, openLocation) : item.note ? item.text : h("code", {}, item.text),
      item.frontend ? [" ", locationLink(item.frontend, "(frontend)", openLocation)] : "",
      item.detail ? h("span", { className: "detail" }, item.detail) : ""))),
  ]));
}

async function describeSelection() {
  const current = ++generation;
  showStatus("Reading…");
  // `$0` is the Elements panel's selection in `inspectedWindow.eval`.
  const result = await evaluator.call("describe", ["$0"]);
  if (current !== generation) return;
  if (result.status === "ok") {
    showStatus("");
    render(result.value);
  } else {
    main.replaceChildren();
    showStatus(result.status === "error" ? `No description: ${result.error}` : statusMessage(result.status));
  }
}

if (devtools?.panels?.elements === undefined) {
  showStatus("Open the Elements panel's \"Runic\" pane in the browser's DevTools.");
} else {
  evaluator = createEvaluator(devtoolsEvaluate(devtools));
  devtools.panels.elements.onSelectionChanged.addListener(() => void describeSelection());
  document.querySelector("#refresh").addEventListener("click", () => void describeSelection());
  void describeSelection();
}
