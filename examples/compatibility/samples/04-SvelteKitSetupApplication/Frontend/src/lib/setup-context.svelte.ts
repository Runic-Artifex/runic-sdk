import { createApplicationBridgeContext } from "@runic-artifex/svelte";
import type {
  SetupCommand,
  SetupEvent,
  SetupReceipt,
  SetupSnapshot,
} from "./setup-contract";

export const setupBridgeContext = createApplicationBridgeContext<
  SetupCommand,
  SetupReceipt,
  SetupEvent,
  SetupSnapshot
>();
