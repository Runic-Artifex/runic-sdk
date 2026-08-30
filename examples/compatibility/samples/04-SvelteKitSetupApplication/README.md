# 04 — SvelteKit Setup Application

Use Svelte 5 runes and SvelteKit for the presentation layer while keeping the
same validated Application Bridge over Runic Desktop's presentation transport.

## Prerequisites

- .NET SDK 10.0.302.
- Node.js 24.18 and npm 11.16. Run `npm ci` from the repository root.
- A supported browser or WebView for the interactive window. The smoke check is
  headless.

## Run and verify

From the repository root:

```bash
npm ci
npm run verify
dotnet run --project samples/04-SvelteKitSetupApplication
```

The final command opens a **Runic Toolkit · SvelteKit Setup** window. The
SvelteKit adapter emits the native `index.html` SPA fallback and routes deep
links back to the client application.

Run the deterministic host-only smoke check with:

```bash
dotnet run --project samples/04-SvelteKitSetupApplication -- --smoke-test
```

It exits with code 0 and reports successful completion, cancellation, failure,
and recovery. For a browser-only frontend loop, run:

```bash
npm run dev:mock --workspace @runic-artifex/sveltekit-setup-application
```

## What stays where

Svelte runes own component lifecycle and presentation state. Runic Desktop owns
the local HTTP/WebSocket presentation boundary and browser/WebView selection.
The Application Bridge remains responsible for protocol validation, sessions,
revisions, events, operations, cancellation, and reconnect recovery. Runic
Assets streams the embedded SPA with range and cache semantics, and the Runic
Vite plugin preserves the projection across HMR and contributes bounded
diagnostics through Vite DevTools.

## Next

Continue to [Runic Translations Setup](../05-RunicTranslationsSetup/) to add
typed, locale-aware text generated for both .NET and ESM.

[Svelte and SvelteKit integration documentation](https://docs.runic-artifex.eu/application-bridge) ·
[Examples](https://github.com/Runic-Artifex/runic-toolkit-examples/tree/main/samples/04-SvelteKitSetupApplication) ·
[Issues](https://github.com/Runic-Artifex/runic-toolkit-examples/issues)
