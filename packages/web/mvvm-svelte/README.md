# `@runic-artifex/mvvm-svelte`

Svelte readable-store bindings for the frozen public projection exported by
`@runic-artifex/mvvm`. The adapter does not decode protocol frames, duplicate
the client revision machine, or depend on another UI adapter.

```svelte
<script lang="ts">
  import {
    createSvelteMvvmStore,
    createSvelteMvvmCommandFacade,
    derivedMvvmProperty,
    disposeSvelteMvvmStoreOnDestroy,
  } from "@runic-artifex/mvvm-svelte";

  const model = createSvelteMvvmStore(projection);
  disposeSvelteMvvmStoreOnDestroy(model);

  const amount = derivedMvvmProperty(model, contract.amount);
  const submit = createSvelteMvvmCommandFacade(model, contract.submit);
</script>

<input
  value={$amount ?? 0}
  on:change={(event) => contract.amount.set(Number(event.currentTarget.value))}
/>
<button
  disabled={!$submit?.canExecute}
  on:click={() => $submit.canExecute && submit.execute()}
>
  Submit
</button>
```

`derivedMvvmCollection` and `derivedMvvmValidation` provide the corresponding
typed readables for generated collection and validation handles.
Generated `create{Contract}Stores` functions group those readables and command
facades by contract member. Svelte 5 consumers can adapt any of them to a
rune-tracked getter with `toSvelteMvvmRune` from the `/runes` export; Svelte 4
continues to use ordinary `$store` syntax.

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
