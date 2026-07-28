import { CounterContract } from "./counter-contract.g";
import { bootstrapCounterApplication } from "./application";

await bootstrapCounterApplication({ contract: CounterContract });
