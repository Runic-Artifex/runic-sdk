// The page of text-field.browser.test.ts: a Vue input with `v-model` bound with useTextField to a View of the slow mock.
import { createApp, defineComponent, h, vModelText, withDirectives } from "vue";
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

function recordValues(element: HTMLInputElement): void {
  if (Object.hasOwn(element, "value")) return;
  const property = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")!;
  Object.defineProperty(element, "value", {
    configurable: true,
    get() { return property.get!.call(this); },
    set(value: string) { shown.push(value); property.set!.call(this, value); },
  });
}

const Editor = defineComponent({
  setup() {
    const editor = useView({ connect });
    const { value: title, editing } = useTextField(editor, state => state.title,
      (client: ViewConnection<State>, value) => client.set("title", value), { coalesce: query.get("coalesce") !== "0" });
    // What `<input v-model="title" :disabled="!editor.client">` compiles to. The field stays enabled while its own
    // writes are in flight.
    return () => withDirectives(h("input", {
      id: "title", "aria-label": "Title", "data-editing": String(editing.value), "data-ready": String(editor.client.value !== undefined),
      disabled: editor.client.value === undefined,
      "onUpdate:modelValue": (value: string) => { title.value = value; },
      onVnodeBeforeMount: (node: { el: unknown }) => recordValues(node.el as HTMLInputElement),
    }), [[vModelText, title.value]]);
  },
});

createApp(Editor).mount("#root");
Object.assign(globalThis, { textFieldTest: { shown, title: () => view.state["title"], sets: () => bridge.calls.filter(call => call.kind === "set").length } });
