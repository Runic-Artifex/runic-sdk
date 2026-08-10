# `@runic-artifex/svelte`

Svelte 5-only projection and lifecycle support for a Runic Toolkit Application
Bridge controller.

## Runic Translations locale state

Translation support is isolated in `@runic-artifex/svelte/translations`. It
consumes the structural `LocaleSource` emitted by Runic Translations and does
not make the generated catalog a runtime dependency.

Create one typed context in an application module:

```ts
// src/lib/locale-context.ts
import { createLocaleContext } from "@runic-artifex/svelte/translations";

export type AppLocale = "en" | "de";
export const localeContext = createLocaleContext<AppLocale>();
```

Create one locale source per browser root or SSR component tree and provide it
from the root layout. `createLocaleSource` is generated for the catalog:

```svelte
<script lang="ts">
  import { createLocaleSource } from "virtual:runic-translations/app/runtime";
  import { localeContext } from "$lib/locale-context";

  let { data, children } = $props();
  const source = createLocaleSource({ initialLocale: data.locale });
  localeContext.provide(source);
</script>

{@render children()}
```

Message options read reactive rune state, so a source notification invalidates
the rendered call without a reload:

```svelte
<script lang="ts">
  import { m } from "virtual:runic-translations/app";
  import { localeContext } from "$lib/locale-context";

  const locale = localeContext.use();
</script>

<h1>{m["Common.Hello"]({ name: "Ada" }, locale.messageOptions)}</h1>
<button onclick={() => locale.setLocale("de")}>Deutsch</button>
```

`locale.setLocale` writes a mutable source by default. Pass `requestLocale`
when navigation or an application setting is authoritative instead. The state
refreshes from the source after that callback completes; the callback should
therefore update the source directly or complete the navigation that does so.

Providers subscribe during component initialization, including SSR, and
unsubscribe in `onDestroy`. Context makes each render request-scoped; no global
generated resolver is configured. Nested providers shadow their parent and
dispose only their own subscription. Pass `dispose: false` only when the caller
owns a longer-lived state. Hydration is deterministic when the client source is
initialized from the locale returned by the root server load.

Explicit `{ locale }` message options remain authoritative on the server. The
adapter deliberately has no cookie, URL, or router behavior; SvelteKit support
lives in `@runic-artifex/sveltekit/translations`.

## Application Bridge

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
