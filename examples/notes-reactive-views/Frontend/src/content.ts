// Experimental browser-side content outlet. Framework adapters can call this
// with their own mount/unmount functions; it does not own a DOM rendering model.
export interface Readable<S> {
  readonly snapshot: S;
  subscribe(listener: (state: S) => void): () => void;
}

export interface DisposableView {
  dispose(): void;
}

export interface PageReference<K extends string, V extends DisposableView> {
  readonly kind: K;
  connect(): Promise<V>;
}

type ContentKey<S> = {
  [K in keyof S]-?: NonNullable<S[K]> extends PageReference<string, DisposableView> ? K : never;
}[keyof S];

type ViewFor<R> = R extends PageReference<string, infer V> ? V : never;

export type ViewTemplates<R extends PageReference<string, DisposableView>> = {
  [K in R["kind"]]: (
    host: HTMLElement,
    view: ViewFor<Extract<R, { readonly kind: K }>>,
  ) => () => void;
};

/**
 * Mounts the view selected by a ViewModel-valued content property. The source
 * must reuse one PageReference object for repeat snapshots of the same .NET
 * instance. A template cleanup must dispose its connection and subscriptions.
 */
export function mountContent<S, K extends ContentKey<S>>(
  host: HTMLElement,
  source: Readable<S>,
  property: K,
  templates: ViewTemplates<Extract<NonNullable<S[NoInfer<K>]>, PageReference<string, DisposableView>>>,
  onError: (error: unknown) => void = error => { throw error; },
): () => void {
  let disposed = false;
  let generation = 0;
  let current: PageReference<string, DisposableView> | null = null;
  let cleanup: (() => void) | undefined;
  const unmount = () => {
    cleanup?.();
    cleanup = undefined;
    host.replaceChildren();
  };
  const unsubscribe = source.subscribe(state => {
    const next = state[property] as PageReference<string, DisposableView> | null;
    if (next === current) return;
    current = next;
    const version = ++generation;
    unmount();
    if (next === null) return;
    void next.connect().then(view => {
      if (disposed || version !== generation) {
        view.dispose();
        return;
      }
      const template = (templates as unknown as Record<string, (target: HTMLElement, value: DisposableView) => () => void>)[next.kind];
      if (!template) {
        view.dispose();
        throw new Error(`No content template for ${next.kind}.`);
      }
      try { cleanup = template(host, view); }
      catch (error) { view.dispose(); throw error; }
    }).catch(error => { if (!disposed && version === generation) onError(error); });
  });
  return () => {
    if (disposed) return;
    disposed = true;
    generation++;
    unsubscribe();
    unmount();
  };
}
