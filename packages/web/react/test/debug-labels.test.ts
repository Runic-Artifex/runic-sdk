// React DevTools labels (#1041): the development build (dist/development/, the `development` export condition) passes
// each hook's value and a label function to `useDebugValue`; the production build (dist/) does not call it. React calls
// the label function only while React DevTools inspects a component, so the spy below calls it to read the label.
import * as React from "react";
import { createElement, StrictMode, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, describe, expect, test, vi } from "vitest";
import { BridgeError, type BridgeField } from "@runic-artifex/views";
import { connectView } from "@runic-artifex/views/generated";
import * as bridgeWire from "@runic-artifex/views/generated/wire";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";

const labels: unknown[] = [];
vi.mock("react", async original => {
  const react = await original<typeof import("react")>();
  return {
    ...react,
    act: react.act ?? (react as unknown as { unstable_act: typeof react.act }).unstable_act,
    useDebugValue: (value: unknown, format?: (value: unknown) => unknown) => {
      labels.push(format ? format(value) : value);
      react.useDebugValue(value, format);
    },
  };
});

// The development build shares the production build's declarations (dist/*.d.ts).
const load = (entry: string): Promise<unknown> => import(new URL(`../dist/development/${entry}`, import.meta.url).href);
const development = await load("index.js") as typeof import("../dist/index.js");
const production = await import("../dist/index.js");
const { commandLabel, fieldLabel, operationLabel, preview, viewLabel } = await load("debug-labels.js") as typeof import("../dist/debug-labels.js");

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
// React 18.3 (the peer floor) has `act` as `unstable_act`; the mock below gives it as `act`.
const act: typeof React.act = React.act;
const contract = "Demo.Notes.NotesViewModel:ABC";
type State = { readonly title: string };
type Client = Awaited<ReturnType<typeof connectView<State>>>;
const title: BridgeField<Client, State, string> = {
  name: "title", kind: "text", read: state => state.title, write: (client, value) => client.set("title", value),
};

let root: Root | undefined;
afterEach(async () => {
  await act(async () => { root?.unmount(); });
  root = undefined;
  labels.length = 0;
  document.body.innerHTML = "";
});

async function mount(element: ReactNode): Promise<void> {
  await act(async () => {
    root = createRoot(document.body.appendChild(document.createElement("div")));
    root.render(createElement(StrictMode, null, element));
  });
}

async function notes() {
  const bridge = createMockBridge();
  installMockBridge(bridge);
  bridge.view("notes", { state: { title: "Draft" }, contract, commands: { save: () => ({}) } });
  return await connectView<State>({ contract, route: "notes", members: { title: bridgeWire.string } });
}

describe("React DevTools labels", () => {
  test("the development build labels the View, its fields, commands, operations and the connection", async () => {
    const client = await notes();
    function Page() {
      const view = development.useView(client);
      development.useField(view, title);
      development.useCommand(() => client.command("save"));
      development.useOperation(() => undefined);
      development.useLatestOperation();
      development.useBridgeConnection();
      development.useViewState(view, state => state.title);
      return null;
    }
    await mount(createElement(Page));
    const shown = labels.map(String);
    expect(shown).toContain(viewLabel({ client, pending: false, error: undefined }));
    expect(shown.some(label => /^NotesViewModel · notes · rev \d+$/.test(label))).toBe(true);
    expect(shown).toContain('title = "Draft"');
    expect(shown).toContain("idle");
    expect(shown).toContain("connected");
    client.dispose();
  });

  test("the production build does not call useDebugValue", async () => {
    const client = await notes();
    function Page() {
      const view = production.useView(client);
      production.useField(view, title);
      production.useCommand(() => client.command("save"));
      production.useOperation(() => undefined);
      production.useBridgeConnection();
      return null;
    }
    await mount(createElement(Page));
    expect(labels).toEqual([]);
    client.dispose();
  });

  test("labels are short and do not traverse values", () => {
    expect(viewLabel({ client: undefined, pending: true, error: undefined })).toBe("connecting");
    expect(viewLabel({ client: undefined, pending: false, error: undefined })).toBe("no View");
    expect(viewLabel({ client: undefined, pending: false, error: new BridgeError("timeout", "No Bridge.") })).toBe("failed: BridgeError: No Bridge.");
    expect(viewLabel({ client: { snapshot: {}, subscribe: () => () => {}, dispose() {} }, pending: false, error: undefined })).toBe("View");
    expect(preview("x".repeat(100))).toBe(JSON.stringify(`${"x".repeat(39)}…`));
    expect(preview({ deep: { deeper: 1 } })).toBe("{…}");
    expect(preview([1, 2, 3])).toBe("Array(3)");
    expect(fieldLabel("step", { value: 2, editing: true, error: undefined, errors: ["Too small.", "Odd."], conflict: { value: 3, version: 2 }, validating: true }))
      .toBe("step = 2 · editing · validating · conflict · 2 errors");
    expect(commandLabel({ pending: true, error: undefined, failure: undefined })).toBe("running");
    expect(commandLabel({ pending: false, error: new Error("Boom."), failure: undefined })).toBe("failed: Error: Boom.");
    const idle = { pending: false, admitting: false, status: undefined, progress: undefined, error: undefined, cancelled: false, cancellationRequested: false, cancelling: false };
    expect(operationLabel(idle)).toBe("idle");
    expect(operationLabel({ ...idle, admitting: true })).toBe("starting");
    expect(operationLabel({ ...idle, pending: true, progress: 0.4 })).toBe("running 40%");
    expect(operationLabel({ ...idle, pending: true, cancellationRequested: true })).toBe("cancelling");
    expect(operationLabel({ ...idle, status: { kind: "succeeded" } })).toBe("succeeded");
    expect(operationLabel({ ...idle, cancelled: true, blocked: true })).toBe("cancelled · blocked");
  });
});
