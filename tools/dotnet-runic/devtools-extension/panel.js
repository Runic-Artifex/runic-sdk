// The "Runic" panel: the inspected page's Views with their states and C# ViewModels, and its recent Bridge calls with
// timing, outcome and C# handler, from Runic Inspect's `snapshot()`. It refreshes every second while visible.
import { createEvaluator, devtoolsEvaluate } from "./page.js";
import { callRows, statusMessage, viewRows } from "./model.js";
import { applyTheme, h, locationLink } from "./render.js";

const refreshInterval = 1000;
const devtools = globalThis.chrome?.devtools;
const main = document.querySelector("main");
const status = document.querySelector(".status");
const pause = document.querySelector("#pause");
const open = new Set();
let evaluator;
let timer;
let busy = false;
let lastJson = "";

applyTheme();

async function openLocation(location) {
  const result = await evaluator.call("open", [JSON.stringify(location)]);
  return result.status === "ok" && result.value === true;
}

function showStatus(text) {
  status.textContent = text;
  status.hidden = text === "";
}

function render(snapshot) {
  const views = viewRows(snapshot);
  const calls = callRows(snapshot);
  const viewList = views.length === 0 ? h("p", { className: "muted" }, "No open View yet.")
    : views.map((view) => {
      const details = h("details", { dataset: { key: view.key }, ontoggle: () => {
        if (details.open) open.add(view.key); else open.delete(view.key);
      } },
      h("summary", {},
        h("code", {}, view.route), " ",
        view.viewModel ? locationLink(view.location, view.viewModel, openLocation) : "",
        view.revision ? h("span", { className: "muted" }, ` · rev ${view.revision}`) : "",
        view.presentations !== undefined && view.presentations !== 1 ? h("span", { className: "muted" }, ` · ${view.presentations} presentations`) : ""),
      view.members.length === 0 ? h("p", { className: "muted" }, "No state.")
        : h("dl", {}, view.members.flatMap((member) => [h("dt", {}, member.name), h("dd", {}, member.value)])));
      if (open.has(view.key)) details.open = true;
      return details;
    });
  const callTable = calls.length === 0 ? h("p", { className: "muted" }, "No Bridge call since the page loaded.")
    : h("table", {},
      h("thead", {}, h("tr", {}, ["#", "Call", "Kind", "Outcome", "Time", "C# handler", "Rev"].map((title) => h("th", {}, title)))),
      h("tbody", {}, calls.map((call) => h("tr", { dataset: { call: call.key } },
        h("td", { className: "muted" }, call.id),
        h("td", {}, h("code", {}, call.label), h("div", { className: "muted" }, `route ${call.route}`)),
        h("td", {}, call.kind),
        h("td", { className: `outcome-${call.state}` }, call.outcome),
        h("td", { className: "mono" }, call.duration),
        h("td", {},
          call.handler ? h("div", { className: "mono" }, call.handler) : "",
          call.symbol || call.location ? locationLink(call.location, call.symbol || "C# member", openLocation) : "",
          !call.handler && !call.symbol && !call.location ? h("span", { className: "muted" }, "—") : ""),
        h("td", { className: "mono" }, call.revision)))));
  const notes = (snapshot.notes ?? []).map((note) => h("p", { className: "note" }, note));
  main.replaceChildren(h("h2", {}, `Views (${views.length})`), ...[viewList].flat(), h("h2", {}, `Recent Bridge calls (${calls.length})`),
    callTable, ...notes);
}

async function refresh() {
  if (busy || evaluator === undefined) return;
  busy = true;
  try {
    const result = await evaluator.call("snapshot");
    if (result.status === "ok") {
      showStatus("");
      const json = JSON.stringify(result.value);
      if (json !== lastJson) {
        lastJson = json;
        render(result.value);
      }
    } else {
      lastJson = "";
      main.replaceChildren();
      showStatus(result.status === "error" ? `The page did not answer: ${result.error}` : statusMessage(result.status));
    }
  } finally {
    busy = false;
  }
}

function schedule() {
  clearInterval(timer);
  timer = undefined;
  if (!pause.checked && document.visibilityState === "visible") timer = setInterval(refresh, refreshInterval);
}

if (devtools?.inspectedWindow === undefined) {
  showStatus("Open this panel from the browser's DevTools: the \"Runic\" tab next to Elements and Console.");
} else {
  evaluator = createEvaluator(devtoolsEvaluate(devtools));
  document.querySelector("#refresh").addEventListener("click", () => { lastJson = ""; void refresh(); });
  pause.addEventListener("change", schedule);
  document.addEventListener("visibilitychange", () => { schedule(); if (document.visibilityState === "visible") void refresh(); });
  // A navigation replaces the page and its calls.
  devtools.network?.onNavigated?.addListener(() => { lastJson = ""; open.clear(); main.replaceChildren(); void refresh(); });
  void refresh();
  schedule();
}
