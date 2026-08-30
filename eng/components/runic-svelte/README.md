![Runic Artifex banner](.github/assets/brand/banner.png)

# Runic Svelte

Build a Svelte 5 or SvelteKit interface over one Runic Toolkit Application
Bridge controller. Runic Svelte turns its authoritative snapshots, events, and
lifecycle into Svelte-native state without taking over your routes, protocol,
or application model.

## Choose a package

| Package | Choose it when | Install |
|---|---|---|
| [`@runic-artifex/svelte`](https://www.npmjs.com/package/@runic-artifex/svelte) | Your Svelte 5 component tree needs Application Bridge lifecycle, rune state, or optional translation/Effect helpers. | `npm install @runic-artifex/svelte@preview` |
| [`@runic-artifex/sveltekit`](https://www.npmjs.com/package/@runic-artifex/sveltekit) | Your SvelteKit application needs static/native output, SPA page options, or URL-based locale routing. | `npm install -D @runic-artifex/sveltekit@preview` |

Both packages are public npm previews. Use the `preview` tag to follow the
current compatible preview release; check [release status](https://docs.runic-artifex.eu/releases)
before upgrading a production application.

## Compatibility

| Package | Required peer compatibility | Optional integration |
|---|---|---|
| `@runic-artifex/svelte` | Svelte `>=5.46.4 <6`, Effect `>=3.22.1 <4` | Runic Toolkit Application Bridge `>=0.1.0-preview.30.1 <1`; Vite integration is optional through `/vite` |
| `@runic-artifex/sveltekit` | SvelteKit `>=2.53.0 <3`, Svelte `>=5.46.4 <6`, Vite `>=8 <9`, `@sveltejs/vite-plugin-svelte` `>=7 <8`, `@sveltejs/adapter-static` `>=3 <4` | `@runic-artifex/svelte` and `@runic-artifex/vite-plugin-runic`; use `/vite` only when Vite diagnostics are wanted. |

Use a Node.js version supported by your SvelteKit and Vite versions. These are
Svelte 5-only packages: there is no Svelte 4 build or legacy-store adapter.

### Vite v0.2 migration

The Vite integration is now `@runic-artifex/vite-plugin-runic`. Replace the
removed `@runic-artifex/vite-plugin-runic-toolkit` install and its
`runicToolkit()` configuration with `runic()`. Its client module is now
`virtual:runic/client`; the new plugin reports `RUNICP001` when an old virtual
module import remains. The `/vite` entry is still isolated, so translation-only
Svelte consumers do not load the Vite peer.

## Start with the Application Bridge

Install the framework-neutral bridge beside the Svelte projection:

```sh
npm install @runic-artifex/application-bridge@preview @runic-artifex/svelte@preview
```

Create the Application Bridge controller at application bootstrap, then project
that controller once into Svelte. The first mounted provider starts it,
initializes its snapshot, and sends `uiReady` and `uiRendered`; unmounting
disposes it. Your controller remains the authority for transport, reconnect,
commands, and events.

```ts
// src/lib/application-bridge.svelte.ts
import {
  createApplicationBridgeContext,
  createSvelteApplicationBridge,
} from "@runic-artifex/svelte";
import { controller } from "./controller";

export const bridge = createSvelteApplicationBridge(controller, {
  reduce: (snapshot, event) =>
    event._tag === "SnapshotReplaced" ? event.snapshot : snapshot,
});

export const bridgeContext = createApplicationBridgeContext();
```

```svelte
<!-- src/App.svelte -->
<script lang="ts">
  import { bridge, bridgeContext } from "$lib/application-bridge.svelte";

  const application = bridgeContext.provide(bridge);
  let snapshot = $derived(application.snapshot);
</script>

<p>{application.status}</p>
{#if snapshot}
  <button onclick={() => application.dispatch({ _tag: "Refresh" })}>
    Refresh
  </button>
{/if}
```

The command and event types in this example belong to your Application Bridge
contract. See the package README for lifecycle options, translations, and
Effect actions.

## Add SvelteKit only when it owns the page

Use `@runic-artifex/sveltekit` for the static/native adapter and SvelteKit
route settings. It does not replace SvelteKit's ownership of URLs, browser
history, or page state. The [SvelteKit package README](https://github.com/Runic-Artifex/runic-svelte/tree/main/packages/sveltekit)
has the adapter and Vite setup checklist, including SPA and prerendered modes.

## Further reading and support

- [Application Bridge guide](https://docs.runic-artifex.eu/application-bridge)
- [Package catalog](https://docs.runic-artifex.eu/packages)
- [SvelteKit reference application](https://github.com/Runic-Artifex/runic-toolkit-examples/tree/main/samples/04-SvelteKitSetupApplication)
- [Issues and support](https://github.com/Runic-Artifex/runic-svelte/issues)

Runic Svelte is licensed under the [MIT License](LICENSE).
