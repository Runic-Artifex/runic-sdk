# `@runic-artifex/mvvm-react`

React 18.3/19 bindings for the public `@runic-artifex/mvvm` projection.
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
  const todo = useTodoBindings(contract); // generated aggregate hook
  return (
    <button
      disabled={!todo.submit.canExecute || todo.submit.isRunning}
      onClick={() => void todo.submit.execute().completion}
    >
      Submit {todo.amount}
    </button>
  );
}
```

Numeric member identifiers remain supported for dynamic scenarios.
`useMvvmCommandFacade` is also available directly and exposes the last result,
error, cancellation request, and monotonically ordered lifecycle transition.

Ownership is explicit at both boundaries:

- A store only disposes its projection when created with `ownsProjection: true`.
- A provider only disposes its store when rendered with `ownsStore`.

Both defaults are `false`, allowing a projection or store to be shared safely.
