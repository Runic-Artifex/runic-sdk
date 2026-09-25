interface ConnectedPage<V, S> {
  readonly view: V | undefined;
  readonly state: S | undefined;
}

/** A small authoring experiment: typed Svelte function bindings and ordered writes. */
export function bridgeForm<V, S extends object>(
  page: ConnectedPage<V, S>,
  report: (error: unknown | undefined) => void,
) {
  let tail: Promise<void> = Promise.resolve();
  let failed: unknown | undefined;
  let active = true;

  return {
    field<K extends keyof S>(key: K, write: (view: V, value: S[K]) => Promise<unknown>) {
      let optimistic: S[K] | undefined;
      let pending = 0;
      return {
        get: (): S[K] => pending ? optimistic! : page.state![key],
        set: (value: S[K]): void => {
          optimistic = value;
          pending++;
          failed = undefined;
          tail = tail.then(async () => {
            try {
              if (!page.view) throw new Error("The view is disconnected.");
              await write(page.view, value);
            } catch (error) {
              failed = error;
              if (active) report(error);
            } finally {
              pending--;
            }
          });
        },
      };
    },
    async run(action: (view: V) => Promise<unknown>): Promise<void> {
      await tail;
      try {
        if (failed !== undefined) throw failed;
        if (!page.view) throw new Error("The view is disconnected.");
        await action(page.view);
        if (active) report(undefined);
      } catch (error) {
        if (active) report(error);
      }
    },
    dispose(): void { active = false; },
  };
}
