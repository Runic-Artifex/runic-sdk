// sdk#1395: useField binds the fields of a generated client whose checked member and rows have fields named like a
// field descriptor's own members (`InvoiceFields.billing.name`, `InvoiceFields.lines.at(key).read`).
import * as React from "react";
import { createElement, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, expect, test } from "vitest";
import { createMockBridge, installMockBridge } from "@runic-artifex/views/mock";
import { useField, useView, type FieldHandle } from "../dist/index.js";
import { connectInvoice, InvoiceFields } from "../../views/test/fixtures/field-names/generated/invoice.js";
import { mockInvoice } from "../../views/test/fixtures/field-names/generated/invoice.mock.js";

const act: typeof React.act = React.act ?? (React as unknown as { unstable_act: typeof React.act }).unstable_act;
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const line = (id: number) => ({ id, name: `n${id}`, read: "r", write: "w", checked: false, baseline: id, errors: "e", validating: false, container: "c" });

let root: Root | undefined;
afterEach(async () => {
  await act(async () => { root?.unmount(); });
  root = undefined;
  document.body.innerHTML = "";
});

const until = async (condition: () => boolean) => {
  for (let round = 0; !condition(); round++) {
    if (round > 500) throw new Error("timed out");
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 2)); });
  }
};

test("useField binds fields named like a descriptor's own members", async () => {
  const bridge = installMockBridge(createMockBridge());
  mockInvoice(bridge, { state: {
    billing: { name: "Ada", read: "r", write: "w", checked: false, baseline: 1, errors: "e", validating: false, container: "c",
      contact: { name: "Grace", read: "cr" } },
    lines: [line(1), line(2)],
  } });
  const client = await connectInvoice();
  let name: FieldHandle<string> | undefined;
  let cell: FieldHandle<string> | undefined;
  let checked: FieldHandle<boolean> | undefined;
  function Editor(): ReactNode {
    const view = useView(client);
    name = useField(view, InvoiceFields.billing.name);
    cell = useField(view, InvoiceFields.lines.at(2).read);
    checked = useField(view, InvoiceFields.billing.checked);
    return null;
  }
  await act(async () => {
    root = createRoot(document.body.appendChild(document.createElement("div")));
    root.render(createElement(Editor));
  });
  expect(name!).toMatchObject({ value: "Ada", text: "Ada", editing: false });
  expect(cell!.value).toBe("r");
  expect(checked!.value).toBe(false);

  await act(async () => { name!.input("Lovelace"); cell!.input("second"); checked!.input(true); });
  await until(() => !name!.editing && !cell!.editing && !checked!.editing);
  expect(name!).toMatchObject({ value: "Lovelace", error: undefined });
  expect(cell!).toMatchObject({ value: "second", error: undefined });
  expect(checked!).toMatchObject({ value: true, error: undefined });
  expect(client.snapshot.billing).toMatchObject({ name: "Lovelace", checked: true, read: "r" });
  expect(client.snapshot.lines[1]).toMatchObject({ id: 2, read: "second", name: "n2" });
  client.dispose();
});
