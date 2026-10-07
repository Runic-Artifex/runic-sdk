import type { BridgeErrorKind, BridgeFailureDetail } from "./errors.js";
import {
  mockBridgeInternals,
  type MockBridge,
  type MockCall,
  type MockFailure,
  type MockInteractionOptions,
  type MockOperationHandler,
  type MockOperationKind,
  type MockRegisteredView,
  type MockRoute,
  type MockState,
  type MockView,
} from "./mock.js";

/** The wire form of a content reference in a mock state, such as `{ kind: "editor", id: "1" }`. */
export interface MockReference<TKind extends string = string> {
  readonly kind: TKind;
  readonly id: string;
}

type Patch<TState> = Partial<{ -readonly [K in keyof TState]: TState[K] }>;
type Awaitable<T> = T | Promise<T>;

/** Runs a command and returns the state changes it makes. A thrown error becomes the client's BridgeError. */
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
export interface MockTypedOperation<TInput, TResult> {
  readonly requestId: string;
  readonly input: TInput;
  readonly kind: MockOperationKind;
  readonly signal: AbortSignal;
  /** Stream values emitted so far. */
  readonly items: readonly TResult[];
  succeed(...result: [TResult] extends [never] ? [] : [result: TResult]): void;
  fail(message?: string, detail?: BridgeFailureDetail): void;
  cancel(): void;
  emit(...items: TResult[]): void;
}

/**
 * Runs a started operation. When it returns, a still running operation succeeds
 * with the outcome; a throw fails it. Return a promise that waits on the
 * operation's signal, or use `"manual"`, to settle it from the test.
 */
export type MockTypedOperationHandler<TState, TInput, TResult> =
  (state: TState, input: TInput, operation: MockTypedOperation<TInput, TResult>) =>
    Awaitable<MockTypedOperationOutcome<TState, TResult> | void>;

/** A keyed collection of a typed mock. Each edit pushes a delta frame. */
export interface MockTypedCollection<TItem> {
  readonly items: readonly TItem[];
  readonly keys: readonly string[];
  add(items: TItem | readonly TItem[], index?: number): void;
  remove(key: string): void;
  replace(key: string, item: TItem): void;
  move(key: string, index: number): void;
}

/** An interaction that .NET asks the browser to handle. */
export interface MockTypedInteraction<TInput, TOutput> {
  /** Whether a mounted client currently handles it. */
  readonly handled: boolean;
  /** Sends a request to the client's handler and resolves with its reply. */
  request(input: TInput, options?: MockInteractionOptions): Promise<
    | { readonly kind: "answered"; readonly output: TOutput }
    | { readonly kind: "cancelled" | "failed" | "unhandled" }>;
}

/** A client method name a failure can target, such as `save`, `startSave`, `setTitle` or `canSetViewport`. */
export type MockMethodName<TClient> = Exclude<keyof TClient & string,
  "snapshot" | "subscribe" | "dispose" | "interactions" | "fieldBaseline" | `recover${string}` | `${string}WithRequestId`>;

/** The typed mock of one generated client, created by a generated `mock{Name}()`. */
export interface MockTypedView<TState, TClient, TKind extends string = string> {
  /** The route prefix, such as `editor` for a root View or `content1` for content. */
  readonly route: string;
  /** Put this in a parent mock's state to present this View as content. */
  readonly reference: MockReference<TKind>;
  readonly state: TState;
  readonly revision: number;
  /** The untyped mock View, for raw frames. */
  readonly raw: MockView;
  /** Calls to this View's routes, in order. */
  readonly calls: readonly MockCall[];
  /** Merges state changes and pushes the new state, like a .NET publication. */
  update(patch: Patch<TState> | ((state: TState) => Patch<TState>)): void;
  /** Answers the next `times` calls of a client method with `failure`. */
  failNext(method: MockMethodName<TClient>, failure: MockFailure, times?: number): void;
  /** Pushes a failure notice; the client keeps its last state and reports the error. */
  pushFailure(failure: { readonly kind?: BridgeErrorKind; readonly message: string; readonly detail?: BridgeFailureDetail }): void;
  /** Pushes the collection edits made by `edit` as one delta frame. */
  batch(edit: () => void): void;
}

interface FieldCodec { encode(value: unknown): unknown; decode(wire: unknown): unknown }

/** What a generated `mock{Name}()` knows about its client. Generated code is its only intended caller. */
export interface MockTypedViewSpec {
  readonly kind: string;
  /** The root route prefix. */
  readonly route: string;
  /** Codecs of value fields by wire name. Other fields, such as content references, are stored as given. */
  readonly fields: Readonly<Record<string, FieldCodec>>;
  /** State the client always receives, such as `canSave: true`. */
  readonly defaults: MockState;
  readonly checkedFields?: readonly string[];
  /** Keyed collections by wire name: a row codec and its key. */
  readonly collections?: Readonly<Record<string, FieldCodec & { key(item: never): string }>>;
  /** Setters by client method: route suffix, wire field, and how to read the route argument as a wire value. */
  readonly setters?: Readonly<Record<string, { readonly route: string; readonly field: string; readonly read: (argument: unknown) => unknown;
    readonly checked?: string }>>;
  /** Commands by client method. */
  readonly commands?: Readonly<Record<string, { readonly route: string; readonly read?: (argument: unknown) => unknown;
    readonly available?: string }>>;
  /** Operations by client command method. */
  readonly operations?: Readonly<Record<string, { readonly member: string; readonly decodeInput?: (wire: unknown) => unknown;
    readonly encodeResult?: (value: unknown) => unknown; readonly stream?: boolean }>>;
  /** The generated contract, `{ViewModel full name}:{fingerprint}`. */
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
  readonly operations?: object;
}

const upperFirst = (value: string) => value.charAt(0).toUpperCase() + value.slice(1);

function rejected(message: string): Error {
  return Object.assign(new Error(message), { kind: "rejected" });
}

/**
 * Registers a typed mock View. Generated `mock{Name}()` helpers call this with
 * their client's codecs; applications call the generated helper.
 */
export function mockTypedView(bridge: MockBridge, spec: MockTypedViewSpec, definition: MockTypedViewDefinition): unknown {
  const route = definition.id === undefined ? spec.route : `content${definition.id}`;
  if (definition.id !== undefined && !/^[A-Za-z0-9_-]+$/.test(definition.id))
    throw new RangeError("A mock content id must contain only letters, digits, '_' and '-'.");
  const internals = mockBridgeInternals(bridge);
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
  const registered = () => internals.registered(route) as MockRegisteredView;

  const routes: Record<string, MockRoute> = {};
  for (const [method, setter] of Object.entries(spec.setters ?? {})) {
    const handler = (definition.setters as Record<string, unknown> | undefined)?.[method] as MockSetterHandler<unknown, unknown> | undefined;
    const apply = async (state: MockState, wire: unknown) => {
      const value = fields[setter.field]?.decode(wire) ?? wire;
      return patchOf(handler ? await handler(decode(state), value) : undefined);
    };
    routes[setter.route] = async (state, argument) => {
      const wire = setter.read(argument);
      return { [setter.field]: wire, ...((await apply(state, wire)) ?? {}) };
    };
    if (setter.checked) routes[setter.checked] = (_state, payload) => internals.writeChecked(registered(), setter.field, payload, apply);
  }
  for (const [method, command] of Object.entries(spec.commands ?? {})) {
    const handler = (definition.commands as Record<string, unknown> | undefined)?.[method] as MockCommandHandler<unknown, unknown[]> | undefined;
    const available = (definition.canExecute as Record<string, unknown> | undefined)?.[method] as ((state: unknown, ...args: unknown[]) => boolean) | undefined;
    const name = upperFirst(method);
    routes[command.route] = async (state, ...args) => {
      const input = command.read && args.length > 0 ? [command.read(args[0])] : [];
      if (command.available && state[command.available] === false) throw rejected(`${name} is unavailable.`);
      if (available && !available(decode(state), ...input)) throw rejected(`${name} is unavailable.`);
      return patchOf(handler ? await handler(decode(state), ...input) : undefined);
    };
    if (command.read) routes[`Can${name}`] = (state, argument) => available ? available(decode(state), command.read!(argument)) : true;
  }
  const operations: Record<string, MockOperationHandler | "manual"> = {};
  const wrapOperation = (method: string) => (raw: ReturnType<MockView["operations"]>[number]): MockTypedOperation<unknown, unknown> => {
    const plan = spec.operations![method]!;
    return {
      get requestId() { return raw.requestId; },
      get input() { return plan.decodeInput && raw.input !== undefined ? plan.decodeInput(raw.input) : raw.input; },
      get kind() { return raw.kind; },
      get signal() { return raw.signal; },
      get items() { return raw.items; },
      succeed: (...result: unknown[]) => raw.succeed(result.length === 0 || !plan.encodeResult ? result[0] : plan.encodeResult(result[0])),
      fail: (message, detail) => raw.fail(message, detail),
      cancel: () => raw.cancel(),
      emit: (...items) => raw.emit(...items.map(item => plan.encodeResult ? plan.encodeResult(item) : item)),
    };
  };
  for (const [method, plan] of Object.entries(spec.operations ?? {})) {
    const handler = (definition.operations as Record<string, unknown> | undefined)?.[method] as MockTypedOperationHandler<unknown, unknown, unknown> | "manual" | undefined;
    if (handler === "manual") operations[plan.member] = "manual";
    else if (handler) {
      operations[plan.member] = async (state, _input, operation) => {
        const typed = wrapOperation(method)(operation);
        const outcome = await handler(decode(state), typed.input, typed);
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
    routes,
    ...(spec.checkedFields ? { checkedFields: spec.checkedFields } : {}),
    ...(spec.collections ? { collections: Object.fromEntries(Object.entries(spec.collections)
      .map(([field, codec]) => [field, (item: unknown) => codec.key(codec.decode(item) as never)])) } : {}),
    operations,
    ...(spec.contract ? { contract: spec.contract } : {}),
    streams: Object.values(spec.operations ?? {}).filter(plan => plan.stream).map(plan => plan.member),
  });

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
  const operationLog: Record<string, readonly MockTypedOperation<unknown, unknown>[]> = {};
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

  const view: MockTypedView<unknown, unknown> & Record<string, unknown> = {
    route,
    reference: { kind: definition.kind ?? spec.kind, id: definition.id ?? route },
    get state() { return decode(raw.state); },
    get revision() { return raw.revision; },
    raw,
    get calls() { return bridge.calls.filter(call => ownedBy(call.name)); },
    update(patch) {
      raw.update(state => ({ ...state, ...encode(typeof patch === "function" ? (patch as (state: never) => object)(decode(state)) : patch as object) }));
    },
    failNext(method, failure, times) { bridge.failNext(`${route}${upperFirst(method as string)}`, failure, times); },
    pushFailure: failure => raw.pushFailure(failure),
    batch: edit => raw.batch(edit),
    collections,
    operations: operationLog,
    interactions,
  };
  // A longer registered route (`content1` vs `content12`) owns its own calls.
  function ownedBy(name: string): boolean {
    if (!name.startsWith(route)) return false;
    for (let length = name.length; length > route.length; length--)
      if (internals.registered(name.slice(0, length))) return false;
    return true;
  }
  return view;
}
