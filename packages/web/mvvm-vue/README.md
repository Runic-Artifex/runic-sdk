# `@webuitoolkit/mvvm-vue`

Vue 3 bindings for the public `MvvmProjection` from `@webuitoolkit/mvvm`.
The adapter keeps protocol behavior in the framework-neutral SDK and exposes
each immutable projection snapshot through one shallow, read-only Vue ref.

```ts
import { computed } from "vue";
import { createMvvmProjection } from "@webuitoolkit/mvvm";
import {
  provideVueMvvm,
  useVueMvvmCommand,
  useVueMvvmProperty,
} from "@webuitoolkit/mvvm-vue";

// In a providing component's setup:
provideVueMvvm(createMvvmProjection(client), { ownsProjection: true });

// In a descendant's setup:
const amount = useVueMvvmProperty(contract.amount);
const submit = useVueMvvmCommandFacade(contract.submit);
const canSubmit = computed(() => submit.canExecute.value && !submit.isRunning.value);
await contract.amount.set(7);
const result = await submit.execute().completion;
```

`property`, `collection`, `command`, and `validation` return cached computed
refs. A projection state event replaces `state.value` before event subscribers
run, so all accessors observe the same accepted snapshot.

The corresponding `toVueMvvmProperty`, `toVueMvvmCollection`,
`toVueMvvmCommand`, and `toVueMvvmValidation` helpers accept an explicit
adapter when dependency injection is not appropriate.
Generated `use{Contract}Bindings` composables aggregate these refs and register
their command-facade cleanup with the active effect scope.

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
