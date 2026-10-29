import {
  createElement,
  useCallback,
  useEffect,
  useInsertionEffect,
  useMemo,
  useReducer,
  useRef,
  useState,
  useSyncExternalStore,
  type ComponentType,
  type ReactNode,
} from "react";
import {
  createCommandController,
  createLatestOperationController,
  createOperationController,
  createFieldController,
  createTextFieldController,
  createViewController,
  onBridgeConnection,
  type BridgeConnectionOptions,
  type BridgeConnectionState,
  type BridgeField,
  type BridgeOperation,
  type BridgeOutcomeFailure,
  type FieldDefinition,
  type FieldOptions,
  type FieldState,
  type LatestOperationController,
  type LatestOperationState,
  type OperationController,
  type OperationControllerOptions,
  type OperationState,
  type PageReference,
  type ViewClient,
  type ViewController,
  type ViewControllerState,
  type ViewSource,
} from "@runic-artifex/views";
import { isViewClient, viewSourceIdentity } from "@runic-artifex/views/bindings";

// React itself requires bundlers to define process.env.NODE_ENV.
declare const process: { readonly env: { readonly NODE_ENV?: string } };

export interface ViewHandle<TClient extends ViewClient> {
  /** The latest state, or undefined until the client connects. */
  readonly state: TClient["snapshot"] | undefined;
  /** The connected client for commands, or undefined until it connects. */
  readonly client: TClient | undefined;
  /** The connection failure, if any. */
  readonly error: unknown;
  /** True while a reference is connecting. */
  readonly pending: boolean;
  /** Connects a reference again after a failure. */
  retry(): void;
}

type InlineConnectTracker = { connect: unknown; text: string; times: number[]; warned: boolean };

// An inline `{ connect: () => connectX() }` creates a new function on every
// render, so every render reconnects and each connection renders again.
function warnInlineConnect(tracker: InlineConnectTracker, connect: () => unknown): void {
  if (tracker.connect === connect) return;
  const text = Function.prototype.toString.call(connect);
  const now = Date.now();
  tracker.times = tracker.text === text ? [...tracker.times.filter(time => now - time < 1000), now] : [now];
  tracker.connect = connect;
  tracker.text = text;
  if (tracker.times.length < 4 || tracker.warned) return;
  tracker.warned = true;
  console.warn("useView reconnected repeatedly because its source's connect function is recreated on every render, " +
    "for example `useView({ connect: () => connectWorkspace() })`. Pass a stable function such as " +
    "`{ connect: connectWorkspace }` or a generated page reference, or memoize the source with useMemo.");
}

type ViewStore<TClient extends ViewClient> = {
  readonly subscribe: (onChange: () => void) => () => void;
  readonly read: () => ViewControllerState<TClient>;
  /** Takes the controller's state after `setSource` when what the hook returns changed. */
  readonly refresh: () => void;
};

// The controller changes `current` silently when the effect sets its source.
// The store copies it and re-renders only when what the hook returns changes,
// so an inline connect function cannot re-render synchronously forever, and a
// returned client is never one the controller already released.
function createViewStore<TClient extends ViewClient>(controller: ViewController<TClient>): ViewStore<TClient> {
  let snapshot = controller.current;
  const listeners = new Set<() => void>();
  const visible = (left: ViewControllerState<TClient>, right: ViewControllerState<TClient>) =>
    left.client !== right.client || left.state !== right.state || left.error !== right.error || left.pending !== right.pending;
  // React re-renders whenever the snapshot object changes, so keep the copy
  // unless what the hook returns changes. A copy with the same client, state,
  // error and pending is equivalent: a released client is never current again.
  const refresh = () => {
    const next = controller.current;
    if (next === snapshot || !visible(next, snapshot)) return;
    snapshot = next;
    for (const listener of [...listeners]) listener();
  };
  controller.subscribe(() => {
    snapshot = controller.current;
    for (const listener of [...listeners]) listener();
  });
  return {
    subscribe(onChange) {
      listeners.add(onChange);
      // Pick up changes made before React subscribed, such as a fast connection.
      refresh();
      return () => { listeners.delete(onChange); };
    },
    read: () => snapshot,
    refresh,
  };
}

export interface UseViewOptions<TClient extends ViewClient> {
  /** Releases a client this hook connected. Defaults to `client.dispose()`. */
  readonly release?: (client: TClient) => void;
}

/**
 * Connects a generated View reference for the lifetime of the component and
 * renders its state through `useSyncExternalStore`. A changed reference
 * disposes the previous client; StrictMode's extra mount disposes its
 * connection when it resolves.
 *
 * A reference is identified by its `connect` function, so an inline
 * `{ connect: connectWorkspace }` does not reconnect. In development a
 * `connect` function recreated on every render logs a warning.
 */
export function useView<TClient extends ViewClient>(source: ViewSource<TClient>, options: UseViewOptions<TClient> = {}): ViewHandle<TClient> {
  const release = useRef(options.release);
  useInsertionEffect(() => { release.current = options.release; });
  const [controller] = useState(() => createViewController<TClient>({
    release: client => { if (release.current) release.current(client); else client.dispose(); },
  }));
  const [store] = useState(() => createViewStore(controller));
  const tracker = useRef<InlineConnectTracker>({ connect: undefined, text: "", times: [], warned: false });
  const identity = viewSourceIdentity(source);
  useEffect(() => {
    if (process.env.NODE_ENV !== "production" && source && !isViewClient(source)) warnInlineConnect(tracker.current, source.connect);
    controller.setSource(source);
    store.refresh();
    // A following run refreshes the store; after unmount nothing renders it.
    return () => { controller.setSource(null); };
    // The source is identified by its client or connect function, not by its wrapper object.
  }, [controller, store, identity]);
  const current = useSyncExternalStore(store.subscribe, store.read, store.read);
  const retry = useCallback(() => controller.retry(), [controller]);

  const observed = source && isViewClient(source) ? source : undefined;
  // A connected client is readable before the effect observes it.
  if (observed && current.client !== observed)
    return { state: observed.snapshot, client: observed, error: undefined, pending: false, retry };
  // Until the effect follows a changed reference, do not show the previous one.
  if (viewSourceIdentity(current.source) !== identity)
    return { state: undefined, client: undefined, error: undefined, pending: source !== null && source !== undefined, retry };
  return { state: current.state, client: current.client, error: current.error, pending: current.pending, retry };
}

/**
 * A build-known map checks each generated reference kind against its component's `page` prop. A nullable
 * reference, such as a region slot's `State["main"]`, checks its non-null kinds.
 */
export type ViewRegistry<R extends PageReference | null | undefined> = {
  readonly [K in NonNullable<R>["kind"]]: ComponentType<{ readonly page: Extract<NonNullable<R>, { readonly kind: K }> }>;
};

export interface ViewOutletProps<R extends PageReference> {
  /** The presented reference, such as `state.main`. */
  readonly content: R | null | undefined;
  readonly registry: ViewRegistry<R>;
  /** Rendered while there is no content. Defaults to nothing. */
  readonly fallback?: ReactNode;
  /**
   * Renders a reference whose kind has no registered component, for example localized text.
   * Defaults to an English `<p role="alert">`.
   */
  readonly missing?: (content: R) => ReactNode;
}

const referenceKeys = new WeakMap<object, number>();
let nextReferenceKey = 0;

function referenceKey(reference: object): number {
  let key = referenceKeys.get(reference);
  if (key === undefined) referenceKeys.set(reference, key = ++nextReferenceKey);
  return key;
}

/**
 * Renders the registered component for a generated View reference and passes
 * the reference as its `page` prop. A different reference remounts the
 * component. A kind without a component renders `missing`, or an English alert without it.
 */
export function ViewOutlet<R extends PageReference>({ content, registry, fallback = null, missing }: ViewOutletProps<R>): ReactNode {
  if (!content) return fallback;
  const component = registry[content.kind as R["kind"]] as ComponentType<{ readonly page: R }> | undefined;
  if (!component) {
    if (missing) return missing(content);
    return createElement("p", { role: "alert" }, `No web component is registered for ${content.kind}.`);
  }
  return createElement(component, { key: referenceKey(content), page: content });
}

// A command or operation controller lives while its component is mounted: unmounting disposes it,
// which stops its feedback and, for an operation, settles its pending run promises and stops
// observing accepted work (it never cancels the work). StrictMode's simulated unmount, and a component that React shows again (`<Activity>`),
// mount once more with a fresh controller, so feedback of runs started before the unmount ends.
const disposedControllers = new WeakSet<object>();

function useMountedController<T extends { dispose(): void }>(create: () => T): T {
  const controller = useRef<T | null>(null);
  if (controller.current === null) controller.current = create();
  const [, remount] = useReducer((count: number) => count + 1, 0);
  useEffect(() => {
    if (disposedControllers.has(controller.current!)) {
      controller.current = create();
      remount();
    }
    const mounted = controller.current!;
    return () => {
      disposedControllers.add(mounted);
      mounted.dispose();
    };
    // The controller reads the latest callbacks through refs, so it is created once per mount.
  }, []);
  return controller.current;
}

export interface CommandHandle<TArgs extends readonly unknown[], TResult, TFailure = BridgeOutcomeFailure<TResult>> {
  /**
   * Runs the command. Resolves to its result, including a `BridgeOutcome` with a
   * declared failure, which `failure` then holds, or to undefined after an
   * unexpected failure, which `error` then holds. Never rejects.
   */
  run(...args: TArgs): Promise<TResult | undefined>;
  /** True while a run is in flight. */
  readonly pending: boolean;
  /** Why the latest run failed unexpectedly, until the next run starts. */
  readonly error: unknown;
  /**
   * The declared failure of the latest run, for a command that resolves a
   * `BridgeOutcome`, until the next run starts. A superseded run sets neither.
   */
  readonly failure: TFailure | undefined;
  /** Clears `error` and `failure`. */
  reset(): void;
}

/**
 * Tracks a command's pending state and failure for rendering. `run` always
 * calls the latest `command`, so it may close over the current client. Unmounting
 * disposes the controller: `pending`, `error` and `failure` stop updating, while a
 * run still in flight resolves as usual.
 *
 * ```tsx
 * const increment = useCommand(() => client?.increment());
 * <button disabled={!client || increment.pending} onClick={() => increment.run()}>…</button>
 * ```
 */
export function useCommand<TArgs extends readonly unknown[], TReturn>(
  command: (...args: TArgs) => TReturn,
): CommandHandle<TArgs, Awaited<TReturn>> {
  const latest = useRef(command);
  useInsertionEffect(() => { latest.current = command; });
  const controller = useMountedController(() => createCommandController((...args: TArgs) => latest.current(...args)));
  const read = useCallback(() => controller.current, [controller]);
  const { pending, error, failure } = useSyncExternalStore(controller.subscribe, read, read);
  return { run: controller.run, pending, error, failure, reset: controller.reset };
}

export interface OperationHandle<TArgs extends readonly unknown[], TResult, TFailure = never>
  extends OperationState<TResult, TFailure>, Pick<OperationController<TArgs, TResult, TFailure>, "run" | "cancel" | "reset"> {}

/**
 * Tracks Start admission, pending work, cancellation and terminal feedback of a
 * generated `start<Command>()` operation for rendering. `run` always calls the
 * latest `start` and options, so they may close over the current client. A run
 * that ends `cancelled` sets `cancelled`, not `error`. Unmounting disposes the
 * controller: a run still in flight resolves to undefined and `waitForCompletion`
 * is not called after it, but accepted work is never cancelled.
 *
 * ```tsx
 * const push = useOperation((remote: string) => client?.startPush(remote));
 * <button disabled={!client || push.pending} onClick={() => push.run("origin")}>Push</button>
 * {push.pending && <button disabled={push.cancellationRequested} onClick={() => push.cancel()}>Cancel</button>}
 * {push.cancelled && <p role="status">Push cancelled.</p>}
 * ```
 */
export function useOperation<TArgs extends readonly unknown[], TResult, TFailure = never>(
  start: (...args: TArgs) => BridgeOperation<TResult, TFailure> | undefined | PromiseLike<BridgeOperation<TResult, TFailure> | undefined>,
  options: OperationControllerOptions<TResult, TFailure> = {},
): OperationHandle<TArgs, TResult, TFailure> {
  const latest = useRef({ start, options });
  useInsertionEffect(() => { latest.current = { start, options }; });
  const controller = useMountedController(() => createOperationController((...args: TArgs) => latest.current.start(...args), {
    cancel: operation => latest.current.options.cancel ? latest.current.options.cancel(operation) : operation.cancel(),
    waitForCompletion: (operation, status) => latest.current.options.waitForCompletion?.(operation, status),
  }));
  const read = useCallback(() => controller.current, [controller]);
  const current = useSyncExternalStore(controller.subscribe, read, read);
  return { ...current, run: controller.run, cancel: controller.cancel, reset: controller.reset };
}

export interface LatestOperationHandle<TResult, TFailure = never>
  extends LatestOperationState<TResult, TFailure>, Pick<LatestOperationController<TResult, TFailure>, "run" | "clear"> {}

/**
 * Admits only the newest selection, for reads such as "load the selected item": each
 * `run(intent)` replaces the queued intent and cancels the active read, and the next read
 * starts only after the previous one has finished (including `waitForCompletion`). The
 * intent carries `start`, `cancel`, `waitForCompletion` and `isCurrent`, so it may close over
 * the current client. `blocked` means admission is uncertain; only `clear("replace")`, after
 * the application has established a fresh session, clears it. Unmounting disposes the
 * controller: pending runs resolve to undefined, but accepted work is never cancelled.
 *
 * ```tsx
 * const load = useLatestOperation<Item>();
 * <li onClick={() => load.run({ start: () => client?.startLoad(id) })}>…</li>
 * {load.outcome?.ok && <ItemView item={load.outcome.value} />}
 * ```
 */
export function useLatestOperation<TResult = void, TFailure = never>(): LatestOperationHandle<TResult, TFailure> {
  const controller = useMountedController(() => createLatestOperationController<TResult, TFailure>());
  const read = useCallback(() => controller.current, [controller]);
  const current = useSyncExternalStore(controller.subscribe, read, read);
  return { ...current, run: controller.run, clear: controller.clear };
}

export interface TextFieldHandle {
  /** What the input shows: the draft while one exists, otherwise the member's value in the View's state. */
  readonly value: string;
  /**
   * True while a draft exists, from the first input until the last write settled and the state caught up, and after a
   * failed write. Keep the input enabled while it is true, rather than disabling it on the View's busy state, which
   * the field's own write causes.
   */
  readonly editing: boolean;
  /** Why the latest write failed, until the next input or `reset()`. */
  readonly error: unknown;
  /** The input's `onChange`: the user changed the text. */
  onChange(event: { readonly currentTarget: { readonly value: string } }): void;
  /** The input's `onCompositionStart`: the text is kept but not written until the IME composition ends. */
  onCompositionStart(): void;
  /** The input's `onCompositionEnd`: the composed text is written. */
  onCompositionEnd(event: { readonly currentTarget: { readonly value: string } }): void;
  /** Drops the draft and the error and shows the View's value. Writes in flight still complete. */
  reset(): void;
}

export interface UseTextFieldOptions {
  /**
   * With `true` (the default) at most one write is in flight, and text typed meanwhile is written next, as one
   * value. With `false` every change is written. Read when the component mounts.
   */
  readonly coalesce?: boolean;
  /**
   * With `true` (the default) a failed write keeps the draft and sets `error`; with `false` the field shows the
   * View's value again. Read when the component mounts.
   */
  readonly keepDraftOnError?: boolean;
}

/** The View a text field binds to: a {@link useView} handle, or any state and client. */
export interface TextFieldView<TClient extends ViewClient> {
  readonly state: TClient["snapshot"] | undefined;
  readonly client: TClient | undefined;
}

/**
 * Binds a controlled text input to one member of a View while the component is mounted, through
 * `createTextFieldController`. Typing key by key never loses characters: while its writes are in flight the field
 * shows what the user typed, at most one write is in flight (newer text is written next), nothing is written during
 * IME composition, and once the last write settled it shows the View's value, including any normalization. `read`
 * and `write` may close over render values; the latest ones are used.
 *
 * ```tsx
 * const person = useView(page);
 * const name = useTextField(person, state => state.name, (client, value) => client.setName(value));
 * <input value={name.value} onChange={name.onChange}
 *   onCompositionStart={name.onCompositionStart} onCompositionEnd={name.onCompositionEnd} />
 * ```
 *
 * `write` may be a generated setter, a command or a checked write. Its failure, and a checked write's `rejected` or
 * `conflict` receipt, keep the draft and set `error` (a receipt's is a `FieldError`). For a generated member, prefer
 * `useField` with its `{View}Fields` descriptor.
 * Unmounting writes text that waits for a write in flight, and stops updating the handle.
 */
export function useTextField<TClient extends ViewClient>(
  view: TextFieldView<TClient>,
  read: (state: TClient["snapshot"]) => string,
  write: (client: TClient, value: string) => Promise<unknown>,
  options: UseTextFieldOptions = {},
): TextFieldHandle {
  const latest = useRef({ read, write });
  useInsertionEffect(() => { latest.current = { read, write }; });
  const controller = useMountedController(() => createTextFieldController<TClient["snapshot"], TClient>({
    read: state => latest.current.read(state),
    write: (client, value) => latest.current.write(client, value),
    ...options.coalesce === undefined ? {} : { coalesce: options.coalesce },
    ...options.keepDraftOnError === undefined ? {} : { keepDraftOnError: options.keepDraftOnError },
  }));
  const { state, client } = view;
  useEffect(() => {
    controller.setSource(state === undefined || client === undefined ? undefined : { state, client });
  }, [controller, state, client]);
  const readCurrent = useCallback(() => controller.current, [controller]);
  const current = useSyncExternalStore(controller.subscribe, readCurrent, readCurrent);
  const handlers = useMemo(() => ({
    onChange: (event: { readonly currentTarget: { readonly value: string } }) => controller.input(event.currentTarget.value),
    onCompositionStart: () => controller.compositionStart(),
    onCompositionEnd: (event: { readonly currentTarget: { readonly value: string } }) => controller.compositionEnd(event.currentTarget.value),
    reset: () => controller.reset(),
  }), [controller]);
  // Without a draft the field shows the state being rendered, which the effect passes to the controller after the render.
  const value = current.editing ? current.value : state === undefined ? "" : read(state);
  return { value, editing: current.editing, error: current.error, ...handlers };
}

/**
 * What {@link useField} spreads on a checkbox (`<input type="checkbox">`): a boolean field. `onChange` reads `checked`.
 */
export interface FieldCheckboxProps {
  readonly checked: boolean;
  readonly onChange: (event: { readonly currentTarget: { readonly checked: boolean } }) => void;
  /** Commits the field (see `FieldHandle.commit`). */
  readonly onBlur: () => void;
  /** True while the field has validation messages (`errors`) or an `error`. */
  readonly "aria-invalid": boolean;
}

/**
 * What {@link useField} spreads on an `<input>`, `<textarea>` or `<select>` of any field but a boolean one: the field's
 * `text`, written through `inputText` (so `-` and `1.` survive typing into a number field), and IME composition.
 */
export interface FieldTextProps {
  readonly value: string;
  readonly onChange: (event: { readonly currentTarget: { readonly value: string } }) => void;
  /** The text is kept but not written until the IME composition ends. */
  readonly onCompositionStart: () => void;
  /** The composed text is written. */
  readonly onCompositionEnd: (event: { readonly currentTarget: { readonly value: string } }) => void;
  /** Commits the field (see `FieldHandle.commit`). */
  readonly onBlur: () => void;
  /** True while the field has validation messages (`errors`) or an `error`. */
  readonly "aria-invalid": boolean;
}

/** The props of a field's element: {@link FieldCheckboxProps} for a boolean field, otherwise {@link FieldTextProps}. */
export type FieldInputProps<TValue> = [TValue] extends [boolean | null] ? FieldCheckboxProps : FieldTextProps;

/** A field bound by {@link useField}: what it shows, the props of its element and its actions. */
export type FieldHandle<TValue> = FieldState<TValue> & {
  /** Spread on the field's `<input>`, `<textarea>` or `<select>`. The handlers are the same on every render. */
  readonly inputProps: FieldInputProps<TValue>;
  /** The user changed the value, such as from a custom control. */
  input(value: TValue): void;
  /**
   * The user is done with the field (`onBlur` calls it): with `commit: "pause"` the draft is written now. Resolves once
   * the field's writes settled, so a command that depends on the field can run after it:
   * `await title.commit(); await editor.save()`. Never rejects; a failure is `error`.
   */
  commit(): Promise<void>;
  /** Writes the draft again after a failed write; after a `conflict`, over the producer's value. */
  retry(): void;
  /** Drops the draft, the error and a conflict and shows the View's value. Writes in flight still complete. */
  reset(): void;
};

const noFieldErrors: readonly string[] = Object.freeze([]);

/** The text a field shows for a value, as the field controller formats it. */
function formatFieldValue(value: unknown): string {
  switch (typeof value) {
    case "string": return value;
    case "number": case "bigint": case "boolean": return String(value);
    default: return "";
  }
}

/**
 * Binds an element to one View member while the component is mounted, through `createFieldController`: pass a
 * generated descriptor such as `EditorFields.title`, or any definition with `read` and `write`. Spread `inputProps` on
 * an `<input>`, `<textarea>` or `<select>` (a checkbox for a boolean field). Typing never loses characters: the field
 * shows a draft while its writes are in flight, writes nothing during IME composition, keeps text that does not
 * parse (such as `1.` in a number field) as the draft, and once the last write settled shows the View's value.
 * `options` are read when the component mounts; the descriptor's `read`, `write` and `errors` may close over render
 * values, and the latest ones are used.
 *
 * ```tsx
 * const editor = useView(page);
 * const title = useField(editor, EditorFields.title, { commit: "pause", checked: true });
 * <input {...title.inputProps} />
 * {title.conflict && <button onClick={title.retry}>Keep mine</button>}
 * ```
 *
 * With `checked: true` a `rejected` or `conflict` receipt keeps the draft and sets `error` (a `FieldError`), and a
 * conflict holds further writes until `retry()` or `reset()`. Unmounting writes a value that waits to be written.
 */
export function useField<TClient extends ViewClient, TValue>(
  view: TextFieldView<TClient>,
  field: BridgeField<NoInfer<TClient>, NoInfer<TClient>["snapshot"], TValue> | FieldDefinition<NoInfer<TClient>, NoInfer<TClient>["snapshot"], TValue>,
  options: FieldOptions = {},
): FieldHandle<TValue> {
  type State = TClient["snapshot"];
  const latest = useRef(field);
  useInsertionEffect(() => { latest.current = field; });
  const controller = useMountedController(() => createFieldController<State, TClient, TValue>({
    ...field.name === undefined ? {} : { name: field.name },
    ...field.kind === undefined ? {} : { kind: field.kind },
    ...field.nullable === undefined ? {} : { nullable: field.nullable },
    ...field.integer === undefined ? {} : { integer: field.integer },
    ...field.choices === undefined ? {} : { choices: field.choices },
    read: state => latest.current.read(state),
    write: (client, value) => latest.current.write(client, value),
    ...field.checked === undefined ? {} : { checked: (client, value, write) => latest.current.checked!(client, value, write) },
    ...field.baseline === undefined ? {} : { baseline: client => latest.current.baseline!(client) },
    ...field.errors === undefined ? {} : { errors: state => latest.current.errors!(state) },
  }, options));
  const { state, client } = view;
  useEffect(() => {
    controller.setSource(state === undefined || client === undefined ? undefined : { state, client });
  }, [controller, state, client]);
  const readCurrent = useCallback(() => controller.current, [controller]);
  const current = useSyncExternalStore(controller.subscribe, readCurrent, readCurrent);
  const actions = useMemo(() => ({
    input: (value: TValue) => controller.input(value),
    commit: () => controller.commit(),
    retry: () => controller.retry(),
    reset: () => controller.reset(),
    onBlur: () => { void controller.commit(); },
    onChange: (event: { readonly currentTarget: { readonly value: string } }) => controller.inputText(event.currentTarget.value),
    onCheck: (event: { readonly currentTarget: { readonly checked: boolean } }) => controller.input(event.currentTarget.checked as TValue),
    onCompositionStart: () => controller.compositionStart(),
    onCompositionEnd: (event: { readonly currentTarget: { readonly value: string } }) => controller.compositionEnd(event.currentTarget.value),
  }), [controller]);
  // Without a draft the field shows the state being rendered, which the effect passes to the controller after the
  // render (setSource does not notify). Text that still means that value (such as `1.` for 1) stays.
  const kind = controller.field.kind;
  const value = current.editing ? current.value
    : state === undefined ? (kind === "text" || kind === undefined ? "" : undefined) as TValue
    : field.read(state);
  const text = Object.is(value, current.value) ? current.text : formatFieldValue(value);
  const errors = field.errors === undefined ? current.errors : state === undefined ? noFieldErrors : field.errors(state);
  const invalid = errors.length > 0 || current.error !== undefined;
  const inputProps = kind === "boolean"
    ? { checked: value === true, onChange: actions.onCheck, onBlur: actions.onBlur, "aria-invalid": invalid }
    : {
      value: text, onChange: actions.onChange, onCompositionStart: actions.onCompositionStart,
      onCompositionEnd: actions.onCompositionEnd, onBlur: actions.onBlur, "aria-invalid": invalid,
    };
  return {
    value, text, editing: current.editing, error: current.error, errors, conflict: current.conflict,
    inputProps: inputProps as FieldInputProps<TValue>,
    input: actions.input, commit: actions.commit, retry: actions.retry, reset: actions.reset,
  };
}

export interface BridgeConnectionHandle {
  /** The connection to the .NET host, as `onBridgeConnection` reports it. */
  readonly state: BridgeConnectionState;
}

// The state before the component subscribes: a subscription reports it at once.
function currentBridgeConnection(options: BridgeConnectionOptions): BridgeConnectionState {
  let current: BridgeConnectionState = "connecting";
  onBridgeConnection(state => { current = state; }, options)();
  return current;
}

/**
 * Renders the page's connection to its .NET host (`connecting`, `connected`, `reconnecting`
 * or `lost`) while the component is mounted, through `onBridgeConnection`. A changed
 * `lostAfter` subscribes again. The server snapshot is `connecting`.
 *
 * ```tsx
 * const connection = useBridgeConnection();
 * {connection.state === "lost" && <p role="alert">The application stopped responding.</p>}
 * ```
 */
export function useBridgeConnection(options: BridgeConnectionOptions = {}): BridgeConnectionHandle {
  const { lostAfter } = options;
  const [store] = useState(() => ({ state: undefined as BridgeConnectionState | undefined }));
  const subscribe = useCallback((onChange: () => void) => onBridgeConnection(state => {
    store.state = state;
    onChange();
  }, lostAfter === undefined ? {} : { lostAfter }), [store, lostAfter]);
  const read = useCallback(() => store.state ??= currentBridgeConnection(lostAfter === undefined ? {} : { lostAfter }), [store, lostAfter]);
  const state = useSyncExternalStore(subscribe, read, serverConnectionState);
  return { state };
}

const serverConnectionState = (): BridgeConnectionState => "connecting";

export { retrySuspenseView, useSuspenseView, type SuspenseViewHandle, type SuspenseViewOptions } from "./suspense.js";
export { useCollectionViewport, type CollectionViewportHandle } from "./viewport.js";
export { onBridgeConnection, type BridgeConnectionOptions, type BridgeConnectionState } from "@runic-artifex/views";
export {
  FieldError,
  type BridgeField,
  type BridgeFieldKind,
  type FieldBaseline,
  type FieldDefinition,
  type FieldErrorKind,
  type FieldOptions,
  type FieldState,
} from "@runic-artifex/views";
export {
  useMenuBar,
  type MenuBarEntry,
  type MenuBarHandle,
  type MenuBarInvokeResult,
  type MenuBarItem,
  type MenuBarMenu,
  type MenuBarRequest,
  type MenuBarSeparator,
  type MenuBarState,
  type UseMenuBarOptions,
} from "./menu-bar.js";
