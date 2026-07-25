# `@webuitoolkit/mvvm-vue`

Vue 3 bindings for the public `MvvmProjection` from `@webuitoolkit/mvvm`.
The adapter keeps protocol behavior in the framework-neutral SDK and exposes
each immutable projection snapshot through one shallow, read-only Vue ref.

```ts
import { computed } from "vue";
import { createMvvmProjection } from "@webuitoolkit/mvvm";
import { provideVueMvvm, useVueMvvm } from "@webuitoolkit/mvvm-vue";

// In a providing component's setup:
provideVueMvvm(createMvvmProjection(client), { ownsProjection: true });

// In a descendant's setup:
const mvvm = useVueMvvm();
const amount = mvvm.property(1);
const submit = mvvm.command(2);
const canSubmit = computed(() => submit.value?.canExecute === true);
await mvvm.setProperty(1, 7);
const result = await mvvm.execute(2).completion;
```

`property`, `collection`, `command`, and `validation` return cached computed
refs. A projection state event replaces `state.value` before event subscribers
run, so all accessors observe the same accepted snapshot.

## Ownership and cleanup

- `createVueMvvmAdapter` owns its subscription, but does not own the supplied
  projection unless `{ ownsProjection: true }` is explicit.
- `createScopedVueMvvmAdapter` registers adapter disposal with the active Vue
  effect scope.
- `provideVueMvvm` creates, provides, and scope-owns an adapter; component
  unmount therefore removes the projection subscription.
- `provideVueMvvmAdapter` provides an existing caller-owned adapter and never
  disposes it.

Adapter disposal is idempotent. Calls that begin after disposal fail rather
than silently issuing protocol operations.
