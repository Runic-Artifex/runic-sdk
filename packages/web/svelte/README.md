# `@runic-artifex/svelte`

Make one Runic Toolkit Application Bridge controller feel native in a Svelte 5
application. This package owns Svelte lifecycle, context, and rune projection
while the controller remains authoritative for its snapshots, transport,
commands, reconnect, and events.

## Install

```sh
npm install @runic-artifex/application-bridge@preview @runic-artifex/svelte@preview
```

`@runic-artifex/svelte` is a public npm preview. The `preview` tag follows the
current preview release without pinning a stale version.

| Requirement | Supported range |
|---|---|
| Svelte | `>=5.46.4 <6` |
| Effect | `>=3.22.1 <4` |
| Runic Toolkit Application Bridge | `>=0.1.0-preview.30.1 <1` (optional peer; required by the root and `/bridge` entries) |
| Runic Vite integration | `>=0.2.0-preview.1 <1` (optional peer; required only by `/vite`) |

Svelte 4 and legacy stores are not supported.

### Public contract boundary

This adapter follows the released Application Bridge controller: failures are
the generated `BridgeError` union and Effect programs use its owned service.
It does not retain adapter-local failure or runtime generics. The released
local host boundary is the structural Application Bridge `FrameChannel`
contract; Svelte neither creates a frame channel nor owns transport,
reconnect, protocol, revision, or controller lifetime. The optional `/vite`
entry covers the released Vite resource lifecycle; authentication, remote
service transport, deployment, SSR, hydration, and rollout remain outside this
adapter.

## Use an Application Bridge in Svelte 5

`@runic-artifex/svelte/translations` is deliberately independent of the
Application Bridge peer, so translation-only apps can install and render it
without a bridge package. The package root and the additive
`@runic-artifex/svelte/bridge` entry point load bridge support; if the optional
peer is absent, they report the exact `npm install
@runic-artifex/application-bridge@preview` remediation.

Use `@runic-artifex/svelte/vite` only when registering Runic Vite
DevTools. It supplies the typed observer and HMR resource helpers without
making the root bridge entry depend on Vite.

Create the controller with `@runic-artifex/application-bridge`, then create one
Svelte projection and one typed context for the component tree. `provide()`
starts the bridge after the first browser mount by default; it initializes the
snapshot, then calls `uiReady()` and `uiRendered()`. It disposes the bridge on
unmount by default.

For a Runic Desktop application, compose the transport before constructing the
Svelte projection. The generated contract is unchanged:

```ts
import { createDesktopFrameChannel } from "@runic-artifex/desktop";
import {
  ApplicationBridgeLive,
  createApplicationBridgeController,
} from "@runic-artifex/application-bridge";
import { SetupContract } from "./generated/setup-contract";

export const controller = createApplicationBridgeController(
  SetupContract,
  ApplicationBridgeLive(SetupContract, createDesktopFrameChannel()),
);
```

The controller's Effect scope connects and closes the Desktop channel. Svelte
mounting owns only its subscription and projection; dispose the controller once
at the application-composition boundary after the Svelte root is unmounted.

```ts
// src/lib/setup-bridge.svelte.ts
import {
  createApplicationBridgeContext,
  createSvelteApplicationBridge,
} from "@runic-artifex/svelte";
import { controller } from "./setup-controller";

export const setupBridge = createSvelteApplicationBridge(controller, {
  reduce: (snapshot, event) =>
    event._tag === "SnapshotReplaced" ? event.snapshot : snapshot,
});

export const setupBridgeContext = createApplicationBridgeContext();
```

```svelte
<!-- src/routes/+page.svelte -->
<script lang="ts">
  import { setupBridge, setupBridgeContext } from "$lib/setup-bridge.svelte";

  const bridge = setupBridgeContext.provide(setupBridge);
  let snapshot = $derived(bridge.snapshot);

  async function refresh() {
    await bridge.dispatch({ _tag: "Refresh" });
  }
</script>

<p>{bridge.status}</p>
{#if snapshot}
  <button onclick={refresh}>Refresh</button>
{/if}
```

Keep transient presentation state in Svelte. Supply `reduce` when bridge events
produce a newer snapshot; otherwise the initial/reconnect snapshots remain the
projection's state. Call `bridge.cancel(operationId)` to request an explicit
backend operation cancellation. Disposing a component is not a cancellation
command.

## Optional: Runic Translations

The `@runic-artifex/svelte/translations` subpath adapts a generated Runic
Translations `LocaleSource` without making the generated catalog a runtime
dependency. Install and configure the Runic Translations Vite plugin that
provides your `virtual:runic-translations/...` modules, then create one locale
context and provide a source per browser root or SSR component tree.

```ts
// src/lib/locale-context.svelte.ts
import { createLocaleContext } from "@runic-artifex/svelte/translations";

export const localeContext = createLocaleContext<"en" | "de">();
```

```svelte
<script lang="ts">
  import { createLocaleSource } from "virtual:runic-translations/app/runtime";
  import { localeContext } from "$lib/locale-context.svelte";

  let { data, children } = $props();
  const source = createLocaleSource({ initialLocale: data.locale });
  const locale = localeContext.provide(source);
</script>

<button onclick={() => locale.setLocale("de")}>Deutsch</button>
{@render children()}
```

A mutable source is updated directly. If navigation or application settings are
authoritative, pass `requestLocale` to `provide()` and update the source through
that callback. Initialize the client source from the root server load for
deterministic hydration. For URL routing, use
[`@runic-artifex/sveltekit/translations`](https://www.npmjs.com/package/@runic-artifex/sveltekit).

### Localization and visual-accessibility regression cases

`@runic-artifex/svelte/translations/testing` provides framework-neutral data
for a UI regression matrix. Mount each `localizationStressCases` entry with its
`localizationStressAttributes`, render every `pluralStressCounts` value, and
exercise each `visualAccessibilityStressScenarios` media query in the
application's own browser harness. The fixtures include an expanded pseudo
locale, an RTL string with a bidi-isolated identifier, and explicit forced
colors, high-contrast, and reduced-motion checks. They deliberately do not
prescribe application styling or replace real localized content.

```ts
import {
  localizationStressCases,
  pluralStressCounts,
  visualAccessibilityStressScenarios,
} from "@runic-artifex/svelte/translations/testing";

for (const stressCase of localizationStressCases) {
  // Mount this exact text in your app's visual test with lang/dir attributes.
}

for (const count of pluralStressCounts) {
  // Render a real pluralized message for each count.
}
```

## Hosted browser bootstrap

For the hosted SSR profile, render the server-provided bootstrap fingerprint in
`<meta name="runic-hosted-bootstrap">`, then call
`startRunicHostedBridgeAfterBootstrap` inside `onMount`. A matching marker starts
the supplied bridge; a mismatch records a fail-closed hydration state and leaves
the bridge stopped. The marker guards render consistency only—it is not an
authorization credential.

```svelte
<script lang="ts">
  import { onMount } from "svelte";
  import { startRunicHostedBridgeAfterBootstrap } from "@runic-artifex/svelte/hosted";

  let { data, bridge } = $props();

  onMount(() => {
    if (!startRunicHostedBridgeAfterBootstrap(data.bootstrap, bridge)) return;
    return () => { void bridge.dispose(); };
  });
</script>

<svelte:head>
  <meta name="runic-hosted-bootstrap" content={data.bootstrap.fingerprint} />
</svelte:head>
```

This does not create a new transport or authorization path. The existing bridge
remains controller-only; the W20 local FrameChannel/WebSocket boundary is not a
hosted service route.

## Optional: Effect workflows

If your controller exposes the Effect-aware Application Bridge contract, use
`createEffectSvelteApplicationBridge`. It retains the ordinary Promise methods
and exposes `effects`, `run`, `runExit`, and `createAction` over the controller's
existing managed runtime—no renderer-owned Effect runtime is created.

```ts
import { Effect } from "effect";
import { createEffectSvelteApplicationBridge } from "@runic-artifex/svelte";

const bridge = createEffectSvelteApplicationBridge(controller);
const refresh = bridge.createAction((_input, effects) =>
  effects.dispatch({ _tag: "Refresh" }).pipe(Effect.asVoid),
);

void refresh.run(undefined);
```

An action is latest-wins: beginning another invocation interrupts its preceding
Fiber, and disposing the bridge interrupts its actions. That interruption only
stops frontend work; use `cancel(operationId)` when the host operation itself
must be cancelled.

## Links, status, and support

- [Application Bridge guide](https://docs.runic-artifex.eu/application-bridge)
- [Package catalog and release status](https://docs.runic-artifex.eu/packages)
- [SvelteKit reference application](https://github.com/Runic-Artifex/runic-toolkit-examples/tree/main/samples/04-SvelteKitSetupApplication)
- [Issues and support](https://github.com/Runic-Artifex/runic-svelte/issues)
- [MIT License](https://github.com/Runic-Artifex/runic-svelte/blob/main/LICENSE)
