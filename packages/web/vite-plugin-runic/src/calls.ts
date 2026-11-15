// The dock's Bridge call timeline (#954). The development client sends each settled call of the Bridge-call channel
// of `@runic-artifex/views` (route, member, kind, timing, outcome, revision) to the development server, which keeps a
// bounded history of them next to the timeline. The server names each call's C# handler from the development source
// map (`sources.json`), such as `command EditorViewModel.save → SaveAsync() 23 ms`, and the dock opens it in the editor
// with the same decision as `/__runic/open` (sources.ts, `resolveOpenLocation`). A failed call keeps its .NET exception, whose frames in files of the map open the same way.
//
// What the page sends is sanitized like a timeline entry: closed shapes, short names, finite numbers, and the failure
// under the failure rules (bounded, credentials redacted). The handler comes from the server's own map; an entry names
// it by the map's member id, never by a path. The failure stays out of `/__runic/state` and the copied state.

import type { RunicDiagnosticFailure } from "./diagnostics.js";
import { sanitizeFailure } from "./diagnostics.js";
import type { RunicSourceMap, RunicSourceMember } from "./sources.js";

/** How a call ended, as `@runic-artifex/views` reports it (`BridgeCallOutcome`). */
export type RunicBridgeCallOutcome = "ok" | "error" | "cancelled";

/** A call's C# handler, from the development source map. */
export interface RunicBridgeCallHandler {
  /** The C# member that ran, such as `Demo.EditorViewModel.SaveAsync()`. */
  readonly symbol: string;
  /** The symbol below its ViewModel: `SaveAsync()`, `Title.set`. */
  readonly method: string;
  /** The map member's id, `{viewModel}/{kind}/{member}`, which `/__runic/open` opens. */
  readonly source: string;
  /** The source file's name and line, when the build had a PDB: `EditorViewModel.cs`, 42. */
  readonly fileName?: string;
  readonly line?: number;
}

/** One settled Bridge call in the dock's timeline. */
export interface RunicBridgeCall {
  readonly id: string;
  readonly timestamp: string;
  /** What the call did: `command`, `set`, `write`, `can`, `region`, `operation`, `query`, or a later kind. */
  readonly kind: string;
  readonly route: string;
  /** The C# ViewModel's type name, from the View contract: `EditorViewModel`. */
  readonly viewModel: string;
  /** The member's wire name: `save`. */
  readonly member: string;
  /** From the call to its settled reply, in milliseconds, as the page measured it. */
  readonly duration: number;
  readonly outcome: RunicBridgeCallOutcome;
  /** What refines the outcome, such as `superseded` or `domain-failed`. */
  readonly reason?: string;
  /** The reply's revision: the frames with this revision carry the call's effects. */
  readonly revision?: number;
  readonly handler?: RunicBridgeCallHandler;
  /**
   * The C# handler's own duration in milliseconds, as .NET measured it, when the reply carried it (a .NET host in
   * Development with handler timing). Absent otherwise.
   */
  readonly handlerDuration?: number;
  /** .NET's name for the handler from that reply, such as `EditorViewModel.Save`, when the map has none. */
  readonly handlerName?: string;
  /** The .NET exception of a failed call, for local display only; never in `/__runic/state`. */
  readonly failure?: RunicDiagnosticFailure;
  /** `command EditorViewModel.save → SaveAsync() 23 ms`. */
  readonly label: string;
}

/** What the development client sends for a call, before the server names its handler. */
export interface RunicBridgeCallReport {
  readonly page?: string;
  readonly kind: string;
  readonly route: string;
  readonly contract: string;
  readonly member: string;
  readonly duration: number;
  readonly outcome: RunicBridgeCallOutcome;
  readonly reason?: string;
  readonly revision?: number;
  readonly handlerDuration?: number;
  readonly handlerName?: string;
  readonly failure?: RunicDiagnosticFailure;
}

const namePattern = /^[A-Za-z][A-Za-z0-9_-]{0,31}$/u;
const routePattern = /^[A-Za-z0-9_.-]{1,128}$/u;
const memberPattern = /^[A-Za-z_$][A-Za-z0-9_$.-]{0,127}$/u;
// `{CLR type}:{fingerprint}`; a nested type has `+`, a generic one a backtick.
const contractPattern = /^[A-Za-z_][A-Za-z0-9_.+`]{0,255}(?::[A-Za-z0-9_-]{0,128})?$/u;
const reasonPattern = /^[A-Za-z][A-Za-z0-9_-]{0,47}$/u;
const handlerNamePattern = /^[A-Za-z_][A-Za-z0-9_.+`<>,]{0,159}(?:\([A-Za-z0-9_.+`<>, []*\))?$/u;
const pagePattern = /^[a-z0-9]{1,32}$/u;
const outcomes: ReadonlySet<string> = new Set(["ok", "error", "cancelled"]);
// An hour: anything longer is not a measurement of one call.
const maximumDuration = 3_600_000;

/** The report in a client message, or undefined when it is not one. */
export function sanitizeCallReport(candidate: unknown): RunicBridgeCallReport | undefined {
  if (typeof candidate !== "object" || candidate === null || Array.isArray(candidate)) return undefined;
  const value = candidate as Record<string, unknown>;
  const text = (name: string, pattern: RegExp): string | undefined =>
    typeof value[name] === "string" && pattern.test(value[name] as string) ? value[name] as string : undefined;
  const kind = text("kind", namePattern);
  const route = text("route", routePattern);
  const contract = text("contract", contractPattern);
  const member = text("member", memberPattern);
  const duration = milliseconds(value.duration);
  const outcome = typeof value.outcome === "string" && outcomes.has(value.outcome) ? value.outcome as RunicBridgeCallOutcome : undefined;
  if (!kind || !route || !contract || !member || duration === undefined || !outcome) return undefined;
  const page = text("page", pagePattern);
  const reason = text("reason", reasonPattern);
  const revision = typeof value.revision === "number" && Number.isSafeInteger(value.revision) && value.revision >= 0
    ? value.revision : undefined;
  const handlerDuration = milliseconds(value.handlerDuration);
  const handlerName = text("handlerName", handlerNamePattern);
  const failure = outcome === "error" ? sanitizeFailure(value.failure) : undefined;
  return {
    kind, route, contract, member, duration, outcome,
    ...(page === undefined ? {} : { page }),
    ...(reason === undefined ? {} : { reason }),
    ...(revision === undefined ? {} : { revision }),
    ...(handlerDuration === undefined ? {} : { handlerDuration }),
    ...(handlerName === undefined ? {} : { handlerName }),
    ...(failure === undefined ? {} : { failure }),
  };
}

function milliseconds(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) && value >= 0 && value <= maximumDuration
    ? Math.round(value * 10) / 10
    : undefined;
}

// The source map member kinds a call kind runs: a command its handler, a set or write the property's setter.
const memberKindsByCall: Readonly<Record<string, readonly RunicSourceMember["kind"][]>> = {
  command: ["command"],
  can: ["command", "commandState"],
  operation: ["command"],
  set: ["setter", "state"],
  write: ["setter", "state"],
};

/** The map member a call ran, when the map has the call's ViewModel. */
export function findCallMember(map: RunicSourceMap | undefined, report: Pick<RunicBridgeCallReport, "contract" | "route" | "member" | "kind">):
  RunicSourceMember | undefined {
  if (map === undefined) return undefined;
  const type = report.contract.split(":", 1)[0];
  // The same contract, else the same C# type of an older build (another fingerprint).
  const viewModel = map.viewModels.find((entry) => entry.contract === report.contract)
    ?? map.viewModels.find((entry) => entry.id === type);
  if (viewModel === undefined) return undefined;
  const members = map.members.filter((entry) => entry.viewModel === viewModel.id && entry.member === report.member);
  for (const kind of memberKindsByCall[report.kind] ?? []) {
    const member = members.find((entry) => entry.kind === kind);
    if (member !== undefined) return member;
  }
  return members[0];
}

/** The handler of a map member, as the timeline shows it. */
export function callHandler(member: RunicSourceMember): RunicBridgeCallHandler {
  const prefix = `${member.viewModel}.`;
  const method = member.symbol.startsWith(prefix) ? member.symbol.slice(prefix.length) : member.symbol;
  const fileName = member.location?.file.split(/[\\/]/u).pop();
  return {
    symbol: member.symbol,
    method,
    source: member.id,
    ...(fileName ? { fileName } : {}),
    ...(member.location ? { line: member.location.line } : {}),
  };
}

/** `command EditorViewModel.save → SaveAsync() 23 ms`, with the outcome when it is not `ok`. */
export function callLabel(call: Omit<RunicBridgeCall, "id" | "timestamp" | "label">): string {
  const handler = call.handler?.method ?? call.handlerName;
  const outcome = call.outcome === "ok" ? "" : ` · ${call.outcome}${call.reason === undefined ? "" : ` (${call.reason})`}`;
  return `${call.kind} ${call.viewModel}.${call.member}${handler === undefined ? "" : ` → ${handler}`} ${formatMilliseconds(call.duration)}${outcome}`;
}

export function formatMilliseconds(value: number): string {
  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ms`;
}

/** A call's entry: its report with the handler the map names. */
export function callEntry(report: RunicBridgeCallReport, map: RunicSourceMap | undefined, id: string, timestamp: string): RunicBridgeCall {
  const member = findCallMember(map, report);
  // `Demo.EditorViewModel:3F2A…` names the C# ViewModel `EditorViewModel`.
  const viewModel = report.contract.split(":", 1)[0]!.split(/[.+]/u).pop() || report.route;
  const call = {
    kind: report.kind,
    route: report.route,
    viewModel,
    member: report.member,
    duration: report.duration,
    outcome: report.outcome,
    ...(report.reason === undefined ? {} : { reason: report.reason }),
    ...(report.revision === undefined ? {} : { revision: report.revision }),
    ...(member === undefined ? {} : { handler: callHandler(member) }),
    ...(report.handlerDuration === undefined ? {} : { handlerDuration: report.handlerDuration }),
    ...(report.handlerName === undefined || member !== undefined ? {} : { handlerName: report.handlerName }),
    ...(report.failure === undefined ? {} : { failure: report.failure }),
  };
  return { id, timestamp, ...call, label: callLabel(call) };
}

/** A frame of a .NET stack trace with a source location: `   at Demo.EditorViewModel.SaveAsync() in /src/EditorViewModel.cs:line 42`. */
export interface RunicStackFrame {
  /** The frame's text, trimmed. */
  readonly text: string;
  readonly method: string;
  readonly file: string;
  readonly line: number;
}

const framePattern = /^\s*at (.+?) in (.+):line (\d{1,9})\s*$/u;

/** The frames of a .NET stack that name a source file and line, in order, at most `limit`. */
export function stackFrames(stack: string | undefined, limit = 12): RunicStackFrame[] {
  const frames: RunicStackFrame[] = [];
  for (const line of stack?.split("\n") ?? []) {
    const match = framePattern.exec(line);
    if (match === null) continue;
    frames.push({ text: line.trim(), method: match[1]!, file: match[2]!, line: Number(match[3]) });
    if (frames.length >= limit) break;
  }
  return frames;
}
