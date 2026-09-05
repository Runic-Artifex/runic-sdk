> This package is now developed in the [Runic SDK workspace](../../../README.md). Use its root build and verification commands.

![Runic Artifex banner](https://raw.githubusercontent.com/Runic-Artifex/runic-vite/main/.github/assets/brand/banner.png)

# Runic Vite

Develop Runic browser and desktop applications with Vite: retain
long-lived resources through HMR, surface Application Bridge diagnostics, and
use the official Vite DevTools dock when you want it.

## Install

```sh
npm install -D @runic-artifex/vite-plugin-runic@preview @runic-artifex/application-bridge-tooling@preview vite@^8
```

The package is a public npm preview. The `preview` tag selects the current
preview release; check the [package catalog and release status](https://docs.runic-artifex.eu/packages)
before upgrading. It requires Node.js `>=24.18.0` and Vite `^8.0.0`.

For the optional diagnostics dock, also install Vite DevTools:

```sh
npm install -D @vitejs/devtools
```

Choose this plugin for a Runic application built with Vite. It is the
development integration, not an Application Bridge runtime or a SvelteKit
adapter; see the [Application Bridge guide](https://docs.runic-artifex.eu/application-bridge)
or [`@runic-artifex/sveltekit`](https://www.npmjs.com/package/@runic-artifex/sveltekit)
when those are what your application needs.

## Configure Vite

Add the plugin to `vite.config.ts`. `DevTools` is optional; remove it (or set
`devtools: false`) if your project does not use `@vitejs/devtools`.

```ts
import { DevTools } from "@vitejs/devtools";
import { runic } from "@runic-artifex/vite-plugin-runic";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [
    DevTools({ visibility: "passive" }),
    runic({
      desktop: true,
      applicationBridge: true,
      contract: {
        identity: "example.desktop-app",
        version: "1",
      },
    }),
  ],
});
```

Then import the virtual client once from your browser entry (for example,
`src/main.ts`). This import is required, including for native or backend-hosted
HTML, because it connects the browser-side helpers to the Vite integration.

```ts
import { createRunicDevtoolsObserver } from "virtual:runic/client";

const observer = createRunicDevtoolsObserver();

observer.state({
  connection: { state: "connecting", transport: "cs-webui" },
});

observer.trace({ kind: "connection", label: "Application Bridge connecting" });
```

Pass `observer` to the code that owns bridge state and events. A resource
retained with `preserveRunicHmrResource` is reused across HMR updates; call
`disposeRunicHmrResource` when it should be released explicitly.

If TypeScript cannot resolve the virtual module, include its declaration in a
project declaration file such as `src/vite-env.d.ts`:

```ts
/// <reference types="@runic-artifex/vite-plugin-runic/virtual" />
```

## Options and virtual client

`runic(options)` accepts the following options:

| Option | Default | Purpose |
| --- | --- | --- |
| `contract` | none | Initial diagnostic contract metadata: `identity`, `version`, and `fingerprint`. |
| `applicationBridge` | `false` | Generates Bridge IR and the fingerprint facade at startup/build and watches imported contract modules. Pass `{ source, ir, facade }` for non-conventional paths. |
| `devtools` | `"auto"` | Enables DevTools injection when `@vitejs/devtools` is installed. Set `false` to disable it; `true` also requests it when available. |
| `devtoolsVisibility` | `"passive"` | DevTools injector visibility: `"normal"`, `"passive"`, or `"hidden"`. |
| `maxTimelineEntries` | `200` | Maximum retained diagnostic timeline entries (clamped to `1`–`500`). |
| `desktop` | `false` | Injects `./runic-desktop.js` and emits relocatable production assets; pass an absolute Desktop bootstrap URL when Vite owns the development page origin. |

The plugin provides one virtual module, `virtual:runic/client`, with
these browser helpers:

| Export | Use |
| --- | --- |
| `createRunicDevtoolsObserver()` | Creates `state` and `trace` functions for Application Bridge diagnostics. |
| `createRunicDiagnosticReporter(source)` | Creates a browser reporter for `"application-bridge"`, `"assets"`, or `"translations"`. |
| `reportRunicDiagnostic(entry)` | Sends one source-tagged diagnostic summary directly. |
| `reportRunicState(state)` / `traceRunicEvent(entry)` | Sends state or a timeline entry directly. |
| `preserveRunicHmrResource(key, create)` | Keeps a resource alive across HMR updates. |
| `disposeRunicHmrResource(key, dispose?)` | Releases a retained resource; by default, calls its `dispose()` method when present. |

During development, the optional dock shows contract, connection, operation,
and recent timeline state. Diagnostic data is bounded and removes detail fields
named for secrets, credentials, capabilities, passwords, paths, URLs, frames,
or stacks before it is displayed.

Browser and server reporters accept only a bounded summary: primitive detail
values (`string`, `number`, `boolean`, or `null`) and a short label. Sensitive
or path-like text is redacted before browser HMR transport and sanitized again
by the server. The server assigns timeline IDs and timestamps; callers should
not rely on supplied IDs or timestamps being retained.

Companion Vite integrations can report server-side summaries through the
plugin's `diagnostics` seam. It is intentionally summary-only: Application
Bridge, Assets, and Translations remain the authority for their own contracts,
artifacts, and diagnostic models.

```ts
const plugin = runic();

export default defineConfig({
  plugins: [plugin],
});

// Pass this to a companion integration; it can only submit summaries.
runic.diagnostics.report({
  source: "assets",
  kind: "event",
  label: "Assets refreshed",
});
```

## Development and production behavior

The DevTools client is injected only for `vite serve` when DevTools is
available. It is not included in production builds. The virtual-client imports
remain safe in production: without Vite HMR, reporting functions are no-ops.

With `applicationBridge` enabled, generation failures fail production builds and
appear in the development overlay while the last good IR and facade remain in
place. A successful wire change triggers a full reload instead of ordinary HMR.

Runic Desktop can coexist with Vite's own development server and HMR socket:

```ts
runic({
  desktop: {
    bootstrapUrl: "http://127.0.0.1:43123/runic-desktop.js",
  },
});
```

The Desktop surface must admit the Vite origin through its typed security
policy. The plugin injects only the bootstrap script; Vite retains development
server, module graph, build, proxy, and HMR ownership. For production content
served by the Desktop surface itself, `desktop: true` injects the relative
bootstrap URL.

## v0.2 migration

`@runic-artifex/vite-plugin-runic-toolkit` was replaced by
`@runic-artifex/vite-plugin-runic`; it is not a forwarding package. Replace the
package install, `runicToolkit()` import and call with `runic()`, and every
`virtual:runic-toolkit/client` import with `virtual:runic/client`. The new
plugin deliberately rejects the former virtual module with
`RUNICP001` and this exact remediation.

For a working SvelteKit integration, see the [reference application](https://github.com/Runic-Artifex/runic-toolkit-examples/tree/main/samples/04-SvelteKitSetupApplication).

## Troubleshooting

- **Cannot resolve `virtual:runic/client`:** register `runic()`
  in the Vite configuration used by the active command, then restart Vite.
- **TypeScript cannot find the virtual module:** add the triple-slash reference
  shown above and ensure your project includes that declaration file.
- **No Runic dock:** install `@vitejs/devtools`, add `DevTools(...)`,
  and do not set `devtools: false`. The plugin works without the dock.
- **The dock has no application activity:** import the virtual client in the
  browser entry and report state or trace events from the code that owns your
  Application Bridge.

## Support and license

- [Runic Application documentation](https://docs.runic-artifex.eu/products/runic-application)
- [npm package](https://www.npmjs.com/package/@runic-artifex/vite-plugin-runic)
- [Issues and support](https://github.com/Runic-Artifex/runic-vite/issues)
- [MIT License](https://github.com/Runic-Artifex/runic-vite/blob/main/LICENSE)
