import {
  defineComponent,
  h,
  shallowRef,
  toValue,
  watch,
  type Component,
  type MaybeRefOrGetter,
  type Ref,
  type SetupContext,
  type VNodeChild,
} from "vue";
import {
  createCommandController,
  createLatestOperationController,
  createOperationController,
  createViewController,
  type BridgeOperation,
  type BridgeOutcomeFailure,
  type LatestOperationController,
  type LatestOperationState,
  type OperationController,
  type OperationControllerOptions,
  type OperationState,
  type PageReference,
  type ViewClient,
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
}

export function useView<TClient extends ViewClient>(source: MaybeRefOrGetter<ViewSource<TClient>>,
  options: UseViewOptions<TClient> = {}): ViewHandle<TClient> {
  const controller = createViewController<TClient>(options.release ? { release: options.release } : {});
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
 * component. A kind without a component renders an alert; the default slot
 * renders while there is no content.
 */
export const ViewOutlet = defineComponent(<R extends PageReference>(props: ViewOutletProps<R>, { slots }: SetupContext) => (): VNodeChild => {
  const content = props.content;
  if (!content) return slots.default?.();
  const component = props.registry[content.kind as R["kind"]] as Component<{ page: R }> | undefined;
  if (!component) return h("p", { role: "alert" }, `No web component is registered for ${content.kind}.`);
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
