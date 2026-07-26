import assert from "node:assert/strict";
import test from "node:test";

import {
  MvvmCollection,
  MvvmCommand,
  MvvmCommandWithArgument,
  MvvmProperty,
  MvvmReadonlyProperty,
} from "../dist/esm/index.js";

test("typed handles preserve generated member types and operations", async () => {
  const calls = [];
  const snapshot = {
    phase: "open",
    synchronized: true,
    revision: 3n,
    properties: new Map([[1, "Ada"], [2, { count: 4 }]]),
    collections: new Map([[3, [{ id: "one" }]]]),
    commands: new Map([[4, { canExecute: true, isExecuting: false }]]),
    validation: new Map([[1, ["required"]]]),
  };
  const projection = {
    snapshot,
    property: (member) => snapshot.properties.get(member),
    collection: (member) => snapshot.collections.get(member),
    command: (member) => snapshot.commands.get(member),
    validation: (member) => snapshot.validation.get(member),
    setProperty: async (member, value) => {
      calls.push(["set", member, value]);
      return { request: "00000000-0000-4000-8000-000000000001", revision: 4n };
    },
    execute: (member, options = {}) => {
      calls.push(["execute", member, options.argument]);
      return {
        request: "00000000-0000-4000-8000-000000000002",
        completion: Promise.resolve({
          request: "00000000-0000-4000-8000-000000000002",
          revision: 5n,
          value: null,
        }),
        cancel: async () => false,
      };
    },
  };

  const name = new MvvmProperty(projection, 1);
  const summary = new MvvmReadonlyProperty(projection, 2);
  const items = new MvvmCollection(projection, 3);
  const save = new MvvmCommand(projection, 4);
  const remove = new MvvmCommandWithArgument(projection, 5);

  assert.equal(name.value, "Ada");
  assert.deepEqual(name.validation, ["required"]);
  assert.deepEqual(summary.value, { count: 4 });
  assert.deepEqual(items.value, [{ id: "one" }]);
  assert.equal(save.state.canExecute, true);
  await name.set("Grace");
  await save.execute().completion;
  await remove.execute("one").completion;
  assert.deepEqual(calls, [
    ["set", 1, "Grace"],
    ["execute", 4, undefined],
    ["execute", 5, "one"],
  ]);
});
