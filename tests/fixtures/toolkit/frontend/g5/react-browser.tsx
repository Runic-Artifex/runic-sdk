import { PROTOCOL_IDENTITY } from "@webuitoolkit/mvvm";
import {
  ReactMvvmProvider,
  createReactMvvmStore,
  useMvvmSnapshot,
} from "@webuitoolkit/mvvm-react";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { G5Projection } from "./fake-projection.js";
import { assert, commandSubmissions, mandatoryFixtures, report } from "./report.js";

const hostile = "<img src=x onerror=\"globalThis.__g5Xss = true\">";

async function main(): Promise<void> {
  assert(PROTOCOL_IDENTITY === "webuitoolkit.mvvm/1", "The G4 protocol identity changed.");
  const projection = new G5Projection();
  const store = createReactMvvmStore(projection);
  let isolatedDeliveries = 0;
  store.subscribe(() => {
    throw new Error("intentional subscriber isolation probe");
  });
  store.subscribe(() => {
    isolatedDeliveries++;
  });

  function View() {
    const state = useMvvmSnapshot();
    return createElement(
      "main",
      { id: "g5-view" },
      createElement("span", { id: "amount" }, String(state.properties.get(1))),
      createElement("span", { id: "hostile" }, String(state.properties.get(3))),
      createElement("span", { id: "validation" }, (state.validation.get(1) ?? []).join(",")),
    );
  }

  const target = document.createElement("div");
  document.body.append(target);
  const root = createRoot(target);
  root.render(createElement(
    ReactMvvmProvider,
    { store, ownsStore: true },
    createElement(View),
  ));
  await settle();
  assert(projection.listenerCount === 1, "React must own exactly one projection subscription.");

  await store.setProperty(1, 7);
  const invocation = store.execute(2);
  const completion = await invocation.completion;
  await settle();
  const submissions = commandSubmissions(completion.value);
  assert(store.property(1) === 7, "React did not publish the committed amount.");
  assert(store.getSnapshot().revision === 2n, "React did not publish both commits atomically.");
  assert(submissions === 1, "React command result was not preserved.");
  assert(isolatedDeliveries === 2, "React subscriber failure suppressed a sibling subscriber.");

  projection.setHostileText(hostile);
  await settle();
  assert(document.querySelector("#hostile")?.textContent === hostile, "React changed hostile text data.");
  assert(document.querySelector("#hostile img") === null, "React interpreted hostile text as markup.");
  assert(globalThis.__g5Xss !== true, "React hostile text executed script.");

  projection.replaceSnapshot(7, ["required"]);
  await settle();
  assert(document.querySelector("#validation")?.textContent === "required", "React lost validation state.");
  projection.replaceSnapshot(11);
  await settle();
  assert(document.querySelector("#amount")?.textContent === "11", "React lost replacement snapshot state.");

  root.unmount();
  await settle();
  await settle();
  assert(projection.listenerCount === 0, "React unmount leaked its projection subscription.");

  await report({
    adapter: "react",
    status: "passed",
    fixtures: mandatoryFixtures,
    amount: 7,
    submissions,
    commits: 2,
    listenerCount: projection.listenerCount,
  });
}

function settle(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

declare global {
  var __g5Xss: boolean | undefined;
}

main().catch(async (error: unknown) => {
  await report({
    adapter: "react",
    status: "failed",
    fixtures: [],
    error: error instanceof Error ? error.stack ?? error.message : String(error),
  });
});
