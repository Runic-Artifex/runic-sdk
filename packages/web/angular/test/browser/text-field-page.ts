// The page of text-field.browser.test.ts: an Angular input bound with injectTextField to a View of the slow mock.
// Like the unit tests it runs the partially compiled package in JIT mode.
import "@angular/compiler";
import { Component, provideZonelessChangeDetection } from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";
import { connectView, type ViewConnection } from "@runic-artifex/views/generated";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import * as wire from "@runic-artifex/views/generated/wire";
import { injectTextField, injectView } from "../../dist/esm/index.js";

type State = { readonly title: string };
const query = new URLSearchParams(location.search);
const bridge = installMockBridge(createMockBridge({ latency: "slow", reorderRequests: query.get("reorder") === "1" }));
const view = bridge.view("editor", { state: { title: "" }, set: { title: (_state, value) => ({ title: value as string }) } });
const connect = () => connectView<State>({ contract: "Tests.Editor:fp", route: "editor", members: { title: wire.string } });

// Every value script sets on an input; each must be a prefix of what the user typed.
const shown: string[] = [];
const property = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")!;
Object.defineProperty(HTMLInputElement.prototype, "value", {
  configurable: true,
  get() { return property.get!.call(this); },
  set(value: string) { shown.push(value); property.set!.call(this, value); },
});

class Editor {
  readonly editor = injectView({ connect });
  readonly title = injectTextField(this.editor, state => state.title, (client: ViewConnection<State>, value) => client.set("title", value),
    { coalesce: query.get("coalesce") !== "0" });
}
// The field stays enabled while its own writes are in flight.
Component({
  selector: "test-editor",
  standalone: true,
  template: `<input id="title" aria-label="Title" [attr.data-editing]="title.editing()" [attr.data-ready]="editor.client() !== undefined"
    [disabled]="editor.client() === undefined" [value]="title.value()" (input)="title.input($event)"
    (compositionstart)="title.compositionStart()" (compositionend)="title.compositionEnd($event)">`,
})(Editor);

document.body.append(document.createElement("test-editor"));
await bootstrapApplication(Editor, { providers: [provideZonelessChangeDetection()] });
Object.assign(globalThis, { textFieldTest: { shown, title: () => view.state["title"], sets: () => bridge.calls.filter(call => call.kind === "set").length } });
