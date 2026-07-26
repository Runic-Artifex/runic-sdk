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
  // Generated handles preserve the C# member types through the hooks.
  const amount = useMvvmProperty(contract.amount);
  const submit = useMvvmCommand(contract.submit);
  return (
    <button
      disabled={!submit?.canExecute || submit.isExecuting}
      onClick={() => void contract.submit.execute().completion}
    >
      Submit {amount}
    </button>
  );
}
```

Numeric member identifiers remain supported for dynamic scenarios.

Ownership is explicit at both boundaries:

- A store only disposes its projection when created with `ownsProjection: true`.
- A provider only disposes its store when rendered with `ownsStore`.

Both defaults are `false`, allowing a projection or store to be shared safely.
