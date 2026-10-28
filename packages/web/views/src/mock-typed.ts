import type { BridgeErrorKind, BridgeFailureDetail } from "./errors.js";
import type { BridgeOutcomeFailure } from "./controller.js";
import type {
  MockBridge,
  MockCall,
  MockDomainFailure,
  MockErrorFailure,
  MockInteractionOptions,
  MockOperationKind,
  MockState,
  MockView,
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

/**
 * Registers a typed mock View. Generated `mock{Name}()` helpers call this with
 * their client's codecs; applications call the generated helper.
 */
export function mockTypedView(bridge: MockBridge, spec: MockTypedViewSpec, definition: MockTypedViewDefinition): unknown {
  void bridge; void spec; void definition;
  throw new Error("Not implemented yet.");
}
