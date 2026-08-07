# Getting started

Enter the pinned development environment and run the repository gate:

```bash
nix develop
./eng/verify.sh
```

For application code, configure the Runic Artifex GitHub NuGet and npm package
feeds, then reference the smallest package set required by the chosen host and
frontend. The initial prerelease workflow publishes version-matched Toolkit
NuGet packages and the framework-neutral npm runtime:

- `RunicToolkit.ApplicationBridge`
- `RunicToolkit.ApplicationBridge.Generators`
- `RunicToolkit.Hosting.CsWebUi.ApplicationBridge`
- `@runic-artifex/application-bridge`
- `@runic-artifex/svelte` for Svelte 5 projects
- `@runic-artifex/sveltekit` for native-hosted SvelteKit projects
- `@runic-artifex/vite-plugin-runic-toolkit` for Vite 8 development and DevTools

Use [`runic-toolkit-examples`](https://github.com/Runic-Artifex/runic-toolkit-examples)
for runnable, package-only applications. React, Vue, and Angular exercise the
controller directly. Svelte uses the official Svelte-owned lifecycle projection
while retaining the same single Application Bridge runtime.
