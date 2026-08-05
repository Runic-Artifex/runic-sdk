import { bootstrapCounterApplication } from "./application";
import { createCounterMockChannel } from "./counter.mock";
import { CounterContract } from "./counter-contract.g";

await bootstrapCounterApplication({
  contract: CounterContract,
  channelFactory: createCounterMockChannel,
});
