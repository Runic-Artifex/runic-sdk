// The page of field.browser.test.ts: a React form whose inputs are bound with useField to a View of the slow mock: a
// checked text field written on blur, a number field and a checkbox.
import { createElement, Fragment } from "react";
import { createRoot } from "react-dom/client";
import type { BridgeField, FieldBaseline } from "@runic-artifex/views";
import { connectView, type ViewConnection } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import * as wire from "@runic-artifex/views/generated/wire";
import { FieldError, useField, useView } from "../../dist/index.js";

type State = { readonly title: string; readonly step: number; readonly done: boolean };
type Client = ViewConnection<State>;
const bridge = installMockBridge(createMockBridge({ latency: "slow" }));
const view = bridge.view("form", {
  state: { title: "", step: 0, done: false }, checkedFields: ["title"],
  set: { title: (_state, value) => { if (value === "Bad") throw new Error("Titles cannot be Bad."); } },
});
const connect = () => connectView<State>({
  contract: "Tests.Form:fp", route: "form", members: { title: wire.string, step: wire.finiteNumber, done: wire.boolean }, checkedFields: ["title"],
});

const Fields = {
  title: {
    name: "title", kind: "text", read: state => state.title,
    write: (client, value) => client.set("title", value),
    checked: (client, value, options) => client.write("title", {
      requestId: options.requestId, expectedVersion: options.baseline.version, expectedValue: options.baseline.value, value,
    }, wire.string),
    baseline: client => client.fieldBaseline("title") as FieldBaseline<string>,
  } satisfies BridgeField<Client, State, string>,
  step: { name: "step", kind: "number", read: state => state.step, write: (client, value) => client.set("step", value) } satisfies BridgeField<Client, State, number>,
  done: { name: "done", kind: "boolean", read: state => state.done, write: (client, value) => client.set("done", value) } satisfies BridgeField<Client, State, boolean>,
};

function Form() {
  const form = useView({ connect });
  const title = useField(form, Fields.title, { commit: "pause", checked: true });
  const step = useField(form, Fields.step);
  const done = useField(form, Fields.done);
  const ready = String(form.client !== undefined);
  return createElement(Fragment, null,
    createElement("input", { id: "title", "aria-label": "Title", "data-ready": ready, "data-editing": String(title.editing),
      "data-error": title.error instanceof FieldError ? title.error.kind : "", ...title.inputProps }),
    createElement("input", { id: "step", "aria-label": "Step", inputMode: "decimal", "data-editing": String(step.editing), ...step.inputProps }),
    createElement("input", { id: "done", "aria-label": "Done", type: "checkbox", "data-editing": String(done.editing), ...done.inputProps }));
}

createRoot(document.getElementById("root")!).render(createElement(Form));
Object.assign(globalThis, { fieldTest: { state: () => view.state, sets: (member: string) => bridge.calls.filter(call => call.kind === "set" && call.message["member"] === member).map(call => call.message["value"]) } });
