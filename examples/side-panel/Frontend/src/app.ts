import { connectMain, type MainState } from "./generated/main";

// One bundle for two pages of the same Window. The Window's page shows the order form; the debug panel's page, which
// MainViewModel opens beside the Window with OpenSidePanelAsync({ Path: "#/debug" }), shows the debug tool. Both are
// connections of the Window and bind to the same MainViewModel. The panel's address is its own: Runic does not sync it
// with the Window's navigation path, so a route such as this hash is all the panel needs.
const isPanel = location.hash === "#/debug";
const main = await connectMain();
const app = document.querySelector<HTMLDivElement>("#app")!;

function element<K extends keyof HTMLElementTagNameMap>(tag: K, properties: Partial<HTMLElementTagNameMap[K]> = {},
  ...children: (Node | string)[]): HTMLElementTagNameMap[K] {
  const created = Object.assign(document.createElement(tag), properties);
  created.append(...children);
  return created;
}

function button(text: string, action: () => Promise<unknown>, properties: Partial<HTMLButtonElement> = {}): HTMLButtonElement {
  const created = element("button", { type: "button", ...properties }, text);
  created.addEventListener("click", () => { void action(); });
  return created;
}

// The debug tool, the same in the panel's page and in the overlay. A complementary landmark with a name, so a screen
// reader can jump to it and announces it when the focus moves in from the page.
function debugTool(state: MainState, kind: "page" | "overlay"): HTMLElement {
  const side = state.isDebugPanelOnLeft ? "left" : "right";
  return element("aside", { className: `debug ${kind} ${side}`, ariaLabel: "Debug tools" },
    element("h2", {}, "Debug tools"),
    ...(kind === "overlay"
      ? [element("p", { className: "notice", role: "status" },
          `The window cannot grow (${state.refusal}), so the panel covers the page.`)]
      : state.refusal ? [element("p", { className: "notice", role: "status" }, `Not moved: ${state.refusal}`)] : []),
    element("dl", {},
      element("dt", {}, "Quantity"), element("dd", { id: "debug-quantity" }, String(state.quantity)),
      element("dt", {}, "Shown"), element("dd", {}, kind === "page" ? `beside the window, ${side}` : `over the page, ${side}`)),
    element("p", {},
      button(state.isDebugPanelOnLeft ? "Dock right" : "Dock left", () => main.moveDebug(), { id: "debug-move" }), " ",
      button("Close", () => main.closeDebug(), { id: "debug-close" })),
    element("h3", {}, "Log"),
    element("ol", { id: "debug-log" }, ...state.log.map(entry => element("li", {}, entry))));
}

function renderPage(state: MainState): void {
  document.title = "Side panel example";
  const toggle = button(state.isDebugPanelOpen || state.showDebugOverlay ? "Hide debug panel" : "Show debug panel",
    () => main.toggleDebug(), { id: "toggle-debug" });
  toggle.setAttribute("aria-pressed", String(state.isDebugPanelOpen || state.showDebugOverlay));
  app.replaceChildren(
    element("main", {},
      element("h1", {}, "Order"),
      element("p", {}, "The window is 860 × 640. The debug panel opens beside it, so this form keeps its size and place."),
      element("p", { className: "quantity" },
        button("−", () => main.remove(), { id: "remove", ariaLabel: "Remove one" }),
        element("output", { id: "quantity", className: "count", ariaLabel: "Quantity" }, String(state.quantity)),
        button("+", () => main.add(), { id: "add", ariaLabel: "Add one" })),
      element("p", {}, toggle)),
    ...(state.showDebugOverlay ? [debugTool(state, "overlay")] : []));
}

function renderPanel(state: MainState): void {
  // The panel's own title; it does not rename the window.
  document.title = "Debug tools";
  app.replaceChildren(debugTool(state, "page"));
}

const render = () => (isPanel ? renderPanel : renderPage)(main.snapshot);
main.subscribe(render);
render();
