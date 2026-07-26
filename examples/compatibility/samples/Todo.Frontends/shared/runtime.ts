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
  /** Rebinds the retained session; used by the shared browser quality gate. */
  reconnect(): Promise<void>;
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
  let channel = new bridge.CsWebUiFrameChannel();
  const application = await startMvvmApplication({
    contract,
    channel,
  });

  let diagnosticReconnect: (() => Promise<void>) | undefined;
  const connection: TodoConnection = {
    projection: application.projection,
    async reconnect() {
      await channel.close("Todo reconnect quality gate");
      channel = new bridge.CsWebUiFrameChannel();
      await application.reconnect(channel);
    },
    async dispose() {
      if (globalThis.__webuitoolkitTodoReconnect === diagnosticReconnect) {
        delete globalThis.__webuitoolkitTodoReconnect;
      }
      await application.dispose("Todo frontend unloaded");
    },
  };
  // Internal sample diagnostic used by the repository's native browser gate.
  // It remains deliberately outside the public package API and every
  // framework consumes this one shared hook.
  diagnosticReconnect = () => connection.reconnect();
  globalThis.__webuitoolkitTodoReconnect = diagnosticReconnect;
  return connection;
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

declare global {
  // Installed only by these Todo samples so the repository's native browser
  // harness can drive one shared reconnect path across all four frameworks.
  // This is not part of the published WebUIToolkit package API.
  var __webuitoolkitTodoReconnect: (() => Promise<void>) | undefined;
}
