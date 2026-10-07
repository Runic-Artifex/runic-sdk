import {
  defineComponent,
  getCurrentScope,
  h,
  onScopeDispose,
  shallowReactive,
  shallowRef,
  toValue,
  watch,
  type Component,
  type ComponentPublicInstance,
  type MaybeRefOrGetter,
  type Ref,
  type SetupContext,
  type VNodeChild,
} from "vue";
import {
  createCollectionViewportController,
  createCommandController,
  createViewController,
  viewSourceIdentity,
  type BridgeOutcomeFailure,
  type CollectionViewport,
  type CollectionViewportOptions,
  type ViewClient,
  type ViewReference,
  type ViewSource,
} from "@runic-artifex/views";

export type { CollectionViewport, CollectionViewportOptions, ViewClient, ViewConnector, ViewReference, ViewSource } from "@runic-artifex/views";

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
 * disposes the current one.
 *
 * Pass a getter such as `() => props.page` to follow a prop. A reference is
 * identified by its `connect` function, so a getter returning a new
 * `{ connect: connectWorkspace }` object does not reconnect.
 */
export function useView<TClient extends ViewClient>(source: MaybeRefOrGetter<ViewSource<TClient>>): ViewHandle<TClient> {
  const controller = createViewController<TClient>();
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
  if (getCurrentScope()) onScopeDispose(() => controller.dispose());
  return { state, client, error, pending, retry: () => controller.retry() };
}

/** A build-known map checks each generated reference kind against its component's `page` prop. */
export type ViewRegistry<R extends ViewReference> = {
  readonly [K in R["kind"]]: Component<{ page: Extract<R, { readonly kind: K }> }>;
};

export interface ViewOutletProps<R extends ViewReference> {
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
export const ViewOutlet = defineComponent(<R extends ViewReference>(props: ViewOutletProps<R>, { slots }: SetupContext) => (): VNodeChild => {
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
 * Tracks a command's pending state and failure. The result is a reactive
 * object, so templates read `increment.pending` directly; do not destructure
 * `pending`, `error` or `failure`.
 *
 * ```vue
 * const increment = useCommand(() => client.value?.increment());
 * <button :disabled="!client || increment.pending" @click="increment.run()">…</button>
 * ```
 */
export function useCommand<TArgs extends readonly unknown[], TResult>(
  command: (...args: TArgs) => TResult | PromiseLike<TResult>,
): CommandHandle<TArgs, Awaited<TResult>> {
  const controller = createCommandController(command);
  const handle = shallowReactive<CommandHandle<TArgs, Awaited<TResult>>>({
    run: controller.run, pending: false, error: undefined, failure: undefined, reset: controller.reset,
  });
  controller.subscribe(() => {
    const writable = handle as { pending: boolean; error: unknown; failure: unknown };
    writable.pending = controller.current.pending;
    writable.error = controller.current.error;
    writable.failure = controller.current.failure;
  });
  if (getCurrentScope()) onScopeDispose(() => controller.dispose());
  return handle;
}

export interface CollectionViewportHandle {
  /** The rows to request and the sizes to render. */
  readonly viewport: Readonly<Ref<CollectionViewport>>;
  /** A function ref for the scroll container: `<div :ref="attach">`. */
  attach(element: Element | ComponentPublicInstance | null): void;
}

/**
 * Follows a fixed-row-height scroll container and returns the rows to request
 * from .NET. Pass a getter such as `() => ({ totalCount: state.value?.totalCount ?? 0, rowHeight: 32 })`
 * and watch `viewport` to send `start` and `size` to the ViewModel.
 */
export function useCollectionViewport(options: MaybeRefOrGetter<CollectionViewportOptions>): CollectionViewportHandle {
  const controller = createCollectionViewportController(toValue(options));
  const viewport = shallowRef(controller.current);
  controller.subscribe(() => { viewport.value = controller.current; });
  watch(() => {
    const { totalCount, rowHeight, overscan } = toValue(options);
    return [totalCount, rowHeight, overscan] as const;
  }, ([totalCount, rowHeight, overscan]) => {
    controller.update({ totalCount, rowHeight, ...(overscan === undefined ? {} : { overscan }) });
  });
  if (getCurrentScope()) onScopeDispose(() => controller.dispose());
  return {
    viewport,
    attach(element) {
      controller.attach(typeof HTMLElement !== "undefined" && element instanceof HTMLElement ? element : null);
    },
  };
}
