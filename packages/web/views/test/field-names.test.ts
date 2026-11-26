// sdk#1395: fields below a checked member and row cells named like a field descriptor's own members. The generated
// client in fixtures/field-names keeps the descriptor's members under `fieldMembers`, so `InvoiceFields.billing.name` is
// the field of billing's name, and each such field binds and writes like any other.
import assert from "node:assert/strict";
import { beforeEach, test } from "node:test";
import { createFieldController, fieldDefinition, fieldMembers, type BridgeField } from "../dist/index.js";
import { createMockBridge, installMockBridge, type MockBridge } from "../dist/mock.js";
import { connectInvoice, InvoiceFields, type InvoiceClient, type InvoiceState } from "./fixtures/field-names/generated/invoice.js";
import { mockInvoice, type InvoiceMock } from "./fixtures/field-names/generated/invoice.mock.js";

const names = ["name", "read", "write", "checked", "baseline", "errors", "validating", "container"] as const;
const billing = { name: "Ada", read: "r", write: "w", checked: false, baseline: 1, errors: "e", validating: false, container: "c",
  contact: { name: "Grace", read: "cr" } };
const line = (id: number) => ({ id, name: `n${id}`, read: "r", write: "w", checked: false, baseline: id, errors: "e", validating: false, container: "c" });

let bridge: MockBridge;
let mock: InvoiceMock;
beforeEach(() => {
  bridge = installMockBridge(createMockBridge());
  mock = mockInvoice(bridge, { state: { billing, lines: [line(1), line(2)] } });
});

const until = async (condition: () => boolean) => {
  for (let round = 0; !condition(); round++) {
    if (round > 500) assert.fail("timed out");
    await new Promise(resolve => setTimeout(resolve, 2));
  }
};

async function write<T>(client: InvoiceClient, field: BridgeField<InvoiceClient, InvoiceState, T>, value: T): Promise<void> {
  const controller = createFieldController(field);
  const unsubscribe = client.subscribe(state => controller.setSource({ state, client }));
  controller.setSource({ state: client.snapshot, client });
  controller.input(value);
  await controller.commit();
  await until(() => !controller.current.editing);
  assert.equal(controller.current.error, undefined);
  assert.deepEqual(controller.current.value, value);
  unsubscribe();
  controller.dispose();
}

test("a descriptor's own members are under fieldMembers, and the fields below it under their names", () => {
  assert.equal(InvoiceFields.billing[fieldMembers].name, "billing");
  assert.equal(fieldDefinition(InvoiceFields.billing).kind, "value");
  for (const name of names) {
    assert.equal(InvoiceFields.billing[name][fieldMembers].name, `billing.${name}`);
    assert.equal(InvoiceFields.lines.at(1)[name][fieldMembers].name, `lines.1.${name}`);
    assert.equal(InvoiceFields.lines.at(1)[name][fieldMembers].checkedOnly, true);
  }
  assert.equal(InvoiceFields.billing.contact[fieldMembers].name, "billing.contact");
  assert.equal(InvoiceFields.billing.contact.name[fieldMembers].name, "billing.contact.name");
  assert.equal(InvoiceFields.billing.contact.read[fieldMembers].name, "billing.contact.read");
  // A plain definition is its own members.
  const plain = { read: (state: InvoiceState) => state.billing, write: () => Promise.resolve() };
  assert.equal(fieldDefinition(plain), plain);
});

test("each field named like a descriptor member reads and writes its own value through createFieldController", async () => {
  const client = await connectInvoice();
  const state = client.snapshot;
  assert.equal(fieldDefinition(InvoiceFields.billing.name).read(state), "Ada");
  assert.equal(fieldDefinition(InvoiceFields.billing.contact.read).read(state), "cr");
  assert.equal(fieldDefinition(InvoiceFields.lines.at(2).baseline).read(state), 2);

  await write(client, InvoiceFields.billing.name, "Lovelace");
  await write(client, InvoiceFields.billing.read, "read");
  await write(client, InvoiceFields.billing.checked, true);
  await write(client, InvoiceFields.billing.baseline, 7);
  await write(client, InvoiceFields.billing.contact.name, "Hopper");
  await write(client, InvoiceFields.lines.at(2).name, "second");
  await write(client, InvoiceFields.lines.at(2).validating, true);
  await write(client, InvoiceFields.lines.at(1).container, "box");
  assert.deepEqual(client.snapshot.billing, { ...billing, name: "Lovelace", read: "read", checked: true, baseline: 7,
    contact: { name: "Hopper", read: "cr" } });
  assert.deepEqual(client.snapshot.lines, [{ ...line(1), container: "box" }, { ...line(2), name: "second", validating: true }]);
  assert.deepEqual(mock.state.billing, client.snapshot.billing);

  // The member's own field still writes the whole object.
  await write(client, InvoiceFields.billing, { ...client.snapshot.billing, errors: "none" });
  assert.equal(client.snapshot.billing.errors, "none");
  client.dispose();
});
