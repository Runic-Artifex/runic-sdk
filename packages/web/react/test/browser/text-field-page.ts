// The page of text-field.browser.test.ts: a React input bound with useTextField to a View of the slow mock.
import { createElement } from "react";
import { createRoot } from "react-dom/client";
import { connectView, type ViewConnection } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import * as wire from "@runic-artifex/views/generated/wire";
import { useTextField, useView } from "../../dist/index.js";

type State = { readonly title: string };
const query = new URLSearchParams(location.search);
const bridge = installMockBridge(createMockBridge({ latency: "slow", reorderRequests: query.get("reorder") === "1" }));
const view = bridge.view("editor", { state: { title: "" }, set: { title: (_state, value) => ({ title: value as string }) } });
const connect = () => connectView<State>({ contract: "Tests.Editor:fp", route: "editor", members: { title: wire.string } });
// Every value script sets on the input; each must be a prefix of what the user typed.
const shown: string[] = [];

function Editor() {
  const editor = useView({ connect });
  const title = useTextField(editor, state => state.title, (client: ViewConnection<State>, value) => client.set("title", value),
    { coalesce: query.get("coalesce") !== "0" });
  return createElement("input", {
    id: "title", "aria-label": "Title", "data-editing": String(title.editing), "data-ready": String(editor.client !== undefined),
    // The field stays enabled while its own writes are in flight.
    disabled: editor.client === undefined, value: title.value, onChange: title.onChange,
    onCompositionStart: title.onCompositionStart, onCompositionEnd: title.onCompositionEnd,
    ref: (element: HTMLInputElement | null) => { if (element) recordValues(element); },
  });
}

function recordValues(element: HTMLInputElement): void {
  if (Object.hasOwn(element, "value")) return;
  const property = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")!;
  Object.defineProperty(element, "value", {
    configurable: true,
    get() { return property.get!.call(this); },
    set(value: string) { shown.push(value); property.set!.call(this, value); },
  });
}

createRoot(document.getElementById("root")!).render(createElement(Editor));
Object.assign(globalThis, { textFieldTest: { shown, title: () => view.state["title"], sets: () => bridge.calls.filter(call => call.kind === "set").length } });
