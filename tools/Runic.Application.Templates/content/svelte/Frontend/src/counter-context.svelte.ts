import { createApplicationBridgeContext } from "@runic-artifex/svelte";
import type {
  CounterCommand,
  CounterEvent,
  CounterReceipt,
  CounterSnapshot,
} from "./counter-contract";

export const counterBridgeContext = createApplicationBridgeContext<
  CounterCommand,
  CounterReceipt,
  CounterEvent,
  CounterSnapshot
>();

