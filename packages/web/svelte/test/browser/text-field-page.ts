// The page of text-field.browser.test.ts: a Svelte input with `bind:value` bound with useTextField to a View of the
// slow mock.
import { mount } from "svelte";
import { connectView } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import * as wire from "@runic-artifex/views/generated/wire";
import TextFieldPage from "./TextFieldPage.svelte";

const query = new URLSearchParams(location.search);
const bridge = installMockBridge(createMockBridge({ latency: "slow", reorderRequests: query.get("reorder") === "1" }));
const view = bridge.view("editor", { state: { title: "" }, set: { title: (_state, value) => ({ title: value as string }) } });
const connect = () => connectView<{ readonly title: string }>({ contract: "Tests.Editor:fp", route: "editor", members: { title: wire.string } });
// Every value script sets on the input; each must be a prefix of what the user typed.
const shown: string[] = [];

function record(element: HTMLInputElement): void {
  if (Object.hasOwn(element, "value")) return;
  const property = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")!;
  Object.defineProperty(element, "value", {
    configurable: true,
    get() { return property.get!.call(this); },
    set(value: string) { shown.push(value); property.set!.call(this, value); },
  });
}

mount(TextFieldPage, { target: document.getElementById("root")!, props: { source: { connect }, record } });
Object.assign(globalThis, { textFieldTest: { shown, title: () => view.state["title"], sets: () => bridge.calls.filter(call => call.kind === "set").length } });
