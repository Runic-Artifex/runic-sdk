// field-paths/1 (C4): descriptors of fields below a member and of row cells, path writes through the client and the
// mock bridge, conflicts per cell, and validation messages at the row's index.
import assert from "node:assert/strict";
import { afterEach, beforeEach, test } from "node:test";
import { clock } from "../dist/clock.js";
import { createFieldController, fieldErrorsAt, fieldValidatingAt, type BridgeValidationState, type FieldBaseline } from "../dist/index.js";
import { connectView, defineCollection, defineCollections, fieldPath, rowFields, rowIndex, type FieldPathClient } from "../dist/generated.js";
import { createMockBridge, installMockBridge, type MockBridge } from "../dist/mock.js";
import * as wire from "../dist/wire.js";

const realClock = { ...clock };
afterEach(() => { Object.assign(clock, realClock); });

interface Line { readonly id: number; readonly product: string; readonly quantity: number }
interface Address { readonly street: string; readonly city: string }
interface State { readonly address: Address; readonly lines: readonly Line[]; readonly validation: BridgeValidationState }

const line = (value: unknown): Line => wire.object(value, item => ({ id: wire.integer(item["id"], 0, 1e9), product: wire.string(item["product"]), quantity: wire.integer(item["quantity"], -1e9, 1e9) }));
const validation = (value: unknown): BridgeValidationState => value as BridgeValidationState;

let bridge: MockBridge;
let invoice: ReturnType<MockBridge["view"]>;
beforeEach(() => {
  bridge = installMockBridge(createMockBridge());
  invoice = bridge.view("invoice", {
    state: { address: { street: "", city: "Bern" }, lines: [{ id: 1, product: "Pens", quantity: 2 }, { id: 2, product: "Ink", quantity: 1 }],
      validation: { hasErrors: false, truncated: false, errors: [] } },
    checkedFields: ["address"],
    collections: { lines: (item: unknown) => String((item as Line).id) },
    fieldPaths: { address: [["street"], ["city"]], lines: [["product"], ["quantity"]] },
  });
});

async function connect() {
  const view = await connectView<State>({
    contract: "Tests.Invoice:fp", route: "invoice", checkedFields: ["address"],
    members: { address: value => wire.object(value, item => ({ street: wire.string(item["street"]), city: wire.string(item["city"]) })),
      lines: value => wire.array(value, line), validation },
    collections: defineCollections({ lines: defineCollection(line, item => String(item.id)) }),
  });
  // What the generated client adds for field paths.
  const client = Object.assign(Object.create(view) as typeof view, {
    writePath: <T>(member: string, path: readonly string[], write: Parameters<typeof view.write>[1], decode: (wire: unknown) => T) =>
      view.write<T>(member, { ...write, path }, decode),
    fieldPathVersion: () => view.fieldPathVersion(),
  }) satisfies FieldPathClient;
  return { view, client };
}
type Client = Awaited<ReturnType<typeof connect>>["client"];

// What the generator emits for InvoiceFields.
const street = fieldPath<Client, State, string, Address>({ member: "address", path: ["street"], container: state => state.address,
  read: value => value.street, encode: value => value, decode: value => wire.string(value), kind: "text", validation: state => state.validation });
const lines = rowFields("lines", key => ({
  quantity: fieldPath<Client, State, number, Line>({ member: "lines", path: ["quantity"], row: { key, rows: state => state.lines, keyOf: item => String(item.id) },
    read: value => value.quantity, encode: value => value, decode: value => wire.integer(value, -1e9, 1e9), kind: "number", integer: true,
    validation: state => state.validation }),
  product: fieldPath<Client, State, string, Line>({ member: "lines", path: ["product"], row: { key, rows: state => state.lines, keyOf: item => String(item.id) },
    read: value => value.product, encode: value => value, decode: value => wire.string(value), kind: "text", validation: state => state.validation }),
}));

const until = async (condition: () => boolean) => {
  for (let round = 0; !condition(); round++) {
    if (round > 500) assert.fail("timed out");
    await new Promise(resolve => setTimeout(resolve, 2));
  }
};

test("row fields are the same object per key, and a cell descriptor always writes checked", () => {
  assert.equal(lines.at(1), lines.at("1"));
  assert.notEqual(lines.at(1), lines.at(2));
  assert.equal(lines.at(1).quantity.checkedOnly, true);
  assert.equal(lines.at(1).quantity.name, "lines.1.quantity");
  assert.equal(street.name, "address.street");
});

test("a cell's field writes its row, and a write of a cell another writer changed is a conflict", async () => {
  const { view, client } = await connect();
  const quantity = createFieldController(lines.at(2).quantity, { commit: "pause" });
  view.subscribe(state => quantity.setSource({ state, client }));
  quantity.inputText("5");
  await quantity.commit();
  await until(() => !quantity.current.editing);
  assert.equal(quantity.current.value, 5);
  assert.deepEqual(view.snapshot.lines[1], { id: 2, product: "Ink", quantity: 5 });
  assert.equal(bridge.calls.filter(call => call.kind === "write").at(-1)?.message["path"]?.toString(), "2,quantity");

  // Another page changes the same cell while this one types.
  quantity.inputText("6");
  invoice.update(state => ({ ...state, lines: (state["lines"] as Line[]).map(item => item.id === 2 ? { ...item, quantity: 9 } : item) }));
  await until(() => view.snapshot.lines[1]?.quantity === 9);
  await quantity.commit();
  await until(() => quantity.current.conflict !== undefined);
  assert.equal(quantity.current.value, 6);
  assert.equal((quantity.current.conflict as FieldBaseline<number> | undefined)?.value, 9);
  // Keep mine: written over the other value.
  quantity.retry();
  await quantity.commit();
  await until(() => !quantity.current.editing);
  assert.equal(view.snapshot.lines[1]?.quantity, 6);
  quantity.dispose();
  view.dispose();
});

test("a change of another cell of the row is not a conflict", async () => {
  const { view, client } = await connect();
  const quantity = createFieldController(lines.at(1).quantity, { commit: "pause" });
  view.subscribe(state => quantity.setSource({ state, client }));
  quantity.inputText("3");
  invoice.update(state => ({ ...state, lines: (state["lines"] as Line[]).map(item => item.id === 1 ? { ...item, product: "Markers" } : item) }));
  await until(() => view.snapshot.lines[0]?.product === "Markers");
  await quantity.commit();
  await until(() => !quantity.current.editing);
  assert.equal(quantity.current.error, undefined);
  assert.deepEqual(view.snapshot.lines[0], { id: 1, product: "Markers", quantity: 3 });
  quantity.dispose();
  view.dispose();
});

test("a nested member's field writes the object", async () => {
  const { view, client } = await connect();
  const field = createFieldController(street);
  view.subscribe(state => field.setSource({ state, client }));
  field.inputText("Main Street 1");
  await field.commit();
  await until(() => !field.current.editing);
  assert.deepEqual(view.snapshot.address, { street: "Main Street 1", city: "Bern" });
  field.dispose();
  view.dispose();
});

test("a removed row's cell rejects its write and keeps the draft", async () => {
  const { view, client } = await connect();
  const product = createFieldController(lines.at(2).product, { commit: "pause" });
  view.subscribe(state => product.setSource({ state, client }));
  product.inputText("Toner");
  invoice.collection("lines").remove("2");
  await until(() => view.snapshot.lines.length === 1);
  await product.commit();
  await until(() => product.current.error !== undefined);
  assert.equal(product.current.value, "Toner");
  product.dispose();
  view.dispose();
});

test("the mock forgets a removed row: added again under its key, it is a new row", async () => {
  const { view, client } = await connect();
  const before = view.fieldPathVersion();
  const row = { id: 2, product: "Ink", quantity: 1 };
  invoice.collection("lines").remove("2");
  invoice.collection("lines").add(row);
  await until(() => view.snapshot.lines.length === 2 && view.fieldPathVersion() > before);
  const receipt = await client.writePath("lines", ["2", "quantity"], { requestId: "w-re", expectedVersion: before, expectedValue: 1, value: 3 },
    value => wire.integer(value, 0, 9));
  assert.equal(receipt.kind, "conflict");
  view.dispose();
});

test("the mock answers rejected when a row editor removes the row", async () => {
  bridge = installMockBridge(createMockBridge());
  invoice = bridge.view("invoice", {
    state: { address: { street: "", city: "" }, lines: [{ id: 1, product: "Pens", quantity: 2 }], validation: { hasErrors: false, truncated: false, errors: [] } },
    checkedFields: ["address"],
    collections: { lines: (item: unknown) => String((item as Line).id) },
    fieldPaths: { lines: [["quantity"]] },
    rows: { lines: (state, _key, updated) => (updated as Line).quantity === 0 ? { lines: (state["lines"] as Line[]).filter(item => item.id !== 1) } : undefined },
  });
  const { view, client } = await connect();
  const receipt = await client.writePath("lines", ["1", "quantity"], { requestId: "w-0", expectedVersion: view.fieldPathVersion(), expectedValue: 2, value: 0 },
    value => wire.integer(value, 0, 9));
  assert.equal(receipt.kind, "rejected");
  view.dispose();
});

test("a cell's errors and pending checks are found at its row's index", () => {
  const state: State = {
    address: { street: "", city: "" },
    lines: [{ id: 7, product: "a", quantity: 0 }, { id: 3, product: "b", quantity: 1 }],
    validation: {
      hasErrors: true, truncated: false,
      errors: [
        { path: ["lines", 0, "quantity"], message: "At least one." },
        { path: ["lines", 1, "quantity"], message: "Other row." },
        { path: ["lines"], message: "The list." },
        { path: ["address", "street"], message: "Street required." },
        { path: ["address"], message: "Whole address." },
      ],
      pending: [["lines", 1, "product"]],
    },
  };
  assert.deepEqual(lines.at(7).quantity.errors!(state), ["At least one."]);
  assert.deepEqual(lines.at(3).quantity.errors!(state), ["Other row."]);
  assert.deepEqual(lines.at(99).quantity.errors!(state), []);
  assert.equal(lines.at(3).product.validating!(state), true);
  assert.equal(lines.at(7).product.validating!(state), false);
  assert.deepEqual(street.errors!(state), ["Street required."]);
  assert.equal(fieldErrorsAt(state.validation, ["address", "street"]), fieldErrorsAt(state.validation, ["address", "street"]));
  assert.equal(fieldValidatingAt(state.validation, ["lines", 1]), true);
  assert.equal(rowIndex(state.lines, item => String(item.id), "3"), 1);
});
