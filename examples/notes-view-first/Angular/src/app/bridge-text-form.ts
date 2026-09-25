import { effect, signal, type Signal } from "@angular/core";
import { FormControl, FormGroup } from "@angular/forms";
import { WindowOperations } from "./window-operations";

interface ConnectedView<S> {
  readonly snapshot: S;
  subscribe(listener: (state: S) => void): () => void;
  dispose(): void;
}
type StringKeys<S> = { [K in keyof S]-?: S[K] extends string ? K : never }[keyof S] & string;
type TextControls<K extends string> = { [P in K]: FormControl<string> };
type ViewSnapshot<V> = V extends { readonly snapshot: infer S } ? S : never;

/** App-local form adapter for text properties. The field map is generated in the target design. */
export function bridgeTextForm<V extends ConnectedView<any>, K extends StringKeys<ViewSnapshot<V>>>(
  source: Signal<V | undefined>,
  fields: Record<K, (view: V, value: string) => Promise<unknown>>,
  operations: WindowOperations,
) {
  const form = signal<FormGroup<TextControls<K>> | undefined>(undefined);
  const error = signal<unknown>(undefined);

  effect(onCleanup => {
    const view = source();
    if (!view) { form.set(undefined); return; }
    const names = Object.keys(fields) as K[];
    const controls = Object.fromEntries(names.map(key => [key,
      new FormControl(view.snapshot[key] as string, { nonNullable: true }),
    ])) as TextControls<K>;
    const group = new FormGroup(controls);
    const pending = new Map<K, number>();
    let active = true;

    const changes = names.map(key => controls[key].valueChanges.subscribe(value => {
      pending.set(key, (pending.get(key) ?? 0) + 1);
      let succeeded = false;
      void operations.run(view, target => fields[key](target, value))
        .then(() => { succeeded = true; if (active) error.set(undefined); })
        .catch(cause => { if (active) error.set(cause); })
        .finally(() => {
          const remaining = (pending.get(key) ?? 1) - 1;
          pending.set(key, remaining);
          if (!active || !succeeded || remaining > 0) return;
          const remote = view.snapshot[key] as string;
          if (controls[key].value !== remote) controls[key].setValue(remote, { emitEvent: false });
        });
    }));
    const unsubscribe = view.subscribe(snapshot => {
      for (const key of names) {
        if ((pending.get(key) ?? 0) > 0) continue;
        const remote = snapshot[key] as string;
        if (controls[key].value !== remote) controls[key].setValue(remote, { emitEvent: false });
      }
    });
    form.set(group);
    onCleanup(() => {
      active = false;
      for (const subscription of changes) subscription.unsubscribe();
      unsubscribe();
      form.set(undefined);
    });
  });

  return { form: form.asReadonly(), error: error.asReadonly() };
}
