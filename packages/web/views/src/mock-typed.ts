import type { BridgeErrorKind, BridgeFailureDetail } from "./errors.js";
import type { BridgeOutcomeFailure } from "./controller.js";
import {
  mockBridgeInternals,
  mockFailure,
  mockFailureOf,
  type MockBridge,
  type MockCall,
  type MockCommand,
  type MockDomainFailure,
  type MockErrorFailure,
  type MockFailure,
  type MockInteractionOptions,
  type MockOperation,
  type MockOperationHandler,
  type MockOperationKind,
  type MockSetter,
  type MockState,
  type MockView,
} from "./mock-bridge.js";

/** The wire form of a content reference in a mock state, such as `{ kind: "editor", id: "1" }`. */
export interface MockReference<TKind extends string = string> {
  readonly kind: TKind;
  readonly id: string;
}

type Patch<TState> = Partial<{ -readonly [K in keyof TState]: TState[K] }>;
type Awaitable<T> = T | Promise<T>;

/**
 * Runs a command and returns the state changes it makes. A thrown error becomes the
 * client's BridgeError; `throw mockFailure(failure)` resolves the client's outcome
 * with the declared failure.
 */
export type MockCommandHandler<TState, TArgs extends readonly unknown[] = []> =
  (state: TState, ...args: TArgs) => Awaitable<Patch<TState> | void>;

/**
 * Runs before a setter or checked write applies `value`. Return more state
 * changes, such as a dirty flag; throw to reject the value.
 */
export type MockSetterHandler<TState, TValue> = (state: TState, value: TValue) => Awaitable<Patch<TState> | void>;

export interface MockTypedOperationOutcome<TState, TResult> {
  readonly state?: Patch<TState>;
  readonly result?: TResult;
}

/** An operation that a client started with `start{Command}()`, with typed input and results. */
export interface MockTypedOperation<TInput, TResult, TFailure = never> {
  readonly requestId: string;
  readonly input: TInput;
  readonly kind: MockOperationKind;
  readonly signal: AbortSignal;
  /** Stream values emitted so far. */
  readonly items: readonly TResult[];
  /** The latest progress the operation reported, from 0 to 1, or undefined. */
  readonly progress: number | undefined;
  /** Reports progress, clamped to 0 to 1; watching clients receive it as `running` with `progress`. */
  reportProgress(value: number): void;
  succeed(...result: [TResult] extends [void] ? [] : [result: TResult]): void;
  fail(message?: string, detail?: BridgeFailureDetail): void;
  /** Ends the operation with its declared failure (`domain-failed`). */
  failWith(failure: TFailure): void;
  cancel(): void;
  /** Drops the finished operation's outcome, so it reports `expired`; throws while it runs. */
  expire(): void;
  /** Forgets the request id, as a restarted window has, so it reports `unknown`. */
  forget(): void;
  emit(...items: TResult[]): void;
}

/**
 * Runs a started operation. When it returns, a still running operation succeeds
 * with the outcome; a throw fails it, and `throw mockFailure(failure)` ends it
 * with its declared failure. Return a promise that waits on the operation's
 * signal, or use `"manual"`, to settle it from the test.
 */
export type MockTypedOperationHandler<TState, TInput, TResult, TFailure = never> =
  (state: TState, input: TInput, operation: MockTypedOperation<TInput, TResult, TFailure>) =>
    Awaitable<MockTypedOperationOutcome<TState, TResult> | void>;

/**
 * The declared failure type of a client method: `F` for a command that resolves
 * `BridgeOutcome<_, F>` or a `start{Command}` whose operation declares `F`.
 */
export type MockFailureOf<TMethod> = TMethod extends (...args: never[]) => Promise<infer R>
  ? R extends { outcome(...args: never[]): Promise<infer O> } ? BridgeOutcomeFailure<O> : BridgeOutcomeFailure<R>
  : never;

/** A keyed collection of a typed mock. Each edit pushes a frame. */
export interface MockTypedCollection<TItem> {
  readonly items: readonly TItem[];
  readonly keys: readonly string[];
  add(items: TItem | readonly TItem[], index?: number): void;
  remove(key: string): void;
  replace(key: string, item: TItem): void;
  move(key: string, index: number): void;
}

/** An interaction that the producer asks the browser to handle. */
export interface MockTypedInteraction<TInput, TOutput> {
  /** Whether an open client presentation currently handles it. */
  readonly handled: boolean;
  /** Sends a request to the client's handler and resolves with its reply. */
  request(input: TInput, options?: MockInteractionOptions): Promise<
    | { readonly kind: "answered"; readonly output: TOutput }
    | { readonly kind: "cancelled" | "failed" | "unhandled" }>;
}

/** A client method name a failure can target, such as `save`, `startSave`, `setTitle` or `canSetViewport`. */
export type MockMethodName<TClient> = Exclude<keyof TClient & string,
  "snapshot" | "subscribe" | "dispose" | "interactions" | "fieldBaseline" | `recover${string}`>;

/** The typed mock of one generated client, created by a generated `mock{Name}()`. */
export interface MockTypedView<TState, TClient, TKind extends string = string> {
  /** The route, such as `editor` for a root View or `content1` for content. */
  readonly route: string;
  /** Put this in a parent mock's state to present this View as content. */
  readonly reference: MockReference<TKind>;
  readonly state: TState;
  readonly revision: number;
  /** The untyped mock View, for raw frames. */
  readonly raw: MockView;
  /** Requests to this View's route, in order. */
  readonly calls: readonly MockCall[];
  /** Merges state changes and publishes them, like a producer: one frame with the members that changed. */
  update(patch: Patch<TState> | ((state: TState) => Patch<TState>)): void;
  /**
   * Answers the next `times` calls of a client method with `failure`: an unexpected
   * failure, or the method's declared failure as `{ kind: "domain-failed", failure }`.
   */
  failNext<M extends MockMethodName<TClient>>(method: M,
    failure: MockErrorFailure | MockDomainFailure<MockFailureOf<TClient[M]>>, times?: number): void;
  /** Pushes a failure notice; the client keeps its last state and reports the error. */
  pushFailure(failure: { readonly kind?: BridgeErrorKind; readonly message: string; readonly detail?: BridgeFailureDetail }): void;
  /** Publishes the collection edits and updates made by `edit` as one frame. */
  batch(edit: () => void): void;
}

interface FieldCodec { encode(value: unknown): unknown; decode(wire: unknown): unknown }

/** What a generated `mock{Name}()` knows about its client. Generated code is its only intended caller. */
export interface MockTypedViewSpec {
  readonly kind: string;
  /** The root route. */
  readonly route: string;
  /** Codecs of value fields by wire name. Other fields, such as content references, are stored as given. */
  readonly fields: Readonly<Record<string, FieldCodec>>;
  /** State the client always receives, such as `canSave: true`. */
  readonly defaults: MockState;
  readonly checkedFields?: readonly string[];
  /** Keyed collections by wire name: a row codec and its key. */
  readonly collections?: Readonly<Record<string, FieldCodec & { key(item: never): string }>>;
  /**
   * Setters by client method (`setTitle`): the member's wire name, and the client method of its checked write
   * (`writeTitle`) when it has one.
   */
  readonly setters?: Readonly<Record<string, { readonly member: string; readonly checked?: string }>>;
  /**
   * Commands by client method (`save`): the member's wire name, `decodeInput` for a command with an input,
   * `available`, the state member of its availability, and `encodeFailure` for a declared failure. A command with an
   * input also has the client method `can{Method}`.
   */
  readonly commands?: Readonly<Record<string, { readonly member: string; readonly decodeInput?: (wire: unknown) => unknown;
    readonly available?: string; readonly encodeFailure?: (value: unknown) => unknown }>>;
  /** Operations by client command method (`start{Method}`). `encodeFailure` encodes a declared failure. */
  readonly operations?: Readonly<Record<string, { readonly member: string; readonly decodeInput?: (wire: unknown) => unknown;
    readonly encodeResult?: (value: unknown) => unknown; readonly stream?: boolean;
    readonly encodeFailure?: (value: unknown) => unknown }>>;
  /** The View's operation contract, `{view}:{fingerprint}`. */
  readonly contract?: string;
  readonly interactions?: Readonly<Record<string, { readonly encodeInput: (value: unknown) => unknown; readonly decodeOutput: (wire: unknown) => unknown }>>;
}

/** The loose shape of a generated mock definition. */
export interface MockTypedViewDefinition {
  /** Presents the mock as content with this id, on route `content{id}`. */
  readonly id?: string;
  readonly state: object;
  /** The page kind of the reference, for a View with several contracts. */
  readonly kind?: string;
  /** Handlers by client method; the generated definition types them. */
  readonly setters?: object;
  readonly commands?: object;
  readonly canExecute?: object;
  /**
   * Key resolution of key commands by client method: whether the producer would find an object for the
   * key. Absent, every key resolves.
   */
  readonly resolveKey?: object;
  readonly operations?: object;
}

const upperFirst = (value: string) => value.charAt(0).toUpperCase() + value.slice(1);

function rejected(message: string, reason: "unavailable" | "unresolved-key"): Error {
  return Object.assign(new Error(message), { kind: "rejected", reason });
}

// A typed handler throws mockFailure(typed); the untyped mock sends the wire value.
function encodedFailure(error: unknown, encode: ((value: unknown) => unknown) | undefined): unknown {
  const declared = mockFailureOf(error);
  return declared && encode ? mockFailure(encode(declared.failure), (error as Error).message) : error;
}

/**
 * Registers a typed mock View. Generated `mock{Name}()` helpers call this with
 * their client's codecs; applications call the generated helper.
 */
export function mockTypedView(bridge: MockBridge, spec: MockTypedViewSpec, definition: MockTypedViewDefinition): unknown {
  const route = definition.id === undefined ? spec.route : `content${definition.id}`;
  if (definition.id !== undefined && !/^[A-Za-z0-9_-]+$/.test(definition.id))
    throw new RangeError("A mock content id must contain only letters, digits, '_' and '-'.");
  const fields = spec.fields;
  const encode = (state: object): MockState => {
    const wire: MockState = {};
    for (const [field, value] of Object.entries(state))
      wire[field] = value !== undefined && Object.hasOwn(fields, field) ? fields[field]!.encode(value) : value;
    return wire;
  };
  const decode = (wire: MockState): never => {
    const state: MockState = {};
    for (const [field, value] of Object.entries(wire))
      state[field] = Object.hasOwn(fields, field) ? fields[field]!.decode(value) : value;
    return state as never;
  };
  const patchOf = (patch: unknown) => patch !== null && typeof patch === "object" ? encode(patch) : undefined;
  const handlersOf = (group: object | undefined, method: string) => (group as Record<string, unknown> | undefined)?.[method];

  const set: Record<string, MockSetter> = {};
  for (const [method, setter] of Object.entries(spec.setters ?? {})) {
    const handler = handlersOf(definition.setters, method) as MockSetterHandler<unknown, unknown> | undefined;
    set[setter.member] = async (state, wire) => {
      const value = fields[setter.member]?.decode(wire) ?? wire;
      return patchOf(handler ? await handler(decode(state), value) : undefined);
    };
  }
  const commands: Record<string, MockCommand> = {};
  const can: Record<string, (state: MockState, input: unknown) => boolean> = {};
  for (const [method, command] of Object.entries(spec.commands ?? {})) {
    const handler = handlersOf(definition.commands, method) as MockCommandHandler<unknown, unknown[]> | undefined;
    const available = handlersOf(definition.canExecute, method) as ((state: unknown, ...args: unknown[]) => boolean) | undefined;
    const resolves = handlersOf(definition.resolveKey, method) as ((state: unknown, ...args: unknown[]) => boolean) | undefined;
    const name = upperFirst(method);
    const inputOf = (wire: unknown) => command.decodeInput && wire !== undefined ? [command.decodeInput(wire)] : [];
    // As a producer does, the key is resolved before availability is evaluated.
    const check = (state: MockState, wire: unknown): "unresolved-key" | "unavailable" | undefined => {
      const input = inputOf(wire);
      if (resolves && !resolves(decode(state), ...input)) return "unresolved-key";
      if (command.available && state[command.available] === false) return "unavailable";
      if (available && !available(decode(state), ...input)) return "unavailable";
      return undefined;
    };
    can[command.member] = (state, wire) => {
      const problem = check(state, wire);
      if (problem === "unresolved-key") throw rejected(`${name} has no object for this key.`, "unresolved-key");
      return problem === undefined;
    };
    commands[command.member] = async (state, wire) => {
      try { return patchOf(handler ? await handler(decode(state), ...inputOf(wire)) : undefined); }
      catch (error) { throw encodedFailure(error, command.encodeFailure); }
    };
  }
  const operations: Record<string, MockOperationHandler | "manual"> = {};
  const wrapOperation = (method: string) => (raw: MockOperation): MockTypedOperation<unknown, unknown, unknown> => {
    const plan = spec.operations![method]!;
    return {
      get requestId() { return raw.requestId; },
      get input() { return plan.decodeInput && raw.input !== undefined ? plan.decodeInput(raw.input) : raw.input; },
      get kind() { return raw.kind; },
      get signal() { return raw.signal; },
      get items() { return raw.items; },
      get progress() { return raw.progress; },
      reportProgress: value => raw.reportProgress(value),
      succeed: (...result: unknown[]) => raw.succeed(result.length === 0 || !plan.encodeResult ? result[0] : plan.encodeResult(result[0])),
      fail: (message, detail) => raw.fail(message, detail),
      failWith: failure => raw.failWith(plan.encodeFailure ? plan.encodeFailure(failure) : failure),
      cancel: () => raw.cancel(),
      expire: () => raw.expire(),
      forget: () => raw.forget(),
      emit: (...items) => raw.emit(...items.map(item => plan.encodeResult ? plan.encodeResult(item) : item)),
    };
  };
  for (const [method, plan] of Object.entries(spec.operations ?? {})) {
    const handler = handlersOf(definition.operations, method) as MockTypedOperationHandler<unknown, unknown, unknown, unknown> | "manual" | undefined;
    if (handler === "manual") operations[plan.member] = "manual";
    else if (handler) {
      operations[plan.member] = async (state, _input, operation) => {
        const typed = wrapOperation(method)(operation);
        let outcome: MockTypedOperationOutcome<unknown, unknown> | void;
        try { outcome = await handler(decode(state), typed.input, typed); }
        catch (error) { throw encodedFailure(error, plan.encodeFailure); }
        if (!outcome) return undefined;
        return {
          ...(outcome.state ? { state: encode(outcome.state) } : {}),
          ...(outcome.result !== undefined ? { result: plan.encodeResult ? plan.encodeResult(outcome.result) : outcome.result } : {}),
        };
      };
    }
  }

  const raw = bridge.view(route, {
    state: { ...spec.defaults, ...encode(definition.state) },
    set,
    commands,
    can,
    ...(spec.checkedFields ? { checkedFields: spec.checkedFields } : {}),
    ...(spec.collections ? { collections: Object.fromEntries(Object.entries(spec.collections)
      .map(([field, codec]) => [field, (item: unknown) => codec.key(codec.decode(item) as never)])) } : {}),
    operations,
    ...(spec.contract ? { contract: spec.contract } : {}),
    streams: Object.values(spec.operations ?? {}).filter(plan => plan.stream).map(plan => plan.member),
  });

  // Client methods by name: which request kind and member each one sends.
  const requests = new Map<string, { readonly kind: string; readonly member: string }>();
  for (const [method, setter] of Object.entries(spec.setters ?? {})) {
    requests.set(method, { kind: "set", member: setter.member });
    if (setter.checked) requests.set(setter.checked, { kind: "write", member: setter.member });
  }
  for (const [method, command] of Object.entries(spec.commands ?? {})) {
    requests.set(method, { kind: "command", member: command.member });
    requests.set(`can${upperFirst(method)}`, { kind: "can", member: command.member });
  }
  for (const [method, plan] of Object.entries(spec.operations ?? {})) requests.set(`start${upperFirst(method)}`, { kind: "start", member: plan.member });

  const collections: Record<string, MockTypedCollection<unknown>> = {};
  for (const [field, codec] of Object.entries(spec.collections ?? {})) {
    collections[field] = {
      get items() { return raw.collection(field).items.map(item => codec.decode(item)); },
      get keys() { return raw.collection(field).keys; },
      add: (items, index) => raw.collection(field).add((Array.isArray(items) ? items : [items]).map(item => codec.encode(item)), index),
      remove: key => raw.collection(field).remove(key),
      replace: (key, item) => raw.collection(field).replace(key, codec.encode(item)),
      move: (key, index) => raw.collection(field).move(key, index),
    };
  }
  const operationLog: Record<string, readonly MockTypedOperation<unknown, unknown, unknown>[]> = {};
  for (const [method, plan] of Object.entries(spec.operations ?? {}))
    Object.defineProperty(operationLog, method, { enumerable: true, get: () => raw.operations(plan.member).map(wrapOperation(method)) });
  const interactions: Record<string, MockTypedInteraction<unknown, unknown>> = {};
  for (const [name, codec] of Object.entries(spec.interactions ?? {})) {
    interactions[name] = {
      get handled() { return raw.interactionHandlers.includes(name); },
      async request(input, options) {
        const reply = await raw.interact(name, codec.encodeInput(input), options);
        return reply.kind === "answered" ? { kind: "answered", output: codec.decodeOutput(reply.output) } : reply;
      },
    };
  }
  mockBridgeInternals(bridge);

  const view: MockTypedView<unknown, unknown> & Record<string, unknown> = {
    route,
    reference: { kind: definition.kind ?? spec.kind, id: definition.id ?? route },
    get state() { return decode(raw.state); },
    get revision() { return raw.revision; },
    raw,
    get calls() { return bridge.calls.filter(call => call.route === route); },
    update(patch) {
      raw.update(state => ({ ...state, ...encode(typeof patch === "function" ? (patch as (state: never) => object)(decode(state)) : patch as object) }));
    },
    failNext(method, typed, times) {
      const target = requests.get(method as string);
      if (!target) throw new RangeError(`The mock ${spec.kind} has no client method ${String(method)}.`);
      let failure: MockFailure = typed as MockFailure;
      if (failure.kind === "domain-failed") {
        // A start{Command} failure is encoded by the operation's codec, a command's by its own.
        const encodeFailure = target.kind === "start"
          ? Object.values(spec.operations ?? {}).find(plan => plan.member === target.member)?.encodeFailure
          : Object.values(spec.commands ?? {}).find(command => command.member === target.member)?.encodeFailure;
        failure = { ...failure, failure: encodeFailure ? encodeFailure(failure.failure) : failure.failure };
      }
      raw.failNext(target, failure, times);
    },
    pushFailure: failure => raw.pushFailure(failure),
    batch: edit => raw.batch(edit),
    collections,
    operations: operationLog,
    interactions,
  };
  return view;
}
