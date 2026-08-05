import { PROTOCOL_IDENTITY } from "@runic-artifex/mvvm";
import { createSvelteMvvmStore } from "@runic-artifex/mvvm-svelte";
import { mount, unmount } from "svelte";

import { G5Projection } from "./fake-projection.js";
import { assert, commandSubmissions, mandatoryFixtures, report } from "./report.js";
import View from "./svelte-browser.svelte";

const hostile = "<img src=x onerror=\"globalThis.__g5Xss = true\">";

async function main(): Promise<void> {
  assert(PROTOCOL_IDENTITY === "runic.toolkit.mvvm/1", "The G4 protocol identity changed.");
  const projection = new G5Projection();
  const store = createSvelteMvvmStore(projection);
  let isolatedDeliveries = 0;
  let throwingInitialDelivery = true;
  const unsubscribeThrowing = store.subscribe(() => {
    if (throwingInitialDelivery) {
      throwingInitialDelivery = false;
      return;
    }
    throw new Error("intentional subscriber isolation probe");
  });
  const unsubscribeCounting = store.subscribe(() => {
    isolatedDeliveries++;
  });

  const target = document.createElement("div");
  document.body.append(target);
  const component = mount(View, { target, props: { store } });
  await settle();
  assert(projection.listenerCount === 1, "Svelte must share exactly one projection subscription.");

  await store.setProperty(1, 7);
  const invocation = store.execute(2);
  const completion = await invocation.completion;
  await settle();
  const submissions = commandSubmissions(completion.value);
  assert(store.property(1) === 7, "Svelte did not publish the committed amount.");
  assert(store.snapshot.revision === 2n, "Svelte did not publish both commits atomically.");
  assert(submissions === 1, "Svelte command result was not preserved.");
  assert(isolatedDeliveries === 3, "Svelte subscriber failure suppressed a sibling subscriber.");

  projection.setHostileText(hostile);
  await settle();
  assert(document.querySelector("#hostile")?.textContent === hostile, "Svelte changed hostile text data.");
  assert(document.querySelector("#hostile img") === null, "Svelte interpreted hostile text as markup.");
  assert(globalThis.__g5Xss !== true, "Svelte hostile text executed script.");

  projection.replaceSnapshot(7, ["required"]);
  await settle();
  assert(document.querySelector("#validation")?.textContent === "required", "Svelte lost validation state.");
  projection.replaceSnapshot(11);
  await settle();
  assert(document.querySelector("#amount")?.textContent === "11", "Svelte lost replacement snapshot state.");

  await unmount(component);
  unsubscribeThrowing();
  unsubscribeCounting();
  await settle();
  assert(projection.listenerCount === 0, "Svelte unmount/unsubscribe leaked its projection subscription.");
  store.dispose();
  store.dispose();

  await report({
    adapter: "svelte",
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
    adapter: "svelte",
    status: "failed",
    fixtures: [],
    error: error instanceof Error ? error.stack ?? error.message : String(error),
  });
});
