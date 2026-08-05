# Frontend framework adapters

`@runic-artifex/mvvm` owns transport validation, reconnect behavior, snapshots,
patches, commands, and the immutable projection. The framework adapters expose
that projection through native framework primitives without creating another
state machine:

| Framework | Package | Primary primitive |
| --- | --- | --- |
| React | `@runic-artifex/mvvm-react` | external store, provider, hooks |
| Vue | `@runic-artifex/mvvm-vue` | shallow refs, computed refs, effect scopes |
| Svelte | `@runic-artifex/mvvm-svelte` | readable stores and Svelte 5 lifecycle |
| Angular | `@runic-artifex/mvvm-angular` | signals, providers, directives |

Each adapter declares its framework as a peer dependency and pins the matching
MVVM core package version. `npm run verify` builds and runs the core,
conformance, type-level, lifecycle, and adapter suites.

Application examples and framework build configuration live in
[`runic-toolkit-examples`](https://github.com/Runic-Artifex/runic-toolkit-examples).
