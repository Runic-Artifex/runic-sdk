// The page of field.browser.test.ts: a Vue form whose inputs are bound with useField and `v-model` to a View of the
// slow mock: a checked text field written on blur, a number input and a checkbox.
import { createApp, defineComponent, h, vModelCheckbox, vModelText, withDirectives } from "vue";
import type { FieldBaseline } from "@runic-artifex/views";
import { connectView, type ViewConnection } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import * as wire from "@runic-artifex/views/generated/wire";
import { FieldError, useField, useView, type BridgeField } from "../../dist/index.js";

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

const Form = defineComponent({
  setup() {
    const form = useView({ connect });
    const title = useField(form, Fields.title, { commit: "pause", checked: true });
    const step = useField(form, Fields.step);
    const done = useField(form, Fields.done);
    // What the template compiles to:
    // <input id="title" v-model="title.value" @blur="title.commit" :aria-invalid="…">
    // <input id="step" type="number" v-model="step.text" @blur="step.commit">
    // <input id="done" type="checkbox" v-model="done.value">
    return () => [
      withDirectives(h("input", {
        id: "title", "aria-label": "Title", "data-ready": String(form.client.value !== undefined), "data-editing": String(title.editing),
        "data-error": title.error instanceof FieldError ? title.error.kind : "", "aria-invalid": title.errors.length > 0 || title.error !== undefined,
        "onUpdate:modelValue": ($event: string) => { title.value = $event; }, onBlur: title.commit,
      }), [[vModelText, title.value]]),
      withDirectives(h("input", {
        id: "step", type: "number", "aria-label": "Step", "data-editing": String(step.editing),
        "onUpdate:modelValue": ($event: string) => { step.text = $event; }, onBlur: step.commit,
      }), [[vModelText, step.text]]),
      withDirectives(h("input", {
        id: "done", type: "checkbox", "aria-label": "Done", "data-editing": String(done.editing),
        "onUpdate:modelValue": ($event: boolean) => { done.value = $event; },
      }), [[vModelCheckbox, done.value]]),
    ];
  },
});

createApp(Form).mount("#root");
Object.assign(globalThis, { fieldTest: { state: () => view.state, sets: (member: string) => bridge.calls.filter(call => call.kind === "set" && call.message["member"] === member).map(call => call.message["value"]) } });
