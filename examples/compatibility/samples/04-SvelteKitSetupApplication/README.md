# SvelteKit Setup Application

This package-only sample is the authoritative native Svelte 5 and SvelteKit
vertical. It consumes the separately published Application Bridge, Svelte,
SvelteKit, and Vite integrations.

- Svelte runes own presentation state and component lifecycle.
- The Application Bridge remains the only protocol and Effect runtime.
- The SvelteKit adapter emits an explicit `index.html` CS-WebUI SPA fallback and
  `runic-toolkit.sveltekit.json`. Conventional static hosts can retain the
  adapter's `200.html` default.
- A catch-all route maps the native startup document and deep links back to the
  client application instead of SvelteKit's 404 page.
- The Runic Vite plugin contributes bounded diagnostics through the official
  experimental Vite DevTools API and preserves the bridge across HMR.

```bash
npm run verify
dotnet build samples/04-SvelteKitSetupApplication/SvelteKitSetupApplication.csproj
dotnet run --project samples/04-SvelteKitSetupApplication/SvelteKitSetupApplication.csproj -- --smoke-test
```
