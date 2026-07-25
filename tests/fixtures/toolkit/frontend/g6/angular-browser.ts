import { PROTOCOL_IDENTITY } from "@webuitoolkit/mvvm";
import {
  AngularMvvmDirectiveLifetime,
  AngularMvvmStore,
} from "@webuitoolkit/mvvm-angular/store";

import { G5Projection } from "../g5/fake-projection.js";
import { assert, commandSubmissions, mandatoryFixtures, report } from "./report.js";

const hostile = "<img src=x onerror=\"globalThis.__g6Xss = true\">";

async function main(): Promise<void> {
  assert(PROTOCOL_IDENTITY === "webuitoolkit.mvvm/1", "The G4 protocol identity changed.");
  const projection = new G5Projection();
  const store = new AngularMvvmStore(projection);
  const amount = store.property(1);
  const validation = store.validation(1);
  let isolatedDeliveries = 0;
  store.subscribe(() => {
    throw new Error("intentional subscriber isolation probe");
  });
  store.subscribe(() => {
    isolatedDeliveries++;
  });
  assert(projection.listenerCount === 1, "Angular must own exactly one projection subscription.");

  const view = document.createElement("main");
  const amountNode = document.createElement("span");
  const hostileNode = document.createElement("span");
  const validationNode = document.createElement("span");
  amountNode.id = "amount";
  hostileNode.id = "hostile";
  validationNode.id = "validation";
  view.append(amountNode, hostileNode, validationNode);
  document.body.append(view);

  await store.setProperty(1, 7);
  const invocation = store.execute(2);
  const completion = await invocation.completion;
  const submissions = commandSubmissions(completion.value);
  amountNode.textContent = String(amount());
  assert(amount() === 7, "Angular did not publish the committed amount.");
  assert(store.snapshot().revision === 2n, "Angular did not publish both commits atomically.");
  assert(submissions === 1, "Angular command result was not preserved.");
  assert(isolatedDeliveries === 2, "Angular subscriber failure suppressed a sibling subscriber.");

  projection.setHostileText(hostile);
  hostileNode.textContent = String(store.property(3)());
  assert(hostileNode.textContent === hostile, "Angular changed hostile text data.");
  assert(hostileNode.querySelector("img") === null, "Angular interpreted hostile text as markup.");
  assert(globalThis.__g6Xss !== true, "Angular hostile text executed script.");

  projection.replaceSnapshot(7, ["required"]);
  validationNode.textContent = (validation() ?? []).join(",");
  assert(validationNode.textContent === "required", "Angular lost validation state.");
  projection.replaceSnapshot(11);
  amountNode.textContent = String(amount());
  assert(amountNode.textContent === "11", "Angular lost replacement snapshot state.");

  const directiveLifetime = new AngularMvvmDirectiveLifetime();
  directiveLifetime.wutMvvmOwnsStore = true;
  directiveLifetime.store = store;
  assert(directiveLifetime.dataContext === store, "Angular directive did not expose its DataContext.");
  directiveLifetime.destroy();
  directiveLifetime.destroy();
  assert(projection.listenerCount === 0, "Angular directive teardown leaked its projection subscription.");

  await report({
    adapter: "angular",
    status: "passed",
    fixtures: mandatoryFixtures,
    amount: 7,
    submissions,
    commits: 2,
    listenerCount: projection.listenerCount,
  });
}

declare global {
  var __g6Xss: boolean | undefined;
}

main().catch(async (error: unknown) => {
  await report({
    adapter: "angular",
    status: "failed",
    fixtures: [],
    error: error instanceof Error ? error.stack ?? error.message : String(error),
  });
});
