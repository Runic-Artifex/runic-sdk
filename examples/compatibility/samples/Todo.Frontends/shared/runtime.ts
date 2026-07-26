import {
  startMvvmApplication,
  type MvvmProjection,
} from "@webuitoolkit/mvvm";

import {
  AdvancedTodoContract,
  SimpleTodoContract,
  type TodoDemo,
} from "./contracts";

export interface TodoConnection {
  readonly projection: MvvmProjection;
  dispose(): Promise<void>;
}

export async function connectTodo(demo: TodoDemo): Promise<TodoConnection> {
  const bridgeUrl = new URL(
    "../vendor/webuitoolkit-mvvm-cswebui.mjs",
    document.baseURI,
  ).href;
  const bridge = await import(bridgeUrl) as {
    CsWebUiFrameChannel: new () => import("@webuitoolkit/mvvm").FrameChannel;
    waitForCsWebUiBinding(): Promise<void>;
  };
  await bridge.waitForCsWebUiBinding();
  const contract = demo === "simple"
    ? SimpleTodoContract.contractName
    : AdvancedTodoContract.contractName;
  const application = await startMvvmApplication({
    contract,
    channel: new bridge.CsWebUiFrameChannel(),
  });

  return {
    projection: application.projection,
    dispose: () => application.dispose("Todo frontend unloaded"),
  };
}

export function reportStartupFailure(error: unknown): void {
  const root = document.querySelector("#app");
  if (root === null) return;
  root.innerHTML = "";
  const alert = document.createElement("div");
  alert.className = "alert alert-danger m-4";
  alert.textContent = error instanceof Error
    ? `The native MVVM session could not start: ${error.message}`
    : "The native MVVM session could not start.";
  root.append(alert);
}
