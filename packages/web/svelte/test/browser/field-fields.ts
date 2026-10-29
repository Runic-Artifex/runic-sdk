// The View of field.browser.test.ts's page: a checked text field, a number and a boolean.
import type { FieldBaseline } from "@runic-artifex/views";
import type { ViewConnection } from "@runic-artifex/views/generated";
import * as wire from "@runic-artifex/views/generated/wire";
import type { BridgeField } from "../../dist/index.js";

export type State = { readonly title: string; readonly step: number; readonly done: boolean };
export type Client = ViewConnection<State>;

export const Fields = {
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
