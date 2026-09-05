import { createApplicationBridgeContext } from "@runic-artifex/svelte";
import type {
  CounterCommand,
  CounterEvent,
  CounterReceipt,
  CounterSnapshot,
} from "./application.bridge.generated";

export const counterBridgeContext = createApplicationBridgeContext<
  CounterCommand,
  CounterReceipt,
  CounterEvent,
  CounterSnapshot
>();
