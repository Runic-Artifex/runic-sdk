import assert from "node:assert/strict";
import { test } from "node:test";
import {
  dirty,
  emptyEditor,
  importContact,
  receiveSnapshot,
  selectCustomer,
} from "./editor-state.ts";
const row = {
  id: "11111111-1111-4111-8111-111111111111",
  name: "Alex",
  email: "alex@example.com",
  company: "Studio",
  version: 1,
};
const snapshot = {
  customers: [row],
  generation: 0,
  save: {
    operationId: null as string | null,
    status: "idle",
    progress: 0,
    message: "Ready",
    issues: [],
  },
};
test("reconnect preserves an unsaved draft and its original concurrency version", () => {
  const initial = receiveSnapshot(emptyEditor, snapshot);
  const editing = { ...initial, draft: { ...row, name: "Unsaved" } };
  const next = receiveSnapshot(editing, {
    ...snapshot,
    customers: [{ ...row, version: 2, name: "Changed elsewhere" }],
  });
  assert.equal(next.draft?.name, "Unsaved");
  assert.equal(next.draft?.version, 1);
  assert.equal(dirty(next), true);
});
test("successful save adopts normalized values and clears dirty state", () => {
  const editing = {
    ...receiveSnapshot(emptyEditor, snapshot),
    draft: { ...row, name: " Updated " },
  };
  const next = receiveSnapshot(editing, {
    ...snapshot,
    generation: 3,
    customers: [{ ...row, name: "Updated", version: 2 }],
    save: { ...snapshot.save, status: "saved" },
  });
  assert.equal(next.draft?.name, "Updated");
  assert.equal(next.draft?.version, 2);
  assert.equal(dirty(next), false);
});
test("late start receipt cannot regress a completed operation", () => {
  const completed = receiveSnapshot(emptyEditor, {
    ...snapshot,
    generation: 5,
    save: { ...snapshot.save, status: "saved" },
  });
  assert.equal(
    receiveSnapshot(completed, {
      ...snapshot,
      generation: 1,
      save: { ...snapshot.save, status: "saving" },
    }),
    completed,
  );
});
test("cancelled work preserves the draft", () => {
  const editing = {
    ...receiveSnapshot(emptyEditor, snapshot),
    draft: { ...row, company: "New company" },
  };
  const next = receiveSnapshot(editing, {
    ...snapshot,
    generation: 3,
    save: { ...snapshot.save, status: "cancelled" },
  });
  assert.equal(next.draft?.company, "New company");
  assert.equal(dirty(next), true);
});
test("explicit discard selects current authoritative data", () => {
  const editing = {
    ...receiveSnapshot(emptyEditor, snapshot),
    draft: { ...row, name: "Unsaved" },
  };
  assert.equal(dirty(selectCustomer(editing, row.id)), false);
});
test("contact import only changes editable fields and checks bounded input", () => {
  const imported = importContact(
    JSON.stringify({
      name: "Imported",
      email: "import@example.com",
      company: "Elsewhere",
      id: "untrusted",
      version: 999,
    }),
    row,
  );
  assert.equal(imported.id, row.id);
  assert.equal(imported.version, 1);
  assert.equal(imported.name, "Imported");
  for (const text of [
    "null",
    "[]",
    "{}",
    "invalid",
    JSON.stringify({ name: 1, email: "e", company: "c" }),
    " ".repeat(4097),
  ])
    assert.throws(() => importContact(text, row));
});
