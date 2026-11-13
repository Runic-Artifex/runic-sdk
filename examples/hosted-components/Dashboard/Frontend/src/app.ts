import { defineHostedRegion } from "@runic-artifex/views/experimental";
import { connectDashboard } from "./generated/dashboard";

// <runic-hosted-region> shows a guest as a component when the host's policy decides so (EmbedComponent): it imports the
// guest's module from the guest's origin, creates the element the module defines and connects it with its token.
defineHostedRegion();

const dashboard = await connectDashboard();
const render = () => {
  const state = dashboard.snapshot;
  document.querySelector("#indexer-state")!.textContent = `(${state.indexer})`;
  document.querySelector("#sync-state")!.textContent = `(${state.sync})`;
  document.querySelector<HTMLOutputElement>("#scoped-registries")!.value = state.scopedRegistries;
};
dashboard.subscribe(render);
render();

// An engine check: scoped custom element registries (new CustomElementRegistry()) would let components keep plain tag
// names. Runic's component builds prefix their tags in every engine regardless, so this only reports what the engine has.
const scopedRegistries = (() => {
  try {
    const Registry = (globalThis as { CustomElementRegistry?: new () => unknown }).CustomElementRegistry;
    return typeof Registry === "function" && new Registry() !== undefined ? "supported" : "unsupported";
  } catch {
    return "unsupported";
  }
})();
await dashboard.reportEngine(scopedRegistries);
