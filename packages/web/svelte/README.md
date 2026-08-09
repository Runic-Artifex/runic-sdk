# `@runic-artifex/svelte`

Svelte 5-only projection and lifecycle support for a Runic Toolkit Application
Bridge controller.

Create the typed context once in an application module, provide it in the root
layout/component, and consume the same context below that boundary. State uses
Svelte 5 runes and immutable snapshots use `$state.raw`.

The first mount starts the controller, initializes its authoritative snapshot,
and announces `uiReady` followed by `uiRendered`. Repeated starts do not repeat
those lifecycle messages, and unmount performs idempotent disposal.

The integration never parses the protocol and never creates a second Effect
runtime. It owns only Svelte lifecycle and projection concerns.

## Opt-in Effect workflows

Use `createEffectSvelteApplicationBridge` with an Effect-aware Application
Bridge controller when a UI workflow benefits from typed failures, composition,
structured concurrency, or interruption. The existing Promise methods remain
available for ordinary event handlers.

The enhanced bridge exposes:

- `effects`, containing the typed `initialize`, `dispatch`, `cancel`, reconnect,
  lifecycle, and event Stream programs;
- `run` and `runExit`, which execute a composed program in the controller's
  existing `ManagedRuntime`;
- `createAction`, which projects one latest-wins Fiber into Svelte-native
  `status`, `value`, `error`, `cause`, and `exit` state.

An action interrupts its previous invocation before starting another. Disposing
the root bridge interrupts every action it created. Fiber interruption only
stops the frontend workflow; backend operation cancellation remains the
explicit `cancel(operationId)` protocol operation.

```ts
import { Effect } from "effect";
import { createEffectSvelteApplicationBridge } from "@runic-artifex/svelte";

const bridge = createEffectSvelteApplicationBridge(controller);
const install = bridge.createAction((command, effects) =>
  effects.dispatch(command).pipe(
    Effect.retry({ times: 2 }),
  )
);

// Components can use install.running, install.value, and install.error.
void install.run({ _tag: "StartInstallation", selectionId });

// Simple calls stay simple.
await bridge.dispatch({ _tag: "Navigate", target: "Summary" });
```

Keep transient presentation state in Svelte runes. Put substantial Effect
programs in framework-neutral TypeScript modules and use actions only to project
their lifecycle into the component tree.

If the host uses Runic Flow, embed the relevant process state in the application's
named snapshot and project it with `reduce`. Flow remains backend-only; this
package neither imports Flow vocabulary nor creates a competing state runtime.
