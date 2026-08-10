# `@runic-artifex/sveltekit`

Static/native SvelteKit adapter for Runic Toolkit applications.

```js
// svelte.config.js
import { runicToolkitAdapter } from "@runic-artifex/sveltekit";

export default {
  kit: {
    adapter: runicToolkitAdapter({ mode: "prerendered" }),
  },
};
```

Use `mode: "spa"` for an entirely host-driven UI. It generates `200.html` by
default and records that entrypoint in `runic-toolkit.sveltekit.json`. The
prerendered mode keeps SvelteKit SSR/prerendering available and initializes the
native bridge only after hydration.

Import route constants from the browser-safe subpath so client compilation does
not traverse the Node-only adapter implementation:

```ts
import { runicToolkitSpaPageOptions } from "@runic-artifex/sveltekit/page-options";

export const ssr = runicToolkitSpaPageOptions.ssr;
export const prerender = runicToolkitSpaPageOptions.prerender;
```

Add `runicToolkit()` and the official `DevTools()` plugin to `vite.config.ts`.
The SvelteKit package does not duplicate their development runtime.

SvelteKit remains authoritative for URLs, history, and page state even when the
host uses Runic Flow. Map routes to named application commands in application
code; do not mirror a Flow process graph into a second router owned by this
adapter.

## Runic Translations routing

Translation routing is exposed from the browser-safe
`@runic-artifex/sveltekit/translations` subpath. Define the locale policy once:

```ts
// src/lib/i18n.ts
import { createRunicLocaleRouting } from "@runic-artifex/sveltekit/translations";

export const routing = createRunicLocaleRouting({
  locales: ["en", "de"] as const,
  baseLocale: "en",
  baseLocalePath: "unprefixed",
});
```

With `unprefixed`, `/setup` is English and `/de/setup` is German. Use
`baseLocalePath: "prefixed"` when both `/en/setup` and `/de/setup` are desired.
Set `basePath` when the SvelteKit application is mounted below `/`.

Use the pure reroute hook so both public URLs select the same filesystem route:

```ts
// src/hooks.ts
import { createRunicLocaleReroute } from "@runic-artifex/sveltekit/translations";
import { routing } from "$lib/i18n";

export const reroute = createRunicLocaleReroute(routing);
```

The server handle resolves one locale for one request, writes it to
`event.locals.locale`, applies canonical redirects, and optionally persists it:

```ts
// src/hooks.server.ts
import { createRunicLocaleHandle } from "@runic-artifex/sveltekit/translations";
import { routing } from "$lib/i18n";

export const handle = createRunicLocaleHandle(routing, {
  persistLocale: true,
  cookie: { name: "locale", path: "/", sameSite: "lax", httpOnly: true },
});
```

Resolution defaults to URL, cookie, application setting, browser preference,
then the configured base locale. Change `resolutionOrder` when application
settings should win. URL prefixes and preference tags are matched
case-insensitively with parent fallback (`de-DE` can select `de`). Unsupported
values fall through to the next strategy; an unsupported first path segment is
treated as an application route, not silently removed.

Declare the local and return it from the root server load for hydration:

```ts
// src/app.d.ts
declare global {
  namespace App {
    interface Locals { locale: "en" | "de" }
  }
}
export {};
```

```ts
// src/routes/+layout.server.ts
import { localeFromLocals } from "@runic-artifex/sveltekit/translations";
import { routing } from "$lib/i18n";

export const load = ({ locals }) => ({ locale: localeFromLocals(locals, routing) });
```

Put the default `%runic.locale%` token in `app.html` to receive the request
locale without a second resolver:

```html
<html lang="%runic.locale%">
```

The token can be changed or disabled with `htmlLanguageToken`. Generated
messages must still receive the request locale explicitly through
`{ locale: data.locale }`; the handle never installs process-global state.

### Client navigation

Browser lifecycle helpers have their own subpath so server hooks do not import
`$app/navigation`:

```svelte
<script lang="ts">
  import { createLocaleSource } from "virtual:runic-translations/app/runtime";
  import {
    createLocaleNavigation,
    synchronizeLocaleWithNavigation,
  } from "@runic-artifex/sveltekit/translations/navigation";
  import { localeContext } from "$lib/locale-context";
  import { routing } from "$lib/i18n";

  let { data, children } = $props();
  const source = createLocaleSource({ initialLocale: data.locale });
  synchronizeLocaleWithNavigation(source, routing);
  localeContext.provide(source, {
    requestLocale: createLocaleNavigation(routing),
  });
</script>

{@render children()}
```

`routing.localizeUrl(url, locale)`, `delocalizeUrl`, and `canonicalUrl` preserve
the query and hash and can be used for links, language selectors, canonical
metadata, and redirects. `gotoLocale` is available for direct event handlers.

### Deployment and prerendering

- SSR works with Node, serverless, and edge adapters that implement SvelteKit's
  standard request, cookie, and response APIs. Concurrent requests share no
  locale state.
- SPA mode uses the same URL and navigation helpers, but there is no server
  handle; initialize from the URL/browser instead.
- Prerendering is supported, but every localized public URL must be present in
  SvelteKit's prerender entries or discoverable links. A deployed prerendered
  asset does not execute `hooks.server`.
- The adapter does not translate route slugs, discover localized entries,
  negotiate locales at a CDN, or manage service-worker caches.
