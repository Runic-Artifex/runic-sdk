import type { BridgeErrorKind, BridgeFailureDetail } from "./errors.js";
import type { RunicBridgeClient } from "./transport.js";

/** A wire state as .NET serializes it, without `revision`. */
export type MockState = Record<string, unknown>;

/**
 * Answers one route suffix of a mock View. A returned object is merged into the
 * state, a boolean answers a `Can{Command}` query, and a string is the raw reply.
 * A thrown error becomes a failed reply with its type, message and stack as
 * `detail`; an error with a `kind` keeps that kind.
 */
export type MockRoute = (state: MockState, ...args: unknown[]) =>
  MockState | boolean | string | void | Promise<MockState | boolean | string | void>;

/**
 * When the mock delivers replies and pushed states. `immediate` (the default)
 * answers each call as soon as its handler returns and pushes synchronously.
 * `manual` queues calls and pushes until `flush()`, `flushUntil()` or `advance()`,
 * so a test decides exactly when the client observes each one.
 */
export type MockScheduling = "immediate" | "manual";

export interface MockBridgeOptions {
  readonly scheduling?: MockScheduling;
}

/** A failure the mock answers instead of running a handler. */
export interface MockFailure {
  /**
   * The BridgeError kind of the reply. `transport` makes the call itself reject,
   * like a dropped host connection; the client reports it as `failed`, or as
   * `disconnected` after `disconnect()`.
   */
  readonly kind: BridgeErrorKind | "transport";
  readonly message?: string;
  /** Development-only .NET detail, as `BridgeError.detail` exposes it. */
  readonly detail?: BridgeFailureDetail;
}

/** One keyed collection edit of a delta frame, as .NET writes it. */
export interface MockCollectionChange {
  readonly field: string;
  readonly kind: "add" | "remove" | "replace" | "move";
  readonly index: number;
  readonly oldIndex: number;
  readonly keys: readonly string[];
  readonly items: readonly unknown[];
}

/**
 * Edits one keyed collection of a mock View and pushes each edit as a delta
 * frame, like a .NET `[RunicCollection]`. Inside `MockView.batch` the edits
 * share one frame.
 */
export interface MockCollection<TItem = unknown> {
  readonly items: readonly TItem[];
  readonly keys: readonly string[];
  /** Inserts rows at `index`, by default at the end. */
  add(items: TItem | readonly TItem[], index?: number): void;
  remove(key: string): void;
  replace(key: string, item: TItem): void;
  move(key: string, index: number): void;
}

export type MockOperationKind = "running" | "succeeded" | "failed" | "cancelled";

/** An operation a client started with `start{Command}()`. */
export interface MockOperation<TInput = unknown, TResult = unknown> {
  readonly member: string;
  readonly requestId: string;
  readonly input: TInput;
  readonly kind: MockOperationKind;
  /** Aborts when the client cancels the operation or the wait times out. */
  readonly signal: AbortSignal;
  /** The stream values emitted so far. */
  readonly items: readonly TResult[];
  succeed(result?: TResult): void;
  fail(message?: string, detail?: BridgeFailureDetail): void;
  cancel(): void;
  /** Appends values to a stream operation. */
  emit(...items: TResult[]): void;
}

/** What an operation handler's completion means. */
export interface MockOperationOutcome {
  /** Merged into the state before the operation succeeds. */
  readonly state?: MockState;
  /** The wire result of the operation. */
  readonly result?: unknown;
}

/**
 * Runs a started operation. When the returned promise resolves, a still running
 * operation succeeds with its outcome; a throw fails it, or cancels it after the client
 * asked to cancel. Cancellation is cooperative, as in .NET: a cancel request aborts
 * `operation.signal`, and the operation keeps running until its handler stops.
 * `"manual"` leaves every operation running until the test settles it through
 * `MockView.operations()`.
 */
export type MockOperationHandler = (state: MockState, input: unknown, operation: MockOperation) =>
  MockOperationOutcome | void | Promise<MockOperationOutcome | void>;

export interface MockViewDefinition {
  readonly state: MockState;
  /** Handlers by route suffix, such as `Increment`, `SetStep` or `CanSave`. */
  readonly routes?: Readonly<Record<string, MockRoute>>;
  /** Row keys of each keyed collection field, which `collection(field)` edits with delta frames. */
  readonly collections?: Readonly<Record<string, (item: unknown) => string>>;
  /** Wire names of fields with checked writes (`Write{Property}` and `fieldBaseline`). */
  readonly checkedFields?: readonly string[];
  /**
   * Operation handlers by command member, such as `Save`. Without one, an
   * operation runs the command's route handler, if any, and succeeds.
   */
  readonly operations?: Readonly<Record<string, MockOperationHandler | "manual">>;
  /** Command members whose operations yield a stream. */
  readonly streams?: readonly string[];
  /**
   * The generated contract, `{ViewModel full name}:{fingerprint}`. With it, repeating
   * `Start{Command}` for a finished operation returns its terminal status, as .NET does.
   */
  readonly contract?: string;
}

/** How the browser answered an interaction request. */
export type MockInteractionReply =
  | { readonly kind: "answered"; readonly output: unknown }
  | { readonly kind: "cancelled" }
  | { readonly kind: "failed" }
  /** No mounted client handles the interaction, so .NET would use its fallback. */
  | { readonly kind: "unhandled" };

export interface MockInteractionOptions {
  /** Cancels the request, as .NET does when its interaction is cancelled. */
  readonly signal?: AbortSignal;
}

export interface MockView {
  /** The route prefix, such as `counter` or `content1`. */
  readonly route: string;
  /** The current wire state. */
  readonly state: MockState;
  /** The revision of the current state. */
  readonly revision: number;
  /** Merges a patch, advances the revision and pushes the state like a .NET publication. */
  update(patch: MockState | ((state: MockState) => MockState)): void;
  /** Pushes a raw frame to the client, such as a stale or malformed state. */
  push(frame: Record<string, unknown>): void;
  /**
   * Pushes a failure notice: .NET cannot publish this route for now, so the
   * client keeps its last state and reports the error.
   */
  pushFailure(failure: { readonly kind?: BridgeErrorKind; readonly message: string; readonly detail?: BridgeFailureDetail }): void;
  /** Edits a field declared in `collections`. */
  collection(field: string): MockCollection;
  /** Pushes the collection edits made by `edit` as one delta frame. */
  batch(edit: () => void): void;
  /** Operations started on this View, oldest first, optionally of one command member. */
  operations(member?: string): readonly MockOperation[];
  /** Interaction names that a mounted client currently handles. */
  readonly interactionHandlers: readonly string[];
  /** Sends an interaction request to the client's handler, as .NET does. */
  interact(name: string, input: unknown, options?: MockInteractionOptions): Promise<MockInteractionReply>;
}

export interface MockCall {
  readonly name: string;
  readonly args: readonly unknown[];
}

/** An in-memory `__runicBridge` that answers generated-client routes without .NET. */
export interface MockBridge extends Required<RunicBridgeClient> {
  /** Every call in order, including rejected ones. */
  readonly calls: readonly MockCall[];
  /** The virtual time in milliseconds, which only `advance()` moves. */
  readonly now: number;
  /** Queued replies, pushes and timers that are due now or later. */
  readonly pending: number;
  /**
   * Serves `{route}Snapshot`, `Mount`, `Unmount`, `Set{Property}`, `Write{Property}`,
   * `Can{Command}`, `Start{Command}` and the given routes.
   */
  view(route: string, definition: MockViewDefinition): MockView;
  /** Serves a whole route name, such as `__runicOperationStatus`. */
  route(name: string, handler: (...args: unknown[]) => string | Promise<string>): void;
  /** Answers the next `times` calls of a whole route name with `failure`. */
  failNext(name: string, failure: MockFailure, times?: number): void;
  /** Rejects further calls until `reconnect()`. */
  disconnect(): void;
  /** Restores calls and notifies reconnect listeners, as a host transport does after a reconnect. */
  reconnect(): void;
  /** Resolves after `milliseconds` of virtual time, for handlers that model latency. */
  sleep(milliseconds: number): Promise<void>;
  /** Delivers every queued reply and push that is due, until the client is idle. */
  flush(): Promise<void>;
  /** Flushes until `promise` settles, then returns its result. Fails when it cannot settle without time passing. */
  flushUntil<T>(promise: Promise<T>): Promise<T>;
  /** Moves the virtual clock, delivering timed work in order. */
  advance(milliseconds: number): Promise<void>;
}

/* ------------------------------------------------------------------ internals */

// Captured before a test can install fake timers, so flush() still yields.
const realSetTimeout = globalThis.setTimeout.bind(globalThis);
const yieldToEventLoop = () => new Promise<void>(resolve => { realSetTimeout(resolve, 0); });
const maximumRounds = 10_000;

const lowerFirst = (value: string) => value.charAt(0).toLowerCase() + value.slice(1);
const sameJson = (left: unknown, right: unknown) => JSON.stringify(left) === JSON.stringify(right);

function parseObject(text: unknown): Record<string, unknown> | undefined {
  if (typeof text !== "string") return undefined;
  try {
    const value = JSON.parse(text) as unknown;
    return value !== null && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
  } catch { return undefined; }
}

function failureFromError(error: unknown): { kind: string; message: string; detail?: BridgeFailureDetail } {
  const kind = typeof (error as { kind?: unknown })?.kind === "string" ? (error as { kind: string }).kind : "failed";
  // Like .NET in development, the reply carries the thrown error's detail.
  const detail = error instanceof Error
    ? { type: error.name, message: error.message, ...(typeof error.stack === "string" ? { stack: error.stack } : {}) }
    : undefined;
  return { kind, message: error instanceof Error ? error.message : String(error), ...(detail ? { detail } : {}) };
}

interface Deferred<T> { readonly promise: Promise<T>; resolve(value: T): void }
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

interface OperationRecord {
  readonly member: string;
  readonly requestId: string;
  readonly input: unknown;
  readonly inputJson: string;
  readonly view: MockRegisteredView;
  readonly stream: boolean;
  readonly controller: AbortController;
  readonly settled: Deferred<void>;
  readonly items: unknown[];
  kind: MockOperationKind;
  result?: unknown;
  error?: { readonly message: string; readonly detail?: BridgeFailureDetail };
  handle: MockOperation;
}

interface InteractionRequestRecord {
  readonly requestId: string;
  readonly name: string;
  readonly contract: string;
  readonly input: unknown;
  readonly reply: Deferred<MockInteractionReply>;
}

interface Presentation {
  readonly id: string;
  handlers: { readonly name: string; readonly contract: string }[];
  readonly queue: InteractionRequestRecord[];
  readonly cancellations: string[];
  wait?: Deferred<string> | undefined;
  controlWait?: Deferred<string> | undefined;
}

/** The mock's internal state of one View route. */
export interface MockRegisteredView {
  readonly route: string;
  readonly routes: Readonly<Record<string, MockRoute>>;
  readonly definition: MockViewDefinition;
  readonly fieldVersions: Map<string, number>;
  readonly operations: OperationRecord[];
  readonly presentations: Map<string, Presentation>;
  readonly requests: Map<string, InteractionRequestRecord>;
  /** Checked-write receipts by request id, for idempotent retries. */
  readonly writes: Map<string, { readonly payload: string; readonly receipt: Record<string, unknown> }>;
  /** Pushed frames the client has not received yet (manual scheduling). */
  readonly queued: Set<object>;
  pendingChanges: MockCollectionChange[] | undefined;
  baseRevision: number;
  state: MockState;
  revision: number;
  handle: MockView;
}

/** Internal hooks the generated typed mocks use. Not part of the public API. */
export interface MockBridgeInternals {
  readonly registered: (route: string) => MockRegisteredView | undefined;
  readonly commit: (view: MockRegisteredView, next: MockState) => void;
  readonly publish: (view: MockRegisteredView) => void;
  readonly writeChecked: (view: MockRegisteredView, field: string, payload: unknown,
    apply: (state: MockState, value: unknown) => MockState | void | Promise<MockState | void>) => Promise<string>;
  readonly envelope: (view: MockRegisteredView, error?: { kind: string; message: string; detail?: unknown }) => string;
}

const internals = new WeakMap<MockBridge, MockBridgeInternals>();
/** @internal Used by generated typed mocks. */
export function mockBridgeInternals(bridge: MockBridge): MockBridgeInternals {
  const value = internals.get(bridge);
  if (!value) throw new TypeError("The value is not a mock Bridge from createMockBridge().");
  return value;
}

export function createMockBridge(options: MockBridgeOptions = {}): MockBridge {
  const manual = options.scheduling === "manual";
  const views = new Map<string, MockRegisteredView>();
  const routes = new Map<string, (...args: unknown[]) => string | Promise<string>>();
  const failures = new Map<string, MockFailure[]>();
  const reconnectListeners = new Set<() => void>();
  const calls: MockCall[] = [];
  const tasks: { readonly due: number; readonly sequence: number; readonly run: () => void }[] = [];
  let connected = true;
  let revision = 0;
  let now = 0;
  let sequence = 0;
  let interactionSequence = 0;

  // Operations by request id. Like a .NET window, the ids are shared by every View.
  const operations = new Map<string, OperationRecord>();
  const schedule = (due: number, run: () => void) => {
    const task = { due, sequence: sequence++, run };
    tasks.push(task);
    return task;
  };
  function takeDue() {
    let best = -1;
    for (let index = 0; index < tasks.length; index++) {
      const task = tasks[index]!;
      if (task.due > now) continue;
      const current = best < 0 ? undefined : tasks[best]!;
      if (!current || task.due < current.due || task.due === current.due && task.sequence < current.sequence) best = index;
    }
    return best < 0 ? undefined : tasks.splice(best, 1)[0];
  }
  // Delivers to the client now, or at the next flush in manual mode.
  const deliver = (run: () => void) => { if (manual) schedule(now, run); else run(); };

  const wireState = (view: MockRegisteredView) => {
    // .NET writes the revision first.
    const state: MockState = { revision: view.revision, ...view.state };
    if (view.definition.checkedFields?.length)
      state["__runicFields"] = Object.fromEntries(view.definition.checkedFields.map(field => [field, { version: view.fieldVersions.get(field) ?? 0 }]));
    return state;
  };
  const envelope = (view: MockRegisteredView, error?: { kind: string; message: string; detail?: unknown }) => JSON.stringify({
    ok: error === undefined, state: wireState(view), error: error ?? null, protocol: 1,
  });
  function commit(view: MockRegisteredView, next: MockState): void {
    for (const field of view.definition.checkedFields ?? [])
      if (!sameJson(view.state[field], next[field])) view.fieldVersions.set(field, (view.fieldVersions.get(field) ?? 0) + 1);
    view.state = next;
    view.revision = ++revision;
    view.baseRevision = view.revision;
  }
  // Like .NET delivery, a full state or failure notice supersedes the frames of its
  // route that the client has not received yet (only manual scheduling queues them).
  function pushFrame(view: MockRegisteredView, frame: Record<string, unknown>, supersedes = false): void {
    const send = () => {
      const callback = (globalThis as unknown as Record<string, unknown>)[`__${view.route}Changed`];
      if (typeof callback === "function") callback(frame);
    };
    if (!manual) { send(); return; }
    if (supersedes) {
      for (let index = tasks.length - 1; index >= 0; index--) if (view.queued.has(tasks[index]!)) tasks.splice(index, 1);
      view.queued.clear();
    }
    const task: object = schedule(now, () => { view.queued.delete(task); send(); });
    view.queued.add(task);
  }
  const publish = (view: MockRegisteredView) => pushFrame(view, wireState(view), true);

  function emitChange(view: MockRegisteredView, change: MockCollectionChange): void {
    if (view.pendingChanges) { view.pendingChanges.push(change); return; }
    flushChanges(view, [change]);
  }
  function flushChanges(view: MockRegisteredView, changes: MockCollectionChange[]): void {
    if (changes.length === 0) return;
    // Like .NET, each change advances the revision, and more than 4,096 changes
    // are sent as a full state instead.
    const base = view.baseRevision;
    revision += changes.length;
    view.revision = view.baseRevision = revision;
    if (changes.length > 4096) { publish(view); return; }
    pushFrame(view, { __runicDelta: 1, baseRevision: base, revision: view.revision, changes });
  }

  function collectionOf(view: MockRegisteredView, field: string): MockCollection {
    const key = view.definition.collections?.[field];
    if (!key) throw new RangeError(`The mock View ${view.route} declares no collection ${field}.`);
    const rows = () => {
      const value = view.state[field];
      if (!Array.isArray(value)) throw new TypeError(`The mock View ${view.route} field ${field} is not an array.`);
      return value as unknown[];
    };
    const indexOf = (target: string) => {
      const at = rows().findIndex(item => key(item) === target);
      if (at < 0) throw new RangeError(`The mock collection ${field} has no row with key ${target}.`);
      return at;
    };
    // Edits change the state in place of a full publication; the frame carries them.
    const set = (next: unknown[]) => { view.state = { ...view.state, [field]: next }; };
    return {
      get items() { return rows(); },
      get keys() { return rows().map(key); },
      add(items, index) {
        const added = (Array.isArray(items) ? [...items] : [items]) as unknown[];
        if (added.length === 0) return;
        const next = [...rows()];
        const at = index ?? next.length;
        if (!Number.isSafeInteger(at) || at < 0 || at > next.length) throw new RangeError("The insert index is outside the collection.");
        next.splice(at, 0, ...added);
        const keys = next.map(key);
        if (new Set(keys).size !== keys.length) throw new RangeError(`The mock collection ${field} would contain a duplicate key.`);
        set(next);
        emitChange(view, { field, kind: "add", index: at, oldIndex: -1, keys: added.map(key), items: added });
      },
      remove(target) {
        const at = indexOf(target);
        const next = [...rows()];
        next.splice(at, 1);
        set(next);
        emitChange(view, { field, kind: "remove", index: at, oldIndex: at, keys: [target], items: [] });
      },
      replace(target, item) {
        const at = indexOf(target);
        if (key(item) !== target && rows().some(row => key(row) === key(item)))
          throw new RangeError(`The mock collection ${field} would contain a duplicate key.`);
        const next = [...rows()];
        next[at] = item;
        set(next);
        emitChange(view, { field, kind: "replace", index: at, oldIndex: at, keys: [target], items: [item] });
      },
      move(target, index) {
        const from = indexOf(target);
        const next = [...rows()];
        if (!Number.isSafeInteger(index) || index < 0 || index >= next.length) throw new RangeError("The move index is outside the collection.");
        const [moved] = next.splice(from, 1);
        next.splice(index, 0, moved);
        set(next);
        emitChange(view, { field, kind: "move", index, oldIndex: from, keys: [target], items: [] });
      },
    };
  }

  /* --------------------------------------------------------------- operations */

  const statusOf = (contract: string, operation: OperationRecord | undefined, requestId: string) => {
    if (!operation) return { contract, requestId, kind: "unknown" };
    return {
      contract, requestId, kind: operation.kind,
      ...(operation.kind === "failed" ? { error: { kind: "failed", message: operation.error?.message ?? "The operation failed.",
        ...(operation.error?.detail ? { detail: operation.error.detail } : {}) } } : {}),
      ...(operation.kind === "succeeded" && operation.result !== undefined ? { result: operation.result } : {}),
      ...(operation.kind === "succeeded" && operation.stream ? { stream: true } : {}),
    };
  };

  function settle(view: MockRegisteredView, operation: OperationRecord, kind: Exclude<MockOperationKind, "running">,
    result?: unknown, error?: OperationRecord["error"]): void {
    if (operation.kind !== "running") return;
    operation.kind = kind;
    if (result !== undefined) operation.result = result;
    if (error) operation.error = error;
    if (kind === "cancelled") operation.controller.abort();
    const executing = `is${operation.member}Executing`;
    if (Object.hasOwn(view.state, executing) && view.state[executing] === true &&
      !view.operations.some(other => other !== operation && other.member === operation.member && other.kind === "running")) {
      commit(view, { ...view.state, [executing]: false });
      publish(view);
    }
    operation.settled.resolve();
  }

  function startOperation(view: MockRegisteredView, member: string, payload: unknown): string {
    const parsed = typeof payload === "string" && payload.startsWith("{") ? parseObject(payload) : undefined;
    const requestId = parsed ? parsed["requestId"] : payload;
    if (typeof requestId !== "string" || requestId.length === 0)
      return JSON.stringify({ kind: "rejected", reason: `${member} has an invalid argument.`, terminal: null });
    const input = parsed ? parsed["input"] : undefined;
    const inputJson = JSON.stringify(input ?? null);
    const contract = view.definition.contract === undefined ? undefined : `${view.definition.contract}:${view.route}`;
    const existing = operations.get(requestId);
    if (existing) {
      // A request id names one operation: repeating it observes that operation,
      // and reusing it for other work is rejected.
      if (existing.view !== view || existing.member !== member || existing.inputJson !== inputJson)
        return JSON.stringify({ ...(contract ? { contract } : {}), requestId, kind: "rejected", status: "unknown",
          reason: "identity-conflict", terminal: null });
      return JSON.stringify({ ...(contract ? { contract } : {}), requestId, kind: "duplicate", status: existing.kind, reason: null,
        terminal: contract && existing.kind !== "running" ? statusOf(contract, existing, requestId) : null });
    }
    const handler = view.definition.operations?.[member];
    const operation: OperationRecord = {
      member, requestId, input, inputJson, view, stream: view.definition.streams?.includes(member) ?? false,
      controller: new AbortController(), settled: deferred<void>(), items: [], kind: "running",
      handle: undefined as unknown as MockOperation,
    };
    operation.handle = {
      get member() { return operation.member; },
      get requestId() { return operation.requestId; },
      get input() { return operation.input; },
      get kind() { return operation.kind; },
      get signal() { return operation.controller.signal; },
      get items() { return operation.items; },
      succeed: result => settle(view, operation, "succeeded", result),
      fail: (message, detail) => settle(view, operation, "failed", undefined, { message: message ?? "The operation failed.", ...(detail ? { detail } : {}) }),
      cancel: () => settle(view, operation, "cancelled"),
      emit: (...items) => {
        if (!operation.stream) throw new TypeError(`The operation ${member} is not a stream.`);
        if (operation.kind !== "running") throw new TypeError(`The operation ${member} has finished.`);
        operation.items.push(...items);
      },
    };
    view.operations.push(operation);
    operations.set(requestId, operation);
    const executing = `is${member}Executing`;
    if (Object.hasOwn(view.state, executing) && view.state[executing] !== true) { commit(view, { ...view.state, [executing]: true }); publish(view); }
    if (handler !== "manual") {
      const command = view.routes[member];
      void (async () => {
        try {
          let outcome: MockOperationOutcome | void = undefined;
          if (handler) outcome = await handler(view.state, operation.input, operation.handle);
          else if (command) {
            const patch = await command(view.state, ...(operation.input === undefined ? [] : [JSON.stringify(operation.input)]));
            outcome = patch !== null && typeof patch === "object" ? { state: patch } : undefined;
          }
          if (operation.kind !== "running") return;
          if (outcome?.state) { commit(view, { ...view.state, ...outcome.state }); publish(view); }
          settle(view, operation, "succeeded", outcome?.result);
        } catch (error) {
          const failure = failureFromError(error);
          // Cancellation is cooperative: a handler that stops by throwing after
          // the request ends cancelled, as a .NET command observing its token does.
          const cancelled = failure.kind === "cancelled" || operation.controller.signal.aborted;
          settle(view, operation, cancelled ? "cancelled" : "failed", undefined,
            { message: failure.message, ...(failure.detail ? { detail: failure.detail } : {}) });
        }
      })();
    }
    return JSON.stringify({ requestId, kind: "accepted", status: "running", reason: null, terminal: null });
  }

  // `{contract}:{route}` identifies the View; the route never contains a colon.
  function findOperation(identity: Record<string, unknown> | undefined) {
    const contract = typeof identity?.["contract"] === "string" ? identity["contract"] : "";
    const requestId = typeof identity?.["requestId"] === "string" ? identity["requestId"] : "";
    const member = typeof identity?.["member"] === "string" ? identity["member"] : undefined;
    const view = views.get(contract.slice(contract.lastIndexOf(":") + 1));
    const found = operations.get(requestId);
    const operation = found && found.view === view && (member === undefined || found.member === member) ? found : undefined;
    return { contract, requestId, view, operation };
  }

  async function operationRoute(name: string, payload: unknown): Promise<string | undefined> {
    const identity = parseObject(payload);
    switch (name) {
      case "__runicOperationStatus": {
        const { contract, requestId, operation } = findOperation(identity);
        return JSON.stringify(statusOf(contract, operation, requestId));
      }
      case "__runicOperationWait": {
        const { contract, requestId, operation } = findOperation(identity);
        if (operation) await operation.settled.promise;
        return JSON.stringify(statusOf(contract, operation, requestId));
      }
      case "__runicOperationCancel": {
        const { contract, requestId, view, operation } = findOperation(identity);
        if (!view || !operation) return JSON.stringify({ contract, requestId, kind: "unknown" });
        if (operation.kind !== "running") return JSON.stringify({ contract, requestId, kind: "not-running" });
        // The operation sees the request through its signal and decides how it ends.
        operation.controller.abort();
        return JSON.stringify({ contract, requestId, kind: "cancellation-requested" });
      }
      case "__runicOperationStream": {
        const { contract, requestId, operation } = findOperation(identity);
        const cursor = typeof identity?.["cursor"] === "number" ? identity["cursor"] : 0;
        if (!operation) return JSON.stringify({ contract, requestId, kind: "unknown" });
        return JSON.stringify({
          contract, requestId, kind: operation.kind, cursor: operation.items.length, completed: operation.kind !== "running",
          items: operation.items.map((value, index) => ({ sequence: index + 1, value })).filter(item => item.sequence > cursor),
        });
      }
      default: return undefined;
    }
  }

  /* ------------------------------------------------------------- interactions */

  function presentationOf(payload: unknown): { view?: MockRegisteredView; presentation?: Presentation; message: Record<string, unknown> | undefined } {
    const message = parseObject(payload);
    const view = typeof message?.["route"] === "string" ? views.get(message["route"]) : undefined;
    const id = message?.["presentationId"];
    return { message, ...(view ? { view } : {}),
      ...(view && typeof id === "string" && view.presentations.has(id) ? { presentation: view.presentations.get(id)! } : {}) };
  }

  function requestEnvelope(view: MockRegisteredView, presentation: Presentation, request: InteractionRequestRecord): string {
    return JSON.stringify({ kind: "request", requestId: request.requestId, route: view.route, presentationId: presentation.id,
      ownerEpoch: 0, name: request.name, contract: request.contract, input: request.input });
  }

  function endPresentation(view: MockRegisteredView, presentation: Presentation): void {
    view.presentations.delete(presentation.id);
    presentation.wait?.resolve(JSON.stringify({ kind: "disconnected" }));
    presentation.controlWait?.resolve(JSON.stringify({ kind: "disconnected" }));
    presentation.wait = presentation.controlWait = undefined;
    for (const request of presentation.queue.splice(0)) request.reply.resolve({ kind: "cancelled" });
  }

  async function interactionRoute(name: string, payload: unknown): Promise<string | undefined> {
    switch (name) {
      case "__runicInteractionControl": {
        const { view, message } = presentationOf(payload);
        const id = message?.["presentationId"];
        if (!view || typeof id !== "string") return JSON.stringify({ kind: "ignored" });
        const handlers = Array.isArray(message?.["handlers"]) ? (message["handlers"] as { name: string; contract: string }[]) : [];
        const presentation = view.presentations.get(id) ?? { id, handlers: [], queue: [], cancellations: [] };
        view.presentations.set(id, presentation);
        presentation.handlers = handlers.map(handler => ({ name: handler.name, contract: handler.contract }));
        if (presentation.handlers.length === 0) {
          presentation.wait?.resolve(JSON.stringify({ kind: "cancelled" }));
          presentation.wait = undefined;
        }
        return JSON.stringify({ kind: "ok" });
      }
      case "__runicInteractionWait": {
        const { view, presentation } = presentationOf(payload);
        if (!view || !presentation) return JSON.stringify({ kind: "disconnected" });
        const next = presentation.queue.shift();
        if (next) return requestEnvelope(view, presentation, next);
        presentation.wait = deferred<string>();
        return presentation.wait.promise;
      }
      case "__runicInteractionControlWait": {
        const { view, presentation } = presentationOf(payload);
        if (!view || !presentation) return JSON.stringify({ kind: "disconnected" });
        const cancelled = presentation.cancellations.shift();
        if (cancelled) return JSON.stringify({ kind: "cancelled", requestId: cancelled });
        presentation.controlWait = deferred<string>();
        return presentation.controlWait.promise;
      }
      case "__runicInteractionReply": {
        const { view, message } = presentationOf(payload);
        const requestId = message?.["requestId"];
        const request = view && typeof requestId === "string" ? view.requests.get(requestId) : undefined;
        if (view && request) {
          view.requests.delete(request.requestId);
          const kind = message?.["kind"];
          request.reply.resolve(kind === "answered" ? { kind, output: message?.["output"] }
            : kind === "failed" ? { kind: "failed" } : { kind: "cancelled" });
        }
        return JSON.stringify({ kind: "ok" });
      }
      default: return undefined;
    }
  }

  function interact(view: MockRegisteredView, name: string, input: unknown, options: MockInteractionOptions = {}): Promise<MockInteractionReply> {
    const presentation = [...view.presentations.values()].find(item => item.handlers.some(handler => handler.name === name));
    if (!presentation || options.signal?.aborted) return Promise.resolve({ kind: presentation ? "cancelled" : "unhandled" });
    const handler = presentation.handlers.find(item => item.name === name)!;
    const request: InteractionRequestRecord = {
      requestId: `interaction-${++interactionSequence}`, name, contract: handler.contract, input, reply: deferred<MockInteractionReply>(),
    };
    view.requests.set(request.requestId, request);
    options.signal?.addEventListener("abort", () => {
      if (!view.requests.has(request.requestId)) return;
      const queued = presentation.queue.indexOf(request);
      if (queued >= 0) {
        presentation.queue.splice(queued, 1);
        view.requests.delete(request.requestId);
        request.reply.resolve({ kind: "cancelled" });
        return;
      }
      if (presentation.controlWait) {
        const wait = presentation.controlWait;
        presentation.controlWait = undefined;
        wait.resolve(JSON.stringify({ kind: "cancelled", requestId: request.requestId }));
      } else presentation.cancellations.push(request.requestId);
    }, { once: true });
    if (presentation.wait) {
      const wait = presentation.wait;
      presentation.wait = undefined;
      wait.resolve(requestEnvelope(view, presentation, request));
    } else presentation.queue.push(request);
    return request.reply.promise;
  }

  /* ------------------------------------------------------------ view routes */

  async function writeChecked(view: MockRegisteredView, field: string, payload: unknown,
    apply: (state: MockState, value: unknown) => MockState | void | Promise<MockState | void>): Promise<string> {
    const request = parseObject(payload);
    if (!request || typeof request["requestId"] !== "string" || typeof request["expectedVersion"] !== "number")
      return envelope(view, { kind: "rejected", message: `${field} has an invalid checked write.` });
    const reply = (receipt: Record<string, unknown>) => JSON.stringify({ ok: true, state: wireState(view), error: null, receipt });
    // Like .NET, a retried request id returns its first receipt, and reusing it
    // for another write is a conflict.
    const key = `${field}\u0000${request["requestId"]}`;
    const canonical = JSON.stringify({ ...request, requestId: undefined });
    const previous = view.writes.get(key);
    const version = () => view.fieldVersions.get(field) ?? 0;
    if (previous) return reply(previous.payload === canonical ? previous.receipt
      : { kind: "conflict", incoming: { value: view.state[field], version: version() },
        message: "The request identity was already used with a different checked-write payload." });
    const retain = (receipt: Record<string, unknown>) => { view.writes.set(key, { payload: canonical, receipt }); return reply(receipt); };
    if (request["expectedVersion"] !== version() || !sameJson(request["expectedValue"], view.state[field]))
      return retain({ kind: "conflict", incoming: { value: view.state[field], version: version() }, message: "The field baseline no longer matches." });
    try {
      const patch = await apply(view.state, request["value"]);
      commit(view, { ...view.state, [field]: request["value"], ...(patch ?? {}) });
    } catch (error) {
      const failure = failureFromError(error);
      // A handler error with kind "committed-with-error" keeps the value, as a .NET
      // setter that ran before its validation failed does.
      if (failure.kind !== "committed-with-error") return retain({ kind: "rejected", message: failure.message });
      commit(view, { ...view.state, [field]: request["value"] });
      return retain({ kind: "committed-with-error", snapshot: { value: view.state[field], version: version() }, message: failure.message });
    }
    return retain({ kind: "applied", snapshot: { value: view.state[field], version: version() } });
  }

  async function answer(view: MockRegisteredView, suffix: string, args: unknown[]): Promise<string | undefined> {
    const handler = Object.hasOwn(view.routes, suffix) ? view.routes[suffix] : undefined;
    if (handler) {
      try {
        const result = await handler(view.state, ...args);
        if (typeof result === "boolean") return String(result);
        if (typeof result === "string") return result;
        if (result) commit(view, { ...view.state, ...result });
        return envelope(view);
      } catch (error) {
        return envelope(view, failureFromError(error));
      }
    }
    if (suffix === "Snapshot") return envelope(view);
    if (suffix === "Mount") {
      // The mount token identifies the presentation that interaction handlers belong to.
      if (typeof args[0] === "string" && !view.presentations.has(args[0]))
        view.presentations.set(args[0], { id: args[0], handlers: [], queue: [], cancellations: [] });
      return "ok";
    }
    if (suffix === "Unmount") {
      const presentation = typeof args[0] === "string" ? view.presentations.get(args[0]) : undefined;
      if (presentation) endPresentation(view, presentation);
      return "ok";
    }
    if (/^Set[A-Z]/.test(suffix)) { commit(view, { ...view.state, [lowerFirst(suffix.slice(3))]: args[0] }); return envelope(view); }
    if (/^Write[A-Z]/.test(suffix) && view.definition.checkedFields?.includes(lowerFirst(suffix.slice(5))))
      return writeChecked(view, lowerFirst(suffix.slice(5)), args[0], () => undefined);
    if (/^Can[A-Z]/.test(suffix)) return "true";
    if (/^Start[A-Z]/.test(suffix)) return startOperation(view, suffix.slice(5), args[0]);
    return undefined;
  }

  function injected(name: string, view: MockRegisteredView | undefined, failure: MockFailure): string {
    const message = failure.message ?? `The mock failed ${name}.`;
    if (failure.kind === "transport") throw new Error(message);
    const detail = failure.detail ? { detail: failure.detail } : {};
    if (!view) throw Object.assign(new Error(message), { kind: failure.kind });
    if (/^Start[A-Z]/.test(name.slice(view.route.length)))
      return JSON.stringify({ kind: failure.kind === "rejected" ? "rejected" : "failed", reason: message, terminal: null, ...detail });
    return envelope(view, { kind: failure.kind, message, ...detail });
  }

  const viewFor = (name: string) => {
    // The longest route prefix wins, so `counter` does not shadow `counterCompact`.
    let best: MockRegisteredView | undefined;
    for (const view of views.values())
      if (name.startsWith(view.route) && (!best || view.route.length > best.route.length)) best = view;
    return best;
  };

  async function compute(name: string, args: unknown[]): Promise<string> {
    const queued = failures.get(name);
    if (queued?.length) {
      const failure = queued.shift()!;
      if (queued.length === 0) failures.delete(name);
      return injected(name, viewFor(name), failure);
    }
    const raw = routes.get(name);
    if (raw) return await raw(...args);
    const builtIn = await operationRoute(name, args[0]) ?? await interactionRoute(name, args[0]);
    if (builtIn !== undefined) return builtIn;
    const view = viewFor(name);
    if (view) {
      const reply = await answer(view, name.slice(view.route.length), args);
      if (reply !== undefined) return reply;
      return envelope(view, { kind: "failed", message: `The mock View ${view.route} has no ${name.slice(view.route.length)} route.` });
    }
    throw new Error(`The mock Bridge has no route ${name}.`);
  }

  async function flush(): Promise<void> {
    for (let round = 0; round < maximumRounds; round++) {
      for (let task = takeDue(); task; task = takeDue()) task.run();
      await yieldToEventLoop();
      if (!tasks.some(task => task.due <= now)) return;
    }
    throw new Error("The mock Bridge did not become idle: the client keeps calling or pushing.");
  }

  const bridge: MockBridge = {
    calls,
    get now() { return now; },
    get pending() { return tasks.length; },
    isConnected: () => connected,
    call(name, ...args) {
      calls.push({ name, args });
      if (!connected) return Promise.reject(new Error("The mock Bridge is disconnected."));
      if (!manual) return compute(name, args);
      // Manual: the call runs at the next flush, and its reply is delivered at a flush after it completes.
      return new Promise<string>((resolve, reject) => {
        schedule(now, () => {
          compute(name, args).then(
            reply => schedule(now, () => resolve(reply)),
            error => schedule(now, () => reject(error)));
        });
      });
    },
    onReconnect(listener) {
      reconnectListeners.add(listener);
      return () => { reconnectListeners.delete(listener); };
    },
    view(route, definition) {
      const view: MockRegisteredView = {
        route, routes: definition.routes ?? {}, definition, fieldVersions: new Map(), operations: [], presentations: new Map(),
        requests: new Map(), writes: new Map(), queued: new Set(), pendingChanges: undefined, baseRevision: 0, state: { ...definition.state }, revision: 0,
        handle: undefined as unknown as MockView,
      };
      view.revision = view.baseRevision = ++revision;
      view.handle = {
        route,
        get state() { return view.state; },
        get revision() { return view.revision; },
        update(patch) {
          commit(view, typeof patch === "function" ? patch(view.state) : { ...view.state, ...patch });
          publish(view);
        },
        push(frame) { pushFrame(view, frame); },
        pushFailure(failure) {
          pushFrame(view, { __runicFailure: 1, revision: view.revision,
            error: { kind: failure.kind ?? "failed", message: failure.message, ...(failure.detail ? { detail: failure.detail } : {}) } }, true);
        },
        collection: field => collectionOf(view, field),
        batch(edit) {
          if (view.pendingChanges) { edit(); return; }
          view.pendingChanges = [];
          try { edit(); }
          finally {
            const changes = view.pendingChanges;
            view.pendingChanges = undefined;
            flushChanges(view, changes);
          }
        },
        operations: member => view.operations.filter(item => member === undefined || item.member === member).map(item => item.handle),
        get interactionHandlers() {
          return [...new Set([...view.presentations.values()].flatMap(item => item.handlers.map(handler => handler.name)))];
        },
        interact: (name, input, options) => interact(view, name, input, options),
      };
      views.set(route, view);
      return view.handle;
    },
    route(name, handler) { routes.set(name, handler); },
    failNext(name, failure, times = 1) {
      if (!Number.isSafeInteger(times) || times < 1) throw new RangeError("times must be a positive integer.");
      failures.set(name, [...(failures.get(name) ?? []), ...Array.from({ length: times }, () => failure)]);
    },
    disconnect() { connected = false; },
    reconnect() {
      connected = true;
      for (const listener of [...reconnectListeners]) listener();
    },
    sleep(milliseconds) {
      if (!Number.isFinite(milliseconds) || milliseconds < 0) throw new RangeError("The delay must be a non-negative number of milliseconds.");
      return new Promise<void>(resolve => schedule(now + milliseconds, resolve));
    },
    flush,
    async flushUntil<T>(promise: Promise<T>): Promise<T> {
      let settled = false;
      const observed = promise.then(value => { settled = true; return value; }, error => { settled = true; throw error; });
      void observed.catch(() => undefined);
      await flush();
      if (!settled) throw new Error("The promise did not settle after the mock Bridge became idle. Advance the virtual clock or settle the pending work.");
      return observed;
    },
    async advance(milliseconds) {
      if (!Number.isFinite(milliseconds) || milliseconds < 0) throw new RangeError("The delay must be a non-negative number of milliseconds.");
      const target = now + milliseconds;
      for (;;) {
        await flush();
        const next = tasks.reduce((earliest, task) => Math.min(earliest, task.due), Number.POSITIVE_INFINITY);
        if (next > target) break;
        now = Math.max(now, next);
      }
      now = target;
      await flush();
    },
  };
  internals.set(bridge, {
    registered: route => views.get(route),
    commit,
    publish,
    writeChecked,
    envelope,
  });
  return bridge;
}

/**
 * Installs a mock as `window.__runicBridge`, so generated clients connect to it
 * instead of waiting for a .NET host. Call it before the first `connect*()`.
 */
export function installMockBridge(bridge: MockBridge = createMockBridge()): MockBridge {
  (globalThis as unknown as { __runicBridge?: RunicBridgeClient }).__runicBridge = bridge;
  return bridge;
}

export {
  mockTypedView,
  type MockReference,
  type MockTypedCollection,
  type MockTypedInteraction,
  type MockTypedOperation,
  type MockTypedView,
  type MockTypedViewSpec,
  type MockTypedViewDefinition,
  type MockCommandHandler,
  type MockSetterHandler,
  type MockTypedOperationHandler,
  type MockTypedOperationOutcome,
  type MockMethodName,
} from "./mock-typed.js";
