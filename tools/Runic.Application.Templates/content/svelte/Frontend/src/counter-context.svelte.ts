import { createApplicationBridgeContext } from "@runic-artifex/svelte";
import type {
  CounterCommand,
  CounterEvent,
  CounterReceipt,
  CounterSnapshot,
} from "./application.bridge";

export const counterBridgeContext = createApplicationBridgeContext<
  CounterCommand,
  CounterReceipt,
  CounterEvent,
  CounterSnapshot
>();
