import assert from "node:assert/strict";
import { test } from "node:test";
import { attachField, bindField, commitFields, createFieldController, FieldError } from "../dist/index.js";

type State = { readonly title: string; readonly done: boolean; readonly size: number };

// What the helper uses of an element: its tag, type, value or checked, events and aria-invalid.
function fakeElement(tagName: string, type = "text") {
  const target = new EventTarget();
  const attributes = new Map<string, string>();
  return Object.assign(target, {
    tagName, type, value: "", checked: false,
    setAttribute(name: string, value: string) { attributes.set(name, value); },
    getAttribute(name: string) { return attributes.get(name) ?? null; },
    fire(event: string) { target.dispatchEvent(new Event(event)); },
  });
}
type FakeElement = ReturnType<typeof fakeElement>;
const asElement = (element: FakeElement) => element as unknown as HTMLInputElement;

function fakeClient(initial: State) {
  let current = initial;
  const listeners = new Set<(state: State) => void>();
  const writes: Partial<State>[] = [];
  return {
    writes,
    get snapshot() { return current; },
    subscribe(listener: (state: State) => void) { listener(current); listeners.add(listener); return () => { listeners.delete(listener); }; },
    dispose() {},
    get listeners() { return listeners.size; },
    async set(change: Partial<State>) {
      writes.push(change);
      current = { ...current, ...change };
      for (const listener of [...listeners]) listener(current);
      return current;
    },
  };
}
type FakeClient = ReturnType<typeof fakeClient>;

const tick = () => new Promise<void>(resolve => setTimeout(resolve, 0));

test("bindField shows the member, writes what is typed and follows the client", async () => {
  const client = fakeClient({ title: "Draft", done: false, size: 1 });
  const input = fakeElement("INPUT");
  const title = bindField(asElement(input), client, {
    kind: "text", read: state => state.title, write: (target: FakeClient, value: string) => target.set({ title: value }),
  });
  assert.equal(input.value, "Draft");
  assert.equal(input.getAttribute("aria-invalid"), "false");
  input.value = "Draft 2";
  input.fire("input");
  await tick();
  assert.deepEqual(client.writes, [{ title: "Draft 2" }]);
  await client.set({ title: "Remote" });
  await title.commit();
  assert.equal(input.value, "Remote");
  title.dispose();
  assert.equal(client.listeners, 0);
  input.value = "ignored";
  input.fire("input");
  await tick();
  assert.equal(client.writes.length, 2, "a disposed field writes nothing");
});

test("bindField writes nothing during composition, commits on blur, and wires checkboxes and selects", async () => {
  const client = fakeClient({ title: "", done: false, size: 1 });
  const input = fakeElement("INPUT");
  const title = bindField(asElement(input), client, {
    kind: "text", read: state => state.title, write: (target: FakeClient, value: string) => target.set({ title: value }),
  }, { commit: "pause", delay: 10_000 });
  input.fire("compositionstart");
  input.value = "か";
  input.fire("input");
  input.fire("compositionend");
  input.fire("blur");
  await title.commit();
  assert.deepEqual(client.writes, [{ title: "か" }]);

  const checkbox = fakeElement("INPUT", "checkbox");
  const done = bindField(asElement(checkbox), client, {
    kind: "boolean", read: state => state.done, write: (target: FakeClient, value: boolean) => target.set({ done: value }),
  });
  checkbox.checked = true;
  checkbox.fire("change");
  await done.commit();
  assert.equal(client.snapshot.done, true);
  assert.equal(checkbox.checked, true);

  const select = fakeElement("SELECT");
  const size = bindField(asElement(select), client, {
    kind: "number", integer: true, read: state => state.size, write: (target: FakeClient, value: number) => target.set({ size: value }),
  });
  assert.equal(select.value, "1");
  select.value = "5";
  select.fire("input");
  await tick();
  assert.equal(client.snapshot.size, 1, "a select writes on change");
  select.fire("change");
  await size.commit();
  assert.equal(client.snapshot.size, 5);
  for (const field of [title, done, size]) field.dispose();
});

test("bindField with commit: change writes typed text on blur, and each choice of a checkbox or select at once (#1316)", async () => {
  const client = fakeClient({ title: "", done: false, size: 1 });
  const input = fakeElement("INPUT");
  const title = bindField(asElement(input), client, {
    kind: "text", read: state => state.title, write: (target: FakeClient, value: string) => target.set({ title: value }),
  }, { commit: "change" });
  input.value = "Host";
  input.fire("input");
  input.value = "Host name";
  input.fire("input");
  // A text input's own `change` comes with its blur.
  await tick();
  assert.deepEqual(client.writes, [], "typing writes nothing");
  assert.equal(title.current.editing, true);
  input.fire("blur");
  await title.commit();
  assert.deepEqual(client.writes, [{ title: "Host name" }]);

  const checkbox = fakeElement("INPUT", "checkbox");
  const done = bindField(asElement(checkbox), client, {
    kind: "boolean", read: state => state.done, write: (target: FakeClient, value: boolean) => target.set({ done: value }),
  }, { commit: "change" });
  checkbox.checked = true;
  checkbox.fire("change");
  await tick();
  assert.equal(client.snapshot.done, true, "a checkbox writes each change");

  const select = fakeElement("SELECT");
  const size = bindField(asElement(select), client, {
    kind: "number", integer: true, read: state => state.size, write: (target: FakeClient, value: number) => target.set({ size: value }),
  }, { commit: "change" });
  select.value = "5";
  select.fire("change");
  await tick();
  assert.equal(client.snapshot.size, 5, "a select writes each change");

  for (const field of [title, done, size]) field.dispose();
});

test("bindField binds a radio group to one field: the checked radio follows the field, a choice writes its value (#1327)", async () => {
  const client = fakeClient({ title: "", done: false, size: 2 });
  const radios = ["1", "2", "3"].map(value => Object.assign(fakeElement("INPUT", "radio"), { value }));
  // The browser unchecks the other radios of a group when one is chosen.
  const choose = (chosen: FakeElement) => {
    for (const radio of radios) radio.checked = radio === chosen;
    chosen.fire("change");
  };
  const definition = {
    kind: "number" as const, integer: true, read: (state: State) => state.size,
    write: (target: FakeClient, value: number) => target.set({ size: value }),
  };
  const size = bindField(radios.map(asElement), client, definition);
  assert.deepEqual(radios.map(radio => radio.value), ["1", "2", "3"], "each radio keeps its option");
  assert.deepEqual(radios.map(radio => radio.checked), [false, true, false]);

  choose(radios[2]!);
  await size.commit();
  assert.deepEqual(client.writes, [{ size: 3 }]);
  // A radio that was not checked by its change writes nothing.
  radios[0]!.fire("change");
  await size.commit();
  assert.deepEqual(client.writes, [{ size: 3 }]);

  // A value from .NET moves the checked radio.
  await client.set({ size: 1 });
  assert.deepEqual(radios.map(radio => radio.checked), [true, false, false]);
  assert.deepEqual(radios.map(radio => radio.value), ["1", "2", "3"]);
  size.dispose();

  // With commit: "change" a radio's choice is written at once, without waiting for the blur.
  client.writes.length = 0;
  const changed = bindField(radios.map(asElement), client, definition, { commit: "change" });
  choose(radios[1]!);
  await tick();
  assert.deepEqual(client.writes, [{ size: 2 }]);
  assert.deepEqual(radios.map(radio => radio.checked), [false, true, false]);
  changed.dispose();

  // One radio bound on its own takes part in its group too.
  const single = bindField(asElement(radios[0]!), client, definition);
  assert.equal(radios[0]!.checked, false);
  assert.equal(radios[0]!.value, "1");
  choose(radios[0]!);
  await single.commit();
  assert.equal(client.snapshot.size, 1);
  single.dispose();
});

test("attachField wires a controller that the caller feeds, and commitFields rejects with a failed field's error", async () => {
  const client = fakeClient({ title: "A", done: false, size: 1 });
  const controller = createFieldController<State, FakeClient, number>({
    kind: "number", integer: true, read: state => state.size, write: (target, value) => target.set({ size: value }),
  });
  controller.setSource({ state: client.snapshot, client });
  const input = fakeElement("INPUT", "number");
  const detach = attachField(asElement(input), controller);
  assert.equal(input.value, "1");
  input.value = "1.5";
  input.fire("input");
  assert.equal(input.getAttribute("aria-invalid"), "true");
  assert.ok(controller.current.error instanceof FieldError);
  await assert.rejects(commitFields(controller), FieldError);
  detach();
  controller.dispose();
});
