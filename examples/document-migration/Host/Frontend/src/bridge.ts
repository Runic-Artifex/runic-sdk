import { Effect, Result } from "effect";
import {
  ApplicationBridgeLive,
  createCsWebUiFrameChannel,
  createApplicationBridgeController,
} from "@runic-artifex/application-bridge";
import { createDesktopFrameChannel } from "@runic-artifex/desktop";
import contract, {
  type DocumentsCommand,
} from "./application.bridge.generated";
export const bridge = createApplicationBridgeController(
  contract,
  ApplicationBridgeLive(contract, "runicCsWebUi" in globalThis ? createCsWebUiFrameChannel() : createDesktopFrameChannel()),
);
export async function dispatch(command: DocumentsCommand) {
  const result = await bridge.run(
    Effect.result(bridge.effects.dispatch(command)),
  );
  if (Result.isFailure(result)) throw result.failure;
  return result.success;
}
