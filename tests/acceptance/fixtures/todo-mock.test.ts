import assert from "node:assert/strict";

import { startNativeMvvmApplication } from "@webuitoolkit/mvvm";
import {
  AdvancedTodoContract,
  SimpleTodoContract,
} from "../../samples/Todo.Frontends/shared/contracts";
import {
  createTodoMockChannel,
  TODO_MOCK_MARKER,
} from "../../samples/Todo.Frontends/shared/todo.mock";

await verifySimpleTodo();
await verifyAdvancedTodo();

assert.equal(TODO_MOCK_MARKER, "webuitoolkit.todo.mock/1");
console.log(
  "Todo frontend-only fixture passed generated property, validation, " +
  "collection, command, filter, workflow, reconnect, and disposal gates.",
);

async function verifySimpleTodo(): Promise<void> {
  const application = await startNativeMvvmApplication({
    contract: SimpleTodoContract,
    channelFactory: () => createTodoMockChannel("simple"),
    pageLifetime: null,
  });
  try {
    assert.equal(application.contract.items.value.length, 2);
    await application.contract.newTitle.set("Protocol-only task");
    assert.deepEqual(application.contract.newTitle.validation, []);
    await application.contract.add.execute().completion;
    assert.equal(application.contract.items.value.length, 3);

    const added = application.contract.items.value.find((item) =>
      item.title === "Protocol-only task");
    assert.ok(added);
    await application.contract.toggle.execute(added.id).completion;
    assert.equal(
      application.contract.items.value.find((item) => item.id === added.id)
        ?.isCompleted,
      true,
    );
    await application.contract.remove.execute(added.id).completion;
    assert.equal(application.contract.items.value.length, 2);

    await application.contract.newTitle.set("x");
    assert.deepEqual(
      application.contract.newTitle.validation,
      ["Title must contain at least two characters."],
    );
    await application.reconnect();
    assert.equal(application.contract.items.value.length, 2);
  } finally {
    await application.dispose();
  }
}

async function verifyAdvancedTodo(): Promise<void> {
  const application = await startNativeMvvmApplication({
    contract: AdvancedTodoContract,
    channelFactory: () => createTodoMockChannel("advanced"),
    pageLifetime: null,
  });
  try {
    assert.equal(application.contract.items.value.length, 3);
    assert.equal(application.contract.state.value?.totalCount, 3);
    assert.match(
      application.contract.diagnostics.value[0]?.message ?? "",
      /Frontend-only protocol fixture/,
    );

    await application.contract.newTitle.set("Advanced mock task");
    await application.contract.newNotes.set("Created without a native host.");
    await application.contract.newPriority.set("High");
    await application.contract.add.execute().completion;
    assert.equal(application.contract.state.value?.totalCount, 4);
    assert.ok(application.contract.items.value.some((item) =>
      item.title === "Advanced mock task" && item.priority === "High"));

    await application.contract.query.set("generated framework");
    await application.contract.filter.set("All");
    await application.contract.applyFilter.execute().completion;
    assert.equal(application.contract.items.value.length, 1);

    await application.contract.query.set("");
    await application.contract.applyFilter.execute().completion;
    await application.contract.newTitle.set("Guided mock task");
    await application.contract.wizardStart.execute().completion;
    assert.equal(
      application.contract.state.value?.wizardStep,
      "todo.create.details",
    );
    await application.contract.wizardNext.execute().completion;
    assert.equal(
      application.contract.state.value?.wizardStep,
      "todo.create.review",
    );
    await application.contract.wizardFinish.execute().completion;
    assert.equal(application.contract.state.value?.wizardStep, null);
    assert.ok(application.contract.items.value.some((item) =>
      item.title === "Guided mock task"));
  } finally {
    await application.dispose();
  }
}
