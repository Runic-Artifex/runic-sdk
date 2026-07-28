import { createInjectedMvvmDevelopmentTools } from "@webuitoolkit/mvvm";
import { CounterContract } from "./counter-contract.g";
import { bootstrapCounterApplication } from "./application";

const development = createInjectedMvvmDevelopmentTools(
  CounterContract.memberMetadata,
);
await bootstrapCounterApplication({
  contract: CounterContract,
  ...(development === undefined ? {} : { inspector: development.inspector }),
});
if (development !== undefined) {
  globalThis.addEventListener(
    "pagehide",
    () => development.dispose(),
    { once: true },
  );
}
