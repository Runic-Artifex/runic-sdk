// Reading the messages of Views protocol 4 (specs/application/README.md, "Message catalogue"). The runtime parses
// each reply and push once here; members a message does not need are ignored, so producers may add members.
import { BridgeError, decodeFailureDetail, type BridgeErrorKind, type BridgeErrorReason } from "./errors.js";

export type Message = Readonly<Record<string, unknown>>;

/**
 * `presentation-epoch/1` (specs/application/README.md, "Presentation epochs"): `open` and `snapshot` replies and `state` pushes carry the
 * route's epoch, and a `command` may carry the epoch it was issued for; a stale one is `superseded`.
 */
export const presentationEpochFeature = "presentation-epoch/1";
/** `presented/1` (specs/application/README.md, "Presented states and window close"): `present` asks the page to report a painted revision; `closing` a close. */
export const presentedFeature = "presented/1";

/** A reply as the runtime reads it. Members beyond `ok`, `error`, `revision` and `released` depend on the request. */
export type Reply = Message & {
  readonly ok: boolean;
  readonly error?: ReplyError;
  readonly revision?: number;
  readonly released?: boolean;
};

export interface ReplyError {
  readonly kind: string;
  readonly message: string;
  readonly reason?: string;
  readonly failure?: unknown;
  readonly detail?: unknown;
}

/** A frame change as the runtime reads it; members depend on `kind`. */
export type Change = Message & { readonly field: string; readonly kind: string };

export type Push =
  | Message & { readonly kind: "state"; readonly route: string; readonly revision: number; readonly state: Message; readonly fields?: Message;
    /** presentation-epoch/1; a client without the feature ignores it. */
    readonly epoch?: unknown }
  | Message & { readonly kind: "frame"; readonly route: string; readonly baseRevision: number; readonly revision: number; readonly changes: readonly Change[] }
  | Message & { readonly kind: "failure"; readonly route: string; readonly revision: number; readonly error: ReplyError }
  | Message & { readonly kind: "operation"; readonly route: string; readonly member: string; readonly operation: string; readonly contract: string; readonly status: Message }
  | Message & { readonly kind: "interaction"; readonly route: string; readonly presentation: string; readonly requestId: string; readonly ownerEpoch: number;
    readonly name: string; readonly contract: string; readonly expiresAt: string; readonly input: unknown }
  | Message & { readonly kind: "interaction-cancelled"; readonly route: string; readonly presentation: string; readonly requestId: string;
    readonly ownerEpoch: number; readonly message?: string }
  | Message & { readonly kind: "menu-bar"; readonly route: string; readonly state: Message }
  /** presented/1: report `presented` once the page painted `route` at `revision` or later. */
  | Message & { readonly kind: "present"; readonly route: string; readonly revision: number }
  /** presented/1: the application closes the window; the only push without a route, for window-level handlers. */
  | Message & { readonly kind: "closing"; readonly route?: undefined };

/** The kinds of {@link Push}; a client ignores other kinds. */
export type PushKind = Push["kind"];

export type PushReading =
  | { readonly result: "valid"; readonly push: Push }
  | { readonly result: "ignored" }
  /** `route` and `kind` when the message named them, so a state or frame of an open route can recover. */
  | { readonly result: "invalid"; readonly route?: string; readonly kind?: string; readonly reason: string };

export const isObject = (value: unknown): value is Message =>
  value !== null && typeof value === "object" && !Array.isArray(value);
const isSafe = (value: unknown): value is number => typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
const isString = (value: unknown): value is string => typeof value === "string";
const has = (message: Message, member: string) => Object.hasOwn(message, member);

/** Parses one message: a JSON object. Returns undefined for anything else. */
export function parseMessage(text: unknown): Message | undefined {
  if (typeof text !== "string" || text.length === 0) return undefined;
  try {
    const value = JSON.parse(text) as unknown;
    return isObject(value) ? value : undefined;
  } catch { return undefined; }
}

function validFields(value: unknown): boolean {
  if (value === undefined) return true;
  if (!isObject(value)) return false;
  return Object.values(value).every(field => isObject(field) && isSafe(field["version"]));
}

const terminalKinds = new Set(["succeeded", "failed", "domain-failed", "cancelled"]);

/** Whether an operation status object is well formed (specs/application, "Status"). */
export function validStatus(status: unknown): status is Message & { readonly kind: string } {
  if (!isObject(status) || !isString(status["kind"])) return false;
  if (status["kind"] === "running")
    return status["progress"] === undefined || typeof status["progress"] === "number" && status["progress"] >= 0 && status["progress"] <= 1;
  if (terminalKinds.has(status["kind"])) return isSafe(status["revision"]);
  return true;
}

function validError(value: unknown): value is ReplyError {
  return isObject(value) && isString(value["kind"]) && isString(value["message"]) && (value["reason"] === undefined || isString(value["reason"]));
}

const replyMembers: Readonly<Record<string, (reply: Message) => boolean>> = {
  hello: reply => reply["protocol"] !== 4 || isObject(reply["implementation"]) && isString(reply["implementation"]["name"])
    && isString(reply["implementation"]["version"])
    && (reply["features"] === undefined || Array.isArray(reply["features"]) && reply["features"].every(isString)),
  open: reply => isSafe(reply["revision"]) && isObject(reply["state"]) && validFields(reply["fields"]) && isString(reply["presentation"]),
  snapshot: reply => isSafe(reply["revision"]) && isObject(reply["state"]) && validFields(reply["fields"]),
  close: () => true,
  set: reply => isSafe(reply["revision"]) && (reply["superseded"] === undefined || typeof reply["superseded"] === "boolean"),
  write: reply => isSafe(reply["revision"]) && isObject(reply["receipt"]),
  command: reply => isSafe(reply["revision"]) && (reply["superseded"] === undefined || typeof reply["superseded"] === "boolean"),
  can: reply => typeof reply["value"] === "boolean",
  start: reply => isSafe(reply["revision"]) && isString(reply["admission"])
    && (reply["admission"] !== "accepted" && reply["admission"] !== "duplicate" || validStatus(reply["status"]))
    && (reply["status"] === undefined || validStatus(reply["status"])),
  status: reply => validStatus(reply["status"]),
  watch: reply => validStatus(reply["status"]),
  unwatch: () => true,
  cancel: reply => isString(reply["cancellation"]),
  stream: reply => isObject(reply["page"]),
  interactions: reply => isString(reply["registration"]),
  "interaction-reply": reply => isString(reply["result"]),
  receive: reply => Array.isArray(reply["messages"]),
  "menu-bar-watch": reply => isObject(reply["state"]),
  "menu-bar-shortcuts": reply => isObject(reply["state"]),
  "menu-bar-invoke": reply => isString(reply["result"]),
  "menu-bar-unwatch": () => true,
  presented: () => true,
  "closing-reply": () => true,
};

/**
 * Reads the reply to a request of `kind`. Returns undefined for an invalid response: not a JSON object, a failure
 * without a well-formed `error`, or a success without the members its request kind defines.
 */
export function readReply(kind: string, text: unknown): Reply | undefined {
  const reply = parseMessage(text);
  if (!reply || typeof reply["ok"] !== "boolean") return undefined;
  if (reply["revision"] !== undefined && !isSafe(reply["revision"])) return undefined;
  if (reply["released"] !== undefined && typeof reply["released"] !== "boolean") return undefined;
  if (reply["ok"] === false) return validError(reply["error"]) ? reply as Reply : undefined;
  // A call that released its own route replies without the members of its kind.
  if (reply["released"] === true && kind !== "hello") return reply as Reply;
  const members = replyMembers[kind];
  return members === undefined || members(reply) ? reply as Reply : undefined;
}

const changeKinds = new Set(["set", "add", "remove", "replace", "move", "text", "splice"]);

/** Checks a frame's changes against the shape and ordering rules of state-changes.md; returns a reason or undefined. */
function invalidChanges(changes: unknown): string | undefined {
  if (!Array.isArray(changes) || changes.length === 0 || changes.length > 4096) return "The frame has no changes or more than 4,096.";
  const sets = new Set<string>();
  const others = new Set<string>();
  for (const change of changes as unknown[]) {
    if (!isObject(change) || !isString(change["field"]) || !isString(change["kind"])) return "A change is not an object with a field and a kind.";
    const field = change["field"];
    const kind = change["kind"];
    if (!changeKinds.has(kind)) return `The change kind ${kind} is unknown.`;
    if (kind === "set") {
      if (sets.has(field) || others.has(field)) return `The field ${field} has a set and another change.`;
      sets.add(field);
      if (!has(change, "value")) return `The set of ${field} has no value.`;
      if (change["version"] !== undefined && !isSafe(change["version"])) return `The set of ${field} has an invalid version.`;
      continue;
    }
    if (sets.has(field)) return `The field ${field} has a set and another change.`;
    others.add(field);
    if (kind === "text") {
      const { length, trim, text } = change as Record<string, unknown>;
      if (!isSafe(length) || !isSafe(trim) || trim > length || !isString(text)) return `The text change of ${field} is invalid.`;
      continue;
    }
    if (kind === "splice") {
      const { length, index, delete: removed, text, version } = change as Record<string, unknown>;
      if (!isSafe(length) || !isSafe(index) || !isSafe(removed) || index > length || removed > length - index || !isString(text)
        || version !== undefined && !isSafe(version)) return `The splice of ${field} is invalid.`;
      continue;
    }
    const { index, oldIndex, keys, items } = change as Record<string, unknown>;
    if (!isSafe(index) || !Array.isArray(keys) || !Array.isArray(items) || keys.length === 0
      || keys.some(key => !isString(key) || key.length === 0)) return `The ${kind} change of ${field} is invalid.`;
    if (kind === "add" ? oldIndex !== -1 : !isSafe(oldIndex)) return `The ${kind} change of ${field} has an invalid oldIndex.`;
    if ((kind === "remove" || kind === "replace") && oldIndex !== index) return `The ${kind} change of ${field} has an invalid oldIndex.`;
    if ((kind === "add" || kind === "replace") ? items.length !== keys.length : items.length !== 0)
      return `The ${kind} change of ${field} has items that do not match its keys.`;
  }
  return undefined;
}

const pushKinds = new Set(["state", "frame", "failure", "operation", "interaction", "interaction-cancelled", "menu-bar", "present", "closing"]);

/** The epoch of a reply or `state` push, when it carries one that is a safe integer (presentation-epoch/1). */
export function epochOf(message: Message): number | undefined {
  const epoch = message["epoch"];
  return isSafe(epoch) ? epoch : undefined;
}

const offsetDateTime = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/;

/** Reads one push (specs/application, "Publication", "Operations", "Interactions"). */
export function readPush(text: unknown): PushReading {
  const push = parseMessage(text);
  if (!push || !isString(push["kind"])) return { result: "invalid", reason: "The push is not a JSON object with a kind." };
  const kind = push["kind"];
  const route = isString(push["route"]) ? push["route"] : undefined;
  const invalid = (reason: string): PushReading => ({ result: "invalid", kind, ...(route === undefined ? {} : { route }), reason });
  if (!pushKinds.has(kind)) return { result: "ignored" };
  // `closing` is about the window: a route it names is ignored, like any member a client does not need.
  if (kind === "closing") return { result: "valid", push: push as Push };
  if (route === undefined) return invalid("The push has no route.");
  switch (kind) {
    case "state":
      if (!isSafe(push["revision"]) || !isObject(push["state"]) || !validFields(push["fields"])) return invalid("The state push is invalid.");
      break;
    case "frame": {
      if (!isSafe(push["baseRevision"]) || !isSafe(push["revision"])) return invalid("The frame's revisions are not safe integers.");
      const reason = invalidChanges(push["changes"]);
      if (reason !== undefined) return invalid(reason);
      break;
    }
    case "failure":
      if (!isSafe(push["revision"]) || !validError(push["error"])) return invalid("The failure push is invalid.");
      break;
    case "operation":
      if (!isString(push["member"]) || !isString(push["operation"]) || !isString(push["contract"]) || !validStatus(push["status"]))
        return invalid("The operation push is invalid.");
      break;
    case "interaction":
      if (!isString(push["presentation"]) || !isString(push["requestId"]) || !isSafe(push["ownerEpoch"]) || !isString(push["name"])
        || !isString(push["contract"]) || !isString(push["expiresAt"]) || !offsetDateTime.test(push["expiresAt"]) || !has(push, "input"))
        return invalid("The interaction push is invalid.");
      break;
    case "interaction-cancelled":
      if (!isString(push["presentation"]) || !isString(push["requestId"]) || !isSafe(push["ownerEpoch"])
        || push["message"] !== undefined && !isString(push["message"])) return invalid("The interaction cancellation is invalid.");
      break;
    case "menu-bar":
      if (!isObject(push["state"])) return invalid("The menu bar push is invalid.");
      break;
    case "present":
      if (!isSafe(push["revision"])) return invalid("The present push has no revision.");
      break;
  }
  return { result: "valid", push: push as Push };
}

// The reply error kinds a client accepts from the wire. `disconnected` is client-only (the Bridge is not
// connected) and a producer never sends it, so like any kind this client does not know it is `failed`.
const replyErrorKinds: readonly string[] = ["rejected", "cancelled", "failed", "released"] satisfies readonly BridgeErrorKind[];

/** The BridgeError of a failed reply. `domain-failed` is `failed` here; callers that declare a failure decode it first. */
export function replyError(error: ReplyError | undefined, route: string | undefined, fallback = "The call failed."): BridgeError {
  const detail = decodeFailureDetail(error?.detail);
  const kind = typeof error?.kind === "string" && replyErrorKinds.includes(error.kind) ? error.kind as BridgeErrorKind : "failed";
  // `reason` is an open vocabulary; an unknown reason is kept as sent.
  const reason = typeof error?.reason === "string" ? error.reason as BridgeErrorReason : undefined;
  const message = kind === "released" && typeof error?.message !== "string" ? "This View was released." : error?.message ?? fallback;
  return new BridgeError(kind, message, {
    ...(route === undefined ? {} : { route }), ...(detail === undefined ? {} : { detail }), ...(reason === undefined ? {} : { reason }),
  });
}

/** A reply that is not a message of the protocol. */
export function invalidResponse(route: string | undefined, cause?: unknown): BridgeError {
  return new BridgeError("failed", "The Bridge returned an invalid response.", {
    ...(route === undefined ? {} : { route }), ...(cause === undefined ? {} : { cause }),
  });
}
