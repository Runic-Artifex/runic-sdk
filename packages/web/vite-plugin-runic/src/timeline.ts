// The development state that `runic()` and `runicDevSessionProxy()` keep in the development server process: the
// contract metadata, the page's connection state and a bounded timeline of sanitized diagnostics.
import type {
  RunicDiagnosticDetail,
  RunicDiagnosticEntry,
  RunicDiagnosticFailure,
  RunicDiagnosticSource,
} from "./diagnostics.js";
import { sanitizeDiagnosticSummary } from "./diagnostics.js";

const defaultTimelineLimit = 200;
const maximumTimelineLimit = 500;

export interface RunicDevelopmentState {
  readonly contract: Readonly<{
    identity: string;
    version: string;
    fingerprint: string;
  }>;
  readonly vite: Readonly<{
    command: string;
    mode: string;
    root: string;
  }>;
  /**
   * The page's connection to its .NET host, as `@runic-artifex/views` reports it
   * (`BridgeConnectionState`), or `unknown` until a View connects.
   */
  readonly connection: Readonly<{
    state: "connecting" | "connected" | "reconnecting" | "lost" | "closed" | "unknown";
  }>;
  readonly timeline: readonly RunicTimelineEntry[];
}

export type RunicTimelineEntry = Readonly<Required<Pick<RunicDiagnosticEntry, "id" | "timestamp" | "kind" | "label">> & {
  source?: RunicDiagnosticSource;
  detail: RunicDiagnosticDetail;
  failure?: RunicDiagnosticFailure;
}>;

export interface DevelopmentTimeline {
  readonly state: RunicDevelopmentState;
  /** Replaces the state without notifying, for configuration such as the resolved Vite command. */
  configure(update: (current: RunicDevelopmentState) => RunicDevelopmentState): void;
  /** Sanitizes and appends an entry; returns it, or `undefined` for a candidate that is not a diagnostic. */
  appendDiagnostic(candidate: unknown, defaultSource?: RunicDiagnosticSource): RunicTimelineEntry | undefined;
  /** Applies a `{ state }` connection report; returns whether the state changed. */
  applyConnection(candidate: unknown): boolean;
}

export function createDevelopmentTimeline(
  initial: RunicDevelopmentState,
  maxTimelineEntries: number,
  changed: (state: RunicDevelopmentState) => void,
): DevelopmentTimeline {
  let state = initial;
  let nextSequence = 1;
  return {
    get state() { return state; },
    configure(update) { state = update(state); },
    appendDiagnostic(candidate, defaultSource) {
      const summary = sanitizeDiagnosticSummary(candidate, defaultSource);
      if (!summary) return undefined;
      const entry: RunicTimelineEntry = {
        ...summary,
        id: `diagnostic-${nextSequence}`,
        timestamp: new Date().toISOString(),
      };
      nextSequence += 1;
      state = { ...state, timeline: [...state.timeline, entry].slice(-maxTimelineEntries) };
      changed(state);
      return entry;
    },
    applyConnection(candidate) {
      const next = connectionState(isRecord(candidate) ? candidate.state : undefined);
      if (next === state.connection.state) return false;
      state = { ...state, connection: { state: next } };
      changed(state);
      return true;
    },
  };
}

export function initialDevelopmentState(
  contract: Readonly<{ identity?: string; version?: string; fingerprint?: string }> | undefined,
  vite: RunicDevelopmentState["vite"] = { command: "serve", mode: "development", root: "" },
): RunicDevelopmentState {
  return {
    contract: {
      identity: cleanString(contract?.identity),
      version: cleanString(contract?.version),
      fingerprint: cleanString(contract?.fingerprint),
    },
    vite,
    connection: { state: "unknown" },
    timeline: [],
  };
}

// Failures keep file paths and stacks for local display only (the dock, the development server's terminal). The
// state endpoint and the copied state stay path-free.
export function withoutFailures(current: RunicDevelopmentState): RunicDevelopmentState {
  return {
    ...current,
    timeline: current.timeline.map(({ failure: _failure, ...entry }) => entry),
  };
}

export function timelineLimit(value: number | undefined): number {
  if (typeof value !== "number" || !Number.isFinite(value)) return defaultTimelineLimit;
  return Math.min(maximumTimelineLimit, Math.max(1, Math.floor(value)));
}

function connectionState(value: unknown): RunicDevelopmentState["connection"]["state"] {
  return value === "connecting" || value === "connected" || value === "reconnecting" || value === "lost" || value === "closed"
    ? value
    : "unknown";
}

function cleanString(value: unknown): string {
  return typeof value === "string" ? value.slice(0, 512) : "";
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
