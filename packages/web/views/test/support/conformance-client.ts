// The client side of the Views protocol 4 conformance corpus (specs/application/conformance/README.md): the test Views
// as a generated client declares them, the scripted host bridge the client talks to, and the virtual clock. Shared by
// the in-repository client runner (client-conformance.test.ts) and the driver's adapter (conformance-adapter.ts).
import { defineCollection, defineCollections, defineInteractions, defineQueryCollection, defineRequests, defineText, decodeQueryCollectionState, regionCollection } from "../../dist/generated.js";
import * as wire from "../../dist/wire.js";
import { decodeBridgeValidation } from "../../dist/validation.js";
import { clock } from "../../dist/clock.js";

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

/* ------------------------------------------------------------------ test Views */

const int32 = (value: unknown) => wire.integer(value, -2147483648, 2147483647);
const row = (value: unknown) => wire.object(value, item => ({ id: int32(item["id"]), label: wire.string(item["label"]) }));
const rows = (value: unknown) => wire.array(value, row);
const reference = (value: unknown) => value === null ? null : wire.object(value, item => ({ kind: wire.string(item["kind"]), id: wire.string(item["id"]) }));
const rowCollections = defineCollections({ rows: defineCollection(row, item => String(item.id)) });
const failure = (value: unknown) => wire.union(value);

export interface ViewModel {
  readonly members: Record<string, (value: unknown) => unknown>;
  readonly checkedFields?: readonly string[];
  /** Text members (text-splice/1). */
  readonly texts?: readonly string[];
  readonly collections?: ReturnType<typeof defineCollections>;
  readonly interactions?: ReturnType<typeof defineInteractions>;
  /** View requests (view-requests/1). */
  readonly requests?: ReturnType<typeof defineRequests>;
  /** Commands that declare a failure. */
  readonly declared?: Readonly<Record<string, (value: unknown) => unknown>>;
  readonly operations?: Readonly<Record<string, { decode: (value: unknown) => unknown; decodeFailure?: (value: unknown) => unknown }>>;
  /** Scoped commands (presentation-epoch/1). */
  readonly scopedCommands?: readonly string[];
}

export const models: Record<string, ViewModel> = {
  list: { members: { rows, title: wire.string }, texts: ["title"], collections: rowCollections },
  log: { members: { log: value => value === null ? null : wire.string(value), title: wire.string }, texts: ["title"],
    collections: defineCollections({ log: defineText(true) }) },
  notes: {
    members: {
      title: wire.string, note: wire.string, priority: value => wire.enumName(value, ["Low", "Normal", "High"]),
      due: value => value === null ? null : wire.localDate(value), elapsed: wire.duration, rows, count: int32, summary: wire.string,
      detail: reference, canClear: wire.boolean, canTouch: wire.boolean, canSave: wire.boolean, canConfirmClear: wire.boolean,
      isSlowImportExecuting: wire.boolean, isConfirmClearExecuting: wire.boolean,
    },
    checkedFields: ["note"],
    texts: ["title", "note", "summary"],
    collections: rowCollections,
    interactions: defineInteractions({
      confirm: { contract: "Tests.Notes:confirm", decodeInput: value => value, encodeOutput: value => value },
    }),
    // The corpus gives the request contracts as provided ids; the client writes them first, in its registration, and
    // writes the value this runner gives a provided id that it writes first.
    requests: defineRequests({
      focus: { contract: "provided:focusContract", decodeInput: value => wire.object(value, item => ({ member: wire.string(item["member"]) })) },
      reveal: { contract: "provided:revealContract", decodeInput: value => wire.object(value, item => ({ id: int32(item["id"]) })) },
      toast: { contract: "provided:toastContract", decodeInput: value => wire.object(value, item => ({ text: wire.string(item["text"]) })) },
    }),
    declared: { save: failure, slowImport: failure },
    operations: { slowImport: { decode: int32, decodeFailure: failure } },
  },
  detail: { members: { lastAction: wire.string, canDismiss: wire.boolean }, texts: ["lastAction"] },
  form: { members: { name: wire.string, nameErrors: value => wire.array(value, wire.string), validation: decodeBridgeValidation }, texts: ["name"] },
  wizard: { members: { page: int32, canNext: wire.boolean, canBack: wire.boolean, canPing: wire.boolean }, scopedCommands: ["next", "back"] },
  invoice: {
    members: {
      address: value => wire.object(value, item => ({ street: wire.string(item["street"]), city: wire.string(item["city"]) })),
      lines: value => wire.array(value, line => wire.object(line, item => ({ id: int32(item["id"]), label: wire.string(item["label"]), quantity: int32(item["quantity"]) }))),
    },
    checkedFields: ["address"],
    collections: defineCollections({ lines: defineCollection(
      (value: unknown) => wire.object(value, item => ({ id: int32(item["id"]), label: wire.string(item["label"]), quantity: int32(item["quantity"]) })),
      item => String(item.id)) }),
  },
  // region-collections/1: the regions present page Views.
  tabs: { members: { documents: value => regionCollection(value, reference), pinned: value => regionCollection(value, reference) } },
  catalog: { members: { rows: decodeQueryCollectionState } },
  files: {
    members: {
      avatar: value => value === null ? null : wire.blob(value),
      lastUpload: value => value === null ? null : wire.string(value),
      attachments: value => wire.array(value, attachment),
    },
    collections: defineCollections({ attachments: defineCollection(attachment, item => String(item.id)) }),
  },
};
// A row of the files View's attachments: a blob in an editable row (runic-sdk#1391).
function attachment(value: unknown) {
  return wire.object(value, item => ({ id: int32(item["id"]), name: wire.string(item["name"]), preview: wire.blob(item["preview"]) }));
}
// The catalog's query collection (query-collections/1): one per route and member, created by the first query call.
export const catalogRows = defineQueryCollection(row);
export const modelOf = (route: string) => route.startsWith("content") ? models["detail"]! : models[route]!;
/** The View contract a route's generated client names. */
export const contractOf = (route: string) => route === "notes" ? "Tests.Notes:fp-1" : `Tests.${route}:fp-1`;

/* ------------------------------------------------------------------ the scripted host */

export interface Sent { message: Json; readonly text: string; resolve(text: string): void; reject(error: Error): void }
export interface Connection { readonly name: string; session?: string; readonly sent: Sent[] }

/** A host bridge with one connection at a time; `connect` starts a new one, as a reconnect does. */
export class ScriptedHost {
  connected = false;
  connection: Connection | undefined;
  readonly messageListeners = new Set<(message: string) => void>();
  readonly connectionListeners = new Set<(connected: boolean) => void>();
  readonly bridge = {
    isConnected: () => this.connected,
    request: (text: string) => new Promise<string>((resolve, reject) => {
      if (!this.connected || !this.connection) { reject(new Error("The scripted host is disconnected.")); return; }
      this.connection.sent.push({ message: JSON.parse(text) as Json, text, resolve, reject });
    }),
    onMessage: (listener: (message: string) => void) => { this.messageListeners.add(listener); return () => { this.messageListeners.delete(listener); }; },
    onConnectionChange: (listener: (connected: boolean) => void) => {
      this.connectionListeners.add(listener);
      listener(this.connected);
      return () => { this.connectionListeners.delete(listener); };
    },
  };

  connect(name: string): Connection {
    const connection: Connection = { name, sent: [] };
    this.connection = connection;
    this.connected = true;
    for (const listener of [...this.connectionListeners]) listener(true);
    return connection;
  }

  disconnect(): void {
    this.connected = false;
    for (const pending of this.connection?.sent ?? []) pending.reject(new Error("The connection closed."));
    for (const listener of [...this.connectionListeners]) listener(false);
  }

  push(text: string): void { for (const listener of [...this.messageListeners]) listener(text); }
}

/* ------------------------------------------------------------------ virtual time */

const timers = new Map<number, { due: number; callback: () => void }>();
let now = 0;
let timerId = 0;
const realClock = { set: clock.set, clear: clock.clear, now: clock.now };
export function useVirtualClock(): void {
  timers.clear();
  now = 0;
  clock.set = (callback, milliseconds) => { const id = ++timerId; timers.set(id, { due: now + milliseconds, callback }); return id; };
  clock.clear = handle => { timers.delete(handle as number); };
  // The page's wall clock: the corpus's default times are on 2026-10-28 around 12:00 UTC.
  clock.now = () => Date.parse("2026-10-28T12:00:00Z") + now;
}
export function useRealClock(): void { Object.assign(clock, realClock); }

/** Lets the client run the work it queued: a few turns of the event loop. */
export const idle = async () => { for (let turn = 0; turn < 5; turn++) await new Promise(resolve => setTimeout(resolve, 0)); };

/** Advances the virtual clock, running each timer that falls due in order. */
export async function advance(milliseconds: number): Promise<void> {
  const target = now + milliseconds;
  for (;;) {
    await idle();
    const next = [...timers.entries()].filter(([, timer]) => timer.due <= target).sort((a, b) => a[1].due - b[1].due || a[0] - b[0])[0];
    if (!next) break;
    timers.delete(next[0]);
    now = Math.max(now, next[1].due);
    next[1].callback();
  }
  now = target;
  await idle();
}

/* ------------------------------------------------------------------ value types */

const integerTypes: Record<string, { minimum: number; maximum: number }> = {
  int8: { minimum: -128, maximum: 127 },
  uint8: { minimum: 0, maximum: 255 },
  int16: { minimum: -32768, maximum: 32767 },
  uint16: { minimum: 0, maximum: 65535 },
  int32: { minimum: -2147483648, maximum: 2147483647 },
  uint32: { minimum: 0, maximum: 4294967295 },
};

export type ValueParameters = { names?: string[]; nullable?: boolean } | null | undefined;

/** Thrown for a wire value type the client has no decoder for. */
export class UnknownValueType extends Error {}

/**
 * Decodes a wire value of a value vector's type (vectors/values) with the generated client's decoders, giving its
 * normal form. An invalid value throws a TypeError or RangeError.
 */
export function decodeValue(type: string, parameters: ValueParameters, value: unknown): unknown {
  if (value === null && parameters?.nullable === true) return null;
  const range = integerTypes[type];
  if (range !== undefined) return wire.integer(value, range.minimum, range.maximum);
  switch (type) {
    case "boolean": return wire.boolean(value);
    case "string": return wire.string(value);
    case "float64": return wire.finiteNumber(value);
    case "float32": return wire.float32(value);
    case "int64": return wire.int64String(value);
    case "uint64": return wire.bigint(value, "0", "18446744073709551615").toString();
    case "big-integer": return wire.bigint(value).toString();
    case "decimal": return wire.decimal(value);
    case "uuid": return wire.uuid(value);
    case "local-date": return wire.localDate(value);
    case "local-time": return wire.localTime(value);
    case "date-time": return wire.dateTime(value);
    case "offset-date-time": return wire.offsetDateTime(value);
    case "duration": return wire.duration(value);
    case "enum": return wire.enumName(value, parameters?.names);
    case "json": return parameters?.nullable === false ? wire.json(value, false) : wire.json(value);
    default: throw new UnknownValueType(`No decoder for the wire value type ${type}.`);
  }
}
