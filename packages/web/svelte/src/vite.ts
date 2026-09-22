import {
  createRunicDiagnosticReporter,
  disposeRunicHmrResource,
  preserveRunicHmrResource,
  reportRunicState,
} from "@runic-artifex/vite-plugin-runic/client";
import type {
  RunicDiagnosticDetail,
  RunicDiagnosticDetailValue,
} from "@runic-artifex/vite-plugin-runic/client";
import type { ApplicationBridgeObserver } from "./types.js";

/** Connects a Svelte bridge projection to the optional Runic Vite client. */
export function createViteApplicationBridgeObserver(): ApplicationBridgeObserver {
  const reporter = createRunicDiagnosticReporter("application-bridge");
  return {
    state: (state) => reportRunicState(state),
    trace: (entry) => {
      const detail = boundedDetail(entry.detail);
      reporter.report({
        kind: entry.kind,
        label: entry.label,
        ...(detail === undefined ? {} : { detail }),
      });
    },
  };
}

function boundedDetail(detail: Readonly<Record<string, unknown>> | undefined): RunicDiagnosticDetail | undefined {
  if (detail === undefined) return undefined;
  const result: Record<string, RunicDiagnosticDetailValue> = {};
  let count = 0;
  for (const [key, value] of Object.entries(detail)) {
    if (count === 12) break;
    if (typeof value === "string" || typeof value === "number" || typeof value === "boolean" || value === null) {
      result[key] = value;
      count += 1;
    }
  }
  return count === 0 ? undefined : result;
}

/** Preserves one application-owned resource across a Vite hot replacement. */
export function preserveViteHmrResource<T>(key: string, create: () => T): T {
  return preserveRunicHmrResource(key, create);
}

/** Explicitly releases a resource preserved for Vite hot replacement. */
export function disposeViteHmrResource(
  key: string,
  dispose?: (resource: unknown) => void | Promise<void>,
): Promise<void> {
  return disposeRunicHmrResource(key, dispose);
}
