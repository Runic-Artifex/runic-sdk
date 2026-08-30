# `@runic-artifex/sveltekit`

Ship a Runic Toolkit SvelteKit UI as a static/native application while keeping
SvelteKit in charge of routes, browser history, and page state. The package
provides the static adapter, SPA page options, and optional URL-based locale
routing.

## Install

```sh
npm install -D @runic-artifex/sveltekit@preview @runic-artifex/vite-plugin-runic@preview @vitejs/devtools @sveltejs/adapter-static
```

Install `@runic-artifex/svelte@preview` too when your pages project an
Application Bridge controller or use the locale context. These packages are
public npm previews; the `preview` tag selects the current preview release.

| Requirement | Supported range |
|---|---|
| SvelteKit | `>=2.53.0 <3` |
| Svelte | `>=5.46.4 <6` |
| Vite | `>=8 <9` |
| `@sveltejs/vite-plugin-svelte` | `>=7 <8` |
| `@sveltejs/adapter-static` | `>=3 <4` |
| `@runic-artifex/svelte` | `>=0.1.0-preview.0 <1` (optional peer) |
| `@runic-artifex/vite-plugin-runic` | `>=0.2.0-preview.1 <1` (optional peer; required for Runic Vite development integration) |

Use a Node.js version supported by your SvelteKit and Vite versions. Svelte 4
is not supported.

For the v0.2 Vite rename, replace the removed
`@runic-artifex/vite-plugin-runic-toolkit` package and `runicToolkit()` call
with `@runic-artifex/vite-plugin-runic` and `runic()`. Browser client imports
move from `virtual:runic-toolkit/client` to `virtual:runic/client`.

## Adapter and Vite setup

Complete this checklist before adding routes or localization:

1. Install the packages above in a SvelteKit 2 / Vite 8 application.
2. Register the Runic Vite plugin and the official Vite DevTools plugin.
3. Use `runicToolkitAdapter` in `svelte.config.js`.
4. Choose `prerendered` for static pages or `spa` for a host-driven shell. A
   multi-page prerender without `/` must name one emitted output file as
   `entrypoint`.
5. Build the app. The adapter writes `runic-toolkit.sveltekit.json` to the
   output directory so the native host can find the entrypoint.

```ts
// vite.config.ts
import { DevTools } from "@vitejs/devtools";
import { runic } from "@runic-artifex/vite-plugin-runic";
import { sveltekit } from "@sveltejs/kit/vite";
import { defineConfig } from "vite";

export default defineConfig({
  plugins: [DevTools({ visibility: "passive" }), runic(), sveltekit()],
});
```

```js
// svelte.config.js
import { runicToolkitAdapter } from "@runic-artifex/sveltekit";

export default {
  kit: {
    adapter: runicToolkitAdapter({ mode: "prerendered" }),
  },
};
```

For an entirely host-driven Desktop SPA, set `mode: "spa"`, enable
`desktop: true`, and set `kit.router.type` to `"hash"`. Desktop mode injects
the surface-relative bootstrap and rejects pathname routing because a
Desktop surface is mounted under a generated path namespace; hash routing keeps
the document URL at that relocatable surface root. SPA mode emits `200.html` by
default and rewrites SvelteKit's generated application asset URLs relative to
that root. Prerendered mode uses `/` only when it actually emitted `index.html`;
otherwise it uses the one emitted page or requires an explicit entrypoint.

```js
kit: {
  adapter: runicToolkitAdapter({ mode: "spa", desktop: true }),
  router: { type: "hash" },
}
```

For a prerendered application without a root page, select its entrypoint explicitly:

```js
adapter: runicToolkitAdapter({ mode: "prerendered", entrypoint: "setup.html" })
```

The `runicToolkitSpaPageOptions` export remains available for pathname-routed
static hosting profiles that use a different adapter. Hash-routed Runic Desktop
SPAs do not need page-level SSR or prerender flags because SvelteKit disables
both for that router.

```ts
// src/routes/+layout.ts
import { runicToolkitSpaPageOptions } from "@runic-artifex/sveltekit/page-options";

export const ssr = runicToolkitSpaPageOptions.ssr;
export const prerender = runicToolkitSpaPageOptions.prerender;
```

For a static prerendered site, use
`runicToolkitPrerenderedPageOptions` instead. In either mode, route application
commands in your code rather than mirroring a host process graph into a second
router.

## Optional: locale routing

The browser-safe `@runic-artifex/sveltekit/translations` subpath resolves a
locale from the URL, cookie, application setting, browser preference, then your
base locale. It keeps URLs canonical without setting process-global translation
state.

```ts
// src/lib/i18n.ts
import { createRunicLocaleRouting } from "@runic-artifex/sveltekit/translations";

export const routing = createRunicLocaleRouting({
  locales: ["en", "de"] as const,
  baseLocale: "en",
  baseLocalePath: "unprefixed",
});
```

With this policy, `/setup` is English and `/de/setup` is German. Choose
`baseLocalePath: "prefixed"` when `/en/setup` is also required, and set
`basePath` when the app is served below `/`.

Use the reroute hook so localized URLs select the same filesystem route, then
use the server handle to resolve, canonicalize, and expose the request locale:

```ts
// src/hooks.ts
import { createRunicLocaleReroute } from "@runic-artifex/sveltekit/translations";
import { routing } from "$lib/i18n";

export const reroute = createRunicLocaleReroute(routing);
```

```ts
// src/hooks.server.ts
import { createRunicLocaleHandle } from "@runic-artifex/sveltekit/translations";
import { routing } from "$lib/i18n";

export const handle = createRunicLocaleHandle(routing, {
  persistLocale: true,
  cookie: { name: "locale", path: "/", sameSite: "lax", httpOnly: true },
});
```

Declare `App.Locals.locale`, return it from the root server load with
`localeFromLocals(locals, routing)`, and initialize your Svelte locale source
from that value. Put `<html lang="%runic.locale%">` in `app.html` to receive
the request language without another resolver. Generated messages must still
receive the request locale explicitly.

For client navigation, call `synchronizeLocaleWithNavigation(source, routing)`
from the root layout and pass `createLocaleNavigation(routing)` as the locale
context's `requestLocale`. `gotoLocale`, `localizeUrl`, `delocalizeUrl`, and
`canonicalUrl` are available for selectors, links, metadata, and redirects.

Prerendering requires every localized public URL to be listed as an entry or
discoverable through links. A deployed static asset does not execute
`hooks.server`; SPA mode has no server handle, so initialize from the URL or
browser instead.

## Hosted SSR session projection

`@runic-artifex/sveltekit/hosted` is the server-only W30 helper for the D008
profile. It derives the locale from request locals, forwards exactly one opaque
`__Host-runic-session` cookie to a caller-supplied C# session loader, and returns
only the C# sanitized session projection plus a deterministic hydration marker.
It does not validate or mint a cookie, process OIDC, carry a bearer token, add
CORS, or create a browser bridge.

```ts
// src/routes/[locale]/+page.server.ts
import { createRunicHostedSsrLoad } from "@runic-artifex/sveltekit/hosted";
import { routing } from "$lib/i18n";
import { loadCSharpSession } from "$lib/csharp-session.server";

export const load = createRunicHostedSsrLoad(routing, {
  loadSession: loadCSharpSession,
});
```

The loader receives only `{ path: "/runic/service/session", cookie }`. Route
it to the C# service using that cookie alone and reject unavailable, malformed,
or unauthorized responses. The helper fails closed if the cookie is missing or
duplicated, C# denies it, or the returned projection contains unbounded or
noncanonical facts. Keep the locale handle's URL-first/cookie-second routing
policy and pass the resolved locale explicitly to every generated translation
call. SSR, client navigation, and browser bootstrap therefore share one
request-scoped locale without process-global state.

## Links, status, and support

- [Application Bridge guide](https://docs.runic-artifex.eu/application-bridge)
- [Package catalog and release status](https://docs.runic-artifex.eu/packages)
- [SvelteKit reference application](https://github.com/Runic-Artifex/runic-toolkit-examples/tree/main/samples/04-SvelteKitSetupApplication)
- [Issues and support](https://github.com/Runic-Artifex/runic-svelte/issues)
- [MIT License](https://github.com/Runic-Artifex/runic-svelte/blob/main/LICENSE)
