import { PROTOCOL_IDENTITY } from "@webuitoolkit/mvvm";
import {
  provideVueMvvm,
  type VueMvvmAdapter,
} from "@webuitoolkit/mvvm-vue";
import { createApp, h, nextTick } from "vue";

import { G5Projection } from "./fake-projection.js";
import { assert, commandSubmissions, mandatoryFixtures, report } from "./report.js";

const hostile = "<img src=x onerror=\"globalThis.__g5Xss = true\">";

async function main(): Promise<void> {
  assert(PROTOCOL_IDENTITY === "webuitoolkit.mvvm/1", "The G4 protocol identity changed.");
  const projection = new G5Projection();
  let adapter: VueMvvmAdapter | undefined;
  let isolatedDeliveries = 0;
  const app = createApp({
    setup() {
      adapter = provideVueMvvm(projection);
      adapter.subscribe(() => {
        throw new Error("intentional subscriber isolation probe");
      });
      adapter.subscribe(() => {
        isolatedDeliveries++;
      });
      return () => h("main", { id: "g5-view" }, [
        h("span", { id: "amount" }, String(adapter!.state.value.properties.get(1))),
        h("span", { id: "hostile" }, String(adapter!.state.value.properties.get(3))),
        h("span", { id: "validation" }, (adapter!.state.value.validation.get(1) ?? []).join(",")),
      ]);
    },
  });
  const target = document.createElement("div");
  document.body.append(target);
  app.mount(target);
  await nextTick();
  assert(adapter !== undefined, "Vue did not create its scoped adapter.");
  assert(projection.listenerCount === 1, "Vue must own exactly one projection subscription.");

  await adapter.setProperty(1, 7);
  const invocation = adapter.execute(2);
  const completion = await invocation.completion;
  await nextTick();
  const submissions = commandSubmissions(completion.value);
  assert(adapter.property(1).value === 7, "Vue did not publish the committed amount.");
  assert(adapter.state.value.revision === 2n, "Vue did not publish both commits atomically.");
  assert(submissions === 1, "Vue command result was not preserved.");
  assert(isolatedDeliveries === 2, "Vue subscriber failure suppressed a sibling subscriber.");

  projection.setHostileText(hostile);
  await nextTick();
  assert(document.querySelector("#hostile")?.textContent === hostile, "Vue changed hostile text data.");
  assert(document.querySelector("#hostile img") === null, "Vue interpreted hostile text as markup.");
  assert(globalThis.__g5Xss !== true, "Vue hostile text executed script.");

  projection.replaceSnapshot(7, ["required"]);
  await nextTick();
  assert(document.querySelector("#validation")?.textContent === "required", "Vue lost validation state.");
  projection.replaceSnapshot(11);
  await nextTick();
  assert(document.querySelector("#amount")?.textContent === "11", "Vue lost replacement snapshot state.");

  app.unmount();
  await nextTick();
  assert(projection.listenerCount === 0, "Vue unmount leaked its projection subscription.");

  await report({
    adapter: "vue",
    status: "passed",
    fixtures: mandatoryFixtures,
    amount: 7,
    submissions,
    commits: 2,
    listenerCount: projection.listenerCount,
  });
}

declare global {
  var __g5Xss: boolean | undefined;
}

main().catch(async (error: unknown) => {
  await report({
    adapter: "vue",
    status: "failed",
    fixtures: [],
    error: error instanceof Error ? error.stack ?? error.message : String(error),
  });
});
