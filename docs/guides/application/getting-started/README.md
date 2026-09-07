# Getting started

From the SDK root, enter the pinned development environment and run the application
CI suite locally (Linux with Docker or rootless Podman):

```bash
nix develop
bun run ci --job managed --matrix suite:application
```

For application code, configure the Runic Artifex GitHub NuGet and npm package
feeds, then reference the smallest package set required by the chosen host and
frontend. The initial prerelease workflow publishes version-matched Toolkit
NuGet packages and the framework-neutral npm runtime:

- `Runic.Application.Bridge`
- `Runic.Application.Bridge.Generators`
- `Runic.Application.Desktop`
- `Runic.Desktop`
- `@runic-artifex/application-bridge`
- `@runic-artifex/svelte` for Svelte 5 projects
- `@runic-artifex/sveltekit` for native-hosted SvelteKit projects
- `@runic-artifex/vite-plugin-runic` for Vite 8 development and DevTools

Use [`runic-toolkit-examples`](https://github.com/Runic-Artifex/runic-toolkit-examples)
for runnable, package-only applications. React and Vue exercise the controller
directly. Angular uses the official controller-owned DI and signal projection;
Svelte uses the official Svelte-owned lifecycle projection
while retaining the same single Application Bridge runtime.
