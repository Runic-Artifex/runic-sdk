export interface TodoApplicationLifetime {
  reconnect(): Promise<void>;
  addCleanup(cleanup: () => void | Promise<void>): () => void;
  dispose(): Promise<void>;
}

/** Exposes the one shared reconnect seam used by the native browser quality gate. */
export function exposeTodoReconnect(application: TodoApplicationLifetime): void {
  // Internal sample diagnostic used by the repository's native browser gate.
  // It remains deliberately outside the public package API and every
  // framework consumes this one shared hook.
  const diagnosticReconnect = () => application.reconnect();
  globalThis.__webuitoolkitTodoReconnect = diagnosticReconnect;
  application.addCleanup(() => {
    if (globalThis.__webuitoolkitTodoReconnect === diagnosticReconnect) {
      delete globalThis.__webuitoolkitTodoReconnect;
    }
  });
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
