import { Effect, Either } from "effect";
import {
  ApplicationBridgeLive,
  createCsWebUiFrameChannel,
  createApplicationBridgeController,
} from "@runic-artifex/application-bridge";
import { createDesktopFrameChannel } from "@runic-artifex/desktop";
import contract, {
  type CustomersCommand,
} from "./application.bridge.generated";
export const bridge = createApplicationBridgeController(
  contract,
  ApplicationBridgeLive(contract, import.meta.env.VITE_RUNIC_HOST === "cswebui" ? createCsWebUiFrameChannel() : createDesktopFrameChannel()),
);
export async function dispatch(command: CustomersCommand) {
  const result = await bridge.run(
    Effect.either(bridge.effects.dispatch(command)),
  );
  if (Either.isLeft(result)) throw result.left;
  return result.right;
}
