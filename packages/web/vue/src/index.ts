import {
  computed,
  defineComponent,
  h,
  reactive,
  shallowRef,
  toValue,
  watch,
  type Component,
  type MaybeRefOrGetter,
  type Ref,
  type SetupContext,
  type VNodeChild,
  type WritableComputedRef,
} from "vue";
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
  type FieldBaseline,
  type FieldDefinition,
  type FieldOptions,
  type LatestOperationController,
  type LatestOperationState,
  type OperationController,
  type OperationControllerOptions,
  type OperationState,
  type PageReference,
  type ViewClient,
  type ViewControllerOptions,
  type ViewSource,
} from "@runic-artifex/views";
import { viewSourceIdentity } from "@runic-artifex/views/bindings";
import { releaseWithScope } from "./scope.js";

export interface ViewHandle<TClient extends ViewClient> {
  /** The latest state, or undefined until the client connects. */
  readonly state: Readonly<Ref<TClient["snapshot"] | undefined>>;
  /** The connected client for commands, or undefined until it connects. */
  readonly client: Readonly<Ref<TClient | undefined>>;
  /** The connection failure, if any. */
  readonly error: Readonly<Ref<unknown>>;
  /** True while a reference is connecting. */
  readonly pending: Readonly<Ref<boolean>>;
  /** Connects a reference again after a failure. */
  retry(): void;
}

/**
 * Connects a generated View reference while the current effect scope (for
 * example a component) is active and exposes its state as shallow refs. A
 * changed reference disposes the previous client; disposing the scope
 * disposes the current one. Call it in `setup()` or `effectScope().run()`:
 * outside an effect scope the client is never released (development builds warn).
 *
 * Pass a getter such as `() => props.page` to follow a prop. A reference is
 * identified by its `connect` function, so a getter returning a new
 * `{ connect: connectWorkspace }` object does not reconnect.
 */
export interface UseViewOptions<TClient extends ViewClient> {
  /** Releases a client this composable connected. Defaults to `client.dispose()`. */
  readonly release?: (client: TClient) => void;
  /**
   * Milliseconds a client this composable connected stays connected after the component unmounts or the source changes,
   * for the next composable that follows the same reference (StrictMode, HMR, `<Activity>`, A→B→A navigation). `0`
   * disposes it at once. Defaults to 2000. Read once, when the composable is created.
   */
  readonly keepAlive?: number;
}

function controllerOptions<TClient extends ViewClient>(options: UseViewOptions<TClient>): ViewControllerOptions<TClient> {
  return { ...(options.release ? { release: options.release } : {}), ...(options.keepAlive === undefined ? {} : { keepAlive: options.keepAlive }) };
}

export function useView<TClient extends ViewClient>(source: MaybeRefOrGetter<ViewSource<TClient>>,
  options: UseViewOptions<TClient> = {}): ViewHandle<TClient> {
  const controller = createViewController<TClient>(controllerOptions(options));
  const state = shallowRef<TClient["snapshot"] | undefined>();
  const client = shallowRef<TClient | undefined>();
  const error = shallowRef<unknown>();
  const pending = shallowRef(false);
  const sync = () => {
    const current = controller.current;
    state.value = current.state;
    client.value = current.client;
    error.value = current.error;
    pending.value = current.pending;
  };
  controller.subscribe(sync);
  watch(() => viewSourceIdentity(toValue(source)), () => {
    controller.setSource(toValue(source));
    sync();
  }, { immediate: true });
  releaseWithScope("useView", () => controller.dispose());
  return { state, client, error, pending, retry: () => controller.retry() };
}

/**
 * A build-known map checks each generated reference kind against its component's `page` prop. A nullable
 * reference, such as a region slot's `State["main"]`, checks its non-null kinds.
 */
export type ViewRegistry<R extends PageReference | null | undefined> = {
  readonly [K in NonNullable<R>["kind"]]: Component<{ page: Extract<NonNullable<R>, { readonly kind: K }> }>;
};

export interface ViewOutletProps<R extends PageReference> {
  /** The presented reference, such as `state.main`. */
  content: R | null | undefined;
  registry: ViewRegistry<R>;
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
 * component. The default slot renders while there is no content. A kind without
 * a component renders the `missing` slot, which receives `{ content }`, for example
 * for localized text, or an English alert without it:
 *
 * ```vue
 * <ViewOutlet :content="state?.main" :registry="pages">
 *   <template #missing="{ content }"><p role="alert">{{ t("missingView", content.kind) }}</p></template>
 * </ViewOutlet>
 * ```
 */
export const ViewOutlet = defineComponent(<R extends PageReference>(props: ViewOutletProps<R>, { slots }: SetupContext) => (): VNodeChild => {
  const content = props.content;
  if (!content) return slots.default?.();
  const component = props.registry[content.kind as R["kind"]] as Component<{ page: R }> | undefined;
  if (!component) {
    if (slots["missing"]) return slots["missing"]({ content });
    return h("p", { role: "alert" }, `No web component is registered for ${content.kind}.`);
  }
  return h(component, { key: referenceKey(content), page: content });
}, { name: "ViewOutlet", props: ["content", "registry"] });

export interface CommandHandle<TArgs extends readonly unknown[], TResult, TFailure = BridgeOutcomeFailure<TResult>> {
  /**
   * Runs the command. Resolves to its result, including a `BridgeOutcome` with a
   * declared failure, which `failure` then holds, or to undefined after an
   * unexpected failure, which `error` then holds. Never rejects.
   */
  run(...args: TArgs): Promise<TResult | undefined>;
  /** True while a run is in flight. */
  readonly pending: Readonly<Ref<boolean>>;
  /** Why the latest run failed unexpectedly, until the next run starts. */
  readonly error: Readonly<Ref<unknown>>;
  /**
   * The declared failure of the latest run, for a command that resolves a
   * `BridgeOutcome`, until the next run starts. A superseded run sets neither.
   */
  readonly failure: Readonly<Ref<TFailure | undefined>>;
  /** Clears `error` and `failure`. */
  reset(): void;
}

/**
 * Tracks a command's pending state and failure as shallow refs, like `useView`,
 * so the handle can be destructured. Call it in `setup()` or `effectScope().run()`
 * (development builds warn otherwise).
 *
 * ```vue
 * const { run: increment, pending } = useCommand(() => client.value?.increment());
 * <button :disabled="!client || pending" @click="increment()">…</button>
 * ```
 */
export function useCommand<TArgs extends readonly unknown[], TReturn>(
  command: (...args: TArgs) => TReturn,
): CommandHandle<TArgs, Awaited<TReturn>> {
  const controller = createCommandController(command);
  const pending = shallowRef(controller.current.pending);
  const error = shallowRef<unknown>(controller.current.error);
  type Failure = BridgeOutcomeFailure<Awaited<TReturn>>;
  // A conditional failure type does not distribute through shallowRef's overloads.
  const failure = shallowRef(controller.current.failure) as Ref<Failure | undefined>;
  controller.subscribe(() => {
    pending.value = controller.current.pending;
    error.value = controller.current.error;
    failure.value = controller.current.failure;
  });
  releaseWithScope("useCommand", () => controller.dispose());
  return { run: controller.run, pending, error, failure, reset: controller.reset };
}

/** Each {@link OperationState} field as a shallow ref, with the controller's `run`, `cancel` and `reset`. */
export type OperationHandle<TArgs extends readonly unknown[], TResult, TFailure = never> = {
  readonly [K in keyof OperationState<TResult, TFailure>]-?: Readonly<Ref<OperationState<TResult, TFailure>[K]>>;
} & Pick<OperationController<TArgs, TResult, TFailure>, "run" | "cancel" | "reset">;

/**
 * Tracks Start admission, pending work, cancellation and terminal feedback of a
 * generated `start<Command>()` operation, each state field a shallow ref, so the
 * handle can be destructured. A run that ends `cancelled` sets `cancelled`, not `error`.
 * Scope disposal settles run promises without cancelling accepted work. Call it in
 * `setup()` or `effectScope().run()`: outside an effect scope it is never released
 * (development builds warn).
 *
 * ```vue
 * const { run: push, pending, cancelled } = useOperation((remote: string) => client.value?.startPush(remote));
 * <button :disabled="pending" @click="push('origin')">Push</button>
 * <p v-if="cancelled" role="status">Push cancelled.</p>
 * ```
 */
export function useOperation<TArgs extends readonly unknown[], TResult, TFailure = never>(
  start: (...args: TArgs) => BridgeOperation<TResult, TFailure> | undefined | PromiseLike<BridgeOperation<TResult, TFailure> | undefined>,
  options: OperationControllerOptions<TResult, TFailure> = {},
): OperationHandle<TArgs, TResult, TFailure> {
  const controller = createOperationController(start, options);
  type State = OperationState<TResult, TFailure>;
  const refs = {} as { [K in keyof State]-?: Ref<State[K]> };
  const keys = Object.keys(controller.current) as (keyof State)[];
  for (const key of keys) (refs as Record<keyof State, Ref<unknown>>)[key] = shallowRef(controller.current[key]);
  controller.subscribe(() => {
    const current = controller.current;
    for (const key of keys) (refs as Record<keyof State, Ref<unknown>>)[key].value = current[key];
  });
  releaseWithScope("useOperation", () => controller.dispose());
  return { ...refs, run: controller.run, cancel: controller.cancel, reset: controller.reset } as OperationHandle<TArgs, TResult, TFailure>;
}

/** Each {@link LatestOperationState} field as a shallow ref, with the controller's `run` and `clear`. */
export type LatestOperationHandle<TResult, TFailure = never> = {
  readonly [K in keyof LatestOperationState<TResult, TFailure>]-?: Readonly<Ref<LatestOperationState<TResult, TFailure>[K]>>;
} & Pick<LatestOperationController<TResult, TFailure>, "run" | "clear">;

/**
 * Admits only the newest selection, for reads such as "load the selected item": each
 * `run(intent)` replaces the queued intent and cancels the active read, and the next read
 * starts only after the previous one has finished (including `waitForCompletion`). Each
 * state field is a shallow ref, so the handle can be destructured. `blocked` means
 * admission is uncertain; only `clear("replace")`, after the application has established a
 * fresh session, clears it. Scope disposal settles run promises without cancelling accepted
 * work. Call it in `setup()` or `effectScope().run()`: outside an effect scope it is never
 * released (development builds warn).
 *
 * ```vue
 * const load = useLatestOperation<Item>();
 * <li @click="load.run({ start: () => client.value?.startLoad(id) })">…</li>
 * <ItemView v-if="load.outcome.value?.ok" :item="load.outcome.value.value" />
 * ```
 */
export function useLatestOperation<TResult = void, TFailure = never>(): LatestOperationHandle<TResult, TFailure> {
  const controller = createLatestOperationController<TResult, TFailure>();
  type State = LatestOperationState<TResult, TFailure>;
  const refs = {} as { [K in keyof State]-?: Ref<State[K]> };
  const keys = Object.keys(controller.current) as (keyof State)[];
  for (const key of keys) (refs as Record<keyof State, Ref<unknown>>)[key] = shallowRef(controller.current[key]);
  controller.subscribe(() => {
    const current = controller.current;
    for (const key of keys) (refs as Record<keyof State, Ref<unknown>>)[key].value = current[key];
  });
  releaseWithScope("useLatestOperation", () => controller.dispose());
  return { ...refs, run: controller.run, clear: controller.clear } as LatestOperationHandle<TResult, TFailure>;
}

export interface TextFieldHandle {
  /**
   * What the input shows: the draft while one exists, otherwise the member's value in the View's state. Setting it
   * is an input by the user, as `v-model` does; `v-model` also writes nothing during IME composition.
   */
  readonly value: WritableComputedRef<string>;
  /**
   * True while a draft exists, from the first input until the last write settled and the state caught up, and after a
   * failed write. Keep the input enabled while it is true, rather than disabling it on the View's busy state, which
   * the field's own write causes.
   */
  readonly editing: Readonly<Ref<boolean>>;
  /** Why the latest write failed, until the next input or `reset()`. */
  readonly error: Readonly<Ref<unknown>>;
  /** Drops the draft and the error and shows the View's value. Writes in flight still complete. */
  reset(): void;
}

export interface UseTextFieldOptions {
  /**
   * With `true` (the default) at most one write is in flight, and text typed meanwhile is written next, as one
   * value. With `false` every input is written.
   */
  readonly coalesce?: boolean;
  /** With `true` (the default) a failed write keeps the draft and sets `error`; with `false` the field shows the View's value again. */
  readonly keepDraftOnError?: boolean;
}

/** The View a text field binds to: a {@link useView} handle, or any state and client refs. */
export interface TextFieldView<TClient extends ViewClient> {
  readonly state: Readonly<Ref<TClient["snapshot"] | undefined>>;
  readonly client: Readonly<Ref<TClient | undefined>>;
}

/**
 * Binds a text input to one member of a View while the current effect scope is active, through
 * `createTextFieldController`. Typing key by key never loses characters: while its writes are in flight the field
 * shows what the user typed, at most one write is in flight (newer text is written next), and once the last write
 * settled it shows the View's value, including any normalization. Each field is a ref, so the handle can be
 * destructured. Call it in `setup()` or `effectScope().run()`: outside an effect scope it is never released
 * (development builds warn).
 *
 * ```vue
 * const person = useView(() => props.page);
 * const { value: name, editing } = useTextField(person, state => state.name, (client, value) => client.setName(value));
 * <input v-model="name">
 * ```
 *
 * `write` may be a generated setter, a command or a checked write. Its failure, and a checked write's `rejected` or
 * `conflict` receipt, keep the draft and set `error` (a receipt's is a `FieldError`). For a generated member, prefer
 * `useField` with its `{View}Fields` descriptor.
 */
export function useTextField<TClient extends ViewClient>(
  view: TextFieldView<TClient>,
  read: (state: TClient["snapshot"]) => string,
  write: (client: TClient, value: string) => Promise<unknown>,
  options: UseTextFieldOptions = {},
): TextFieldHandle {
  const controller = createTextFieldController<TClient["snapshot"], TClient>({ read, write, ...options });
  const current = shallowRef(controller.current);
  controller.subscribe(() => { current.value = controller.current; });
  watch([view.state, view.client], ([state, client]) => {
    controller.setSource(state === undefined || client === undefined ? undefined : { state, client });
    current.value = controller.current;
  }, { immediate: true });
  releaseWithScope("useTextField", () => controller.dispose());
  return {
    value: computed({ get: () => current.value.value, set: text => controller.input(text) }),
    editing: computed(() => current.value.editing),
    error: computed(() => current.value.error),
    reset: () => controller.reset(),
  };
}

/**
 * A field bound by {@link useField}: a `reactive` object, so a template binds its members directly, such as
 * `v-model="title.value"`.
 */
export interface FieldHandle<TValue> {
  /**
   * The draft while one exists, otherwise the member's value in the View's state. Setting it is an input by the user,
   * as `v-model` does: a string set on a field that is not text (such as from `<input type="number">` while its text
   * does not parse) is the field's text, as `text` is.
   */
  value: TValue;
  /**
   * What a text input shows: the text typed while it does not parse or still means `value` (so `1.` stays while typing
   * `1.5`), otherwise `value` formatted. Setting it is an input of text, parsed for the field's kind: bind number
   * inputs with `v-model="step.text"`. The number `v-model` sets on `<input type="number">` once its text is one is
   * an input of that value.
   */
  text: string;
  /**
   * True while a draft exists, from the first input until the last write settled and the state caught up, and after a
   * failed write. Keep the input enabled while it is true, rather than disabling it on the View's busy state.
   */
  readonly editing: boolean;
  /** Why the latest write or text failed (a `BridgeError` or a {@link FieldError}), until the next input, `retry()` or `reset()`. */
  readonly error: unknown;
  /** The member's validation messages in the View's state, or of the last checked write's receipt; `[]` when valid. */
  readonly errors: readonly string[];
  /** The producer's field while a checked write's `conflict` keeps the draft; `undefined` otherwise. */
  readonly conflict: FieldBaseline<TValue> | undefined;
  /** The user changed the value. */
  input(value: TValue): void;
  /**
   * The user is done with the field (`@blur="title.commit"`): with `commit: "pause"` the draft is written now.
   * Resolves once the field's writes settled, so a command that depends on the field can run after it:
   * `await title.commit(); await editor.save()`. Never rejects; a failure is `error`.
   */
  commit(): Promise<void>;
  /** Writes the draft again after a failed write; after a `conflict`, over the producer's value. */
  retry(): void;
  /** Drops the draft, the error and a conflict and shows the View's value. Writes in flight still complete. */
  reset(): void;
}

/**
 * Binds an element to one View member while the current effect scope is active, through `createFieldController`:
 * pass a generated descriptor such as `EditorFields.title`, or any definition with `read` and `write`. The handle is
 * `reactive`; bind `v-model` to its `value` (`text` for number inputs) and call `commit` on blur. `v-model` already
 * writes nothing while an IME composition is in progress. `options` are read once, when the field is created. Call
 * it in `setup()` or `effectScope().run()`: outside an effect scope it is never released (development builds warn).
 *
 * ```vue
 * const editor = useView(() => props.page);
 * const title = useField(editor, EditorFields.title, { commit: "pause", checked: true });
 * <input v-model="title.value" @blur="title.commit" :aria-invalid="title.errors.length > 0 || title.error !== undefined">
 * <input type="number" v-model="step.text" @blur="step.commit">
 * <input type="checkbox" v-model="done.value">
 * ```
 *
 * With `checked: true` a `rejected` or `conflict` receipt keeps the draft and sets `error` (a `FieldError`), and a
 * conflict holds further writes until `retry()` or `reset()`. Leaving the scope writes a value that waits to be
 * written.
 */
export function useField<TClient extends ViewClient, TValue>(
  view: TextFieldView<TClient>,
  field: BridgeField<NoInfer<TClient>, NoInfer<TClient>["snapshot"], TValue> | FieldDefinition<NoInfer<TClient>, NoInfer<TClient>["snapshot"], TValue>,
  options: FieldOptions = {},
): FieldHandle<TValue> {
  const controller = createFieldController<TClient["snapshot"], TClient, TValue>(field, options);
  const current = shallowRef(controller.current);
  controller.subscribe(() => { current.value = controller.current; });
  watch([view.state, view.client], ([state, client]) => {
    controller.setSource(state === undefined || client === undefined ? undefined : { state, client });
    current.value = controller.current;
  }, { immediate: true });
  releaseWithScope("useField", () => controller.dispose());
  const kind = controller.field.kind;
  // `v-model` on `<input type="number">` sets a string while its text is not a number.
  const textual = kind === undefined || kind === "text";
  return reactive({
    value: computed({
      get: () => current.value.value,
      set: (value: TValue) => {
        if (typeof value === "string" && !textual) controller.inputText(value);
        else controller.input(value);
      },
    }),
    // `v-model` on `<input type="number">` sets a number once its text is one.
    text: computed({
      get: () => current.value.text,
      set: (text: string) => { if (typeof text === "string") controller.inputText(text); else controller.input(text as TValue); },
    }),
    editing: computed(() => current.value.editing),
    error: computed(() => current.value.error),
    errors: computed(() => current.value.errors),
    conflict: computed(() => current.value.conflict),
    input: (value: TValue) => controller.input(value),
    commit: async () => { await controller.commit(); },
    retry: () => controller.retry(),
    reset: () => controller.reset(),
  }) as FieldHandle<TValue>;
}

export interface BridgeConnectionHandle {
  /** The connection to the .NET host, as `onBridgeConnection` reports it. */
  readonly state: Readonly<Ref<BridgeConnectionState>>;
}

/**
 * Follows the page's connection to its .NET host (`connecting`, `connected`, `reconnecting`
 * or `lost`) as a shallow ref through `onBridgeConnection`, until the current effect scope is
 * disposed. Call it in `setup()` or `effectScope().run()`: outside an effect scope it is never
 * released (development builds warn).
 *
 * ```vue
 * const { state: connection } = useBridgeConnection();
 * <p v-if="connection === 'lost'" role="alert">The application stopped responding.</p>
 * ```
 */
export function useBridgeConnection(options: BridgeConnectionOptions = {}): BridgeConnectionHandle {
  const state = shallowRef<BridgeConnectionState>("connecting");
  const stop = onBridgeConnection(next => { state.value = next; }, options);
  releaseWithScope("useBridgeConnection", stop);
  return { state };
}

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
