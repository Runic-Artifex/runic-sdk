# `@webuitoolkit/mvvm-svelte`

Svelte readable-store bindings for the frozen public projection exported by
`@webuitoolkit/mvvm`. The adapter does not decode protocol frames, duplicate
the client revision machine, or depend on another UI adapter.

```svelte
<script lang="ts">
  import {
    createSvelteMvvmStore,
    disposeSvelteMvvmStoreOnDestroy,
  } from "@webuitoolkit/mvvm-svelte";

  const model = createSvelteMvvmStore(projection);
  disposeSvelteMvvmStoreOnDestroy(model);

  const amountMember = 1;
  const submitMember = 2;
</script>

<input
  value={$model.properties.get(amountMember) ?? 0}
  on:change={(event) => model.setProperty(amountMember, Number(event.currentTarget.value))}
/>
<button
  disabled={!model.command(submitMember)?.canExecute}
  on:click={() => model.execute(submitMember)}
>
  Submit
</button>
```

The store subscribes to its projection only while it has Svelte subscribers.
Multiple components share that single upstream subscription. Unsubscribing the
last component detaches it immediately; a later subscriber receives the
projection's current immutable snapshot.

The store does not own the projection by default. Pass
`{ ownsProjection: true }` only when the store is the projection's sole owner;
then its idempotent `dispose()` also disposes the projection. The lifecycle
helper registers that disposal with Svelte's `onDestroy`. Tests and wrapper
components can supply an explicit registrar as the helper's second argument.

## Development

After installing repository workspace dependencies:

```sh
npm run build
npm test
npm pack --dry-run
```

The test suite covers atomic snapshot delivery, shared subscription lifetime,
member and operation passthrough, explicit ownership, idempotent disposal, the
shared `amount-submit-v1` vertical, and a strict consumer compile against the
generated declarations.
