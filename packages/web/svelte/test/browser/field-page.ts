// The page of field.browser.test.ts: a Svelte form whose inputs are wired with useField's attachment to a View of the
// slow mock: a checked text field written on blur, a number input and a checkbox.
import { mount } from "svelte";
import { connectView } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import * as wire from "@runic-artifex/views/generated/wire";
import FieldPage from "./FieldPage.svelte";
import type { State } from "./field-fields.js";

const bridge = installMockBridge(createMockBridge({ latency: "slow" }));
const view = bridge.view("form", {
  state: { title: "", step: 0, done: false }, checkedFields: ["title"],
  set: { title: (_state, value) => { if (value === "Bad") throw new Error("Titles cannot be Bad."); } },
});
const connect = () => connectView<State>({
  contract: "Tests.Form:fp", route: "form", members: { title: wire.string, step: wire.finiteNumber, done: wire.boolean }, checkedFields: ["title"],
});

mount(FieldPage, { target: document.getElementById("root")!, props: { source: { connect } } });
Object.assign(globalThis, { fieldTest: { state: () => view.state, sets: (member: string) => bridge.calls.filter(call => call.kind === "set" && call.message["member"] === member).map(call => call.message["value"]) } });
