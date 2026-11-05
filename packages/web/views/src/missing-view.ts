// A ViewOutlet whose registry has no component for a reference's kind: the framework bindings show an English alert in
// development and report the missing kind otherwise, once per kind and page.
import { reportBridgeError } from "./transport.js";

const reportedKey = Symbol.for("runic.views.missing-views");

/** The developer-facing text for a reference kind without a registered web component. */
export function missingViewMessage(kind: string): string {
  return `No web component is registered for ${kind}.`;
}

/**
 * Reports a reference kind without a registered web component through the page's error reporting
 * (`installErrorReporting`, `onBridgeDiagnostic`, the console) after the current task, once per kind and page.
 */
export function reportMissingView(kind: string): void {
  const host = globalThis as unknown as Record<symbol, Set<string> | undefined>;
  const reported = host[reportedKey] ??= new Set();
  if (reported.has(kind)) return;
  reported.add(kind);
  // Outside the framework's render.
  queueMicrotask(() => reportBridgeError(new Error(missingViewMessage(kind))));
}
