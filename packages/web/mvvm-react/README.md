# `@webuitoolkit/mvvm-react`

React 18.3/19 bindings for the public `@webuitoolkit/mvvm` projection.
The adapter uses `useSyncExternalStore`; it does not reinterpret protocol
members or introduce a second state model.

```tsx
const projection = createMvvmProjection(client);
const store = createReactMvvmStore(projection, { ownsProjection: true });

root.render(
  <ReactMvvmProvider store={store} ownsStore>
    <AmountEditor />
  </ReactMvvmProvider>,
);

function AmountEditor() {
  const store = useReactMvvmStore();
  const amount = useMvvmProperty(1);
  const submit = useMvvmCommand(2);
  return (
    <button
      disabled={!submit?.canExecute || submit.isExecuting}
      onClick={() => void store.execute(2).completion}
    >
      Submit {String(amount)}
    </button>
  );
}
```

Ownership is explicit at both boundaries:

- A store only disposes its projection when created with `ownsProjection: true`.
- A provider only disposes its store when rendered with `ownsStore`.

Both defaults are `false`, allowing a projection or store to be shared safely.
